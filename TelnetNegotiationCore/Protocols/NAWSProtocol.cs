using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// NAWS (Negotiate About Window Size) protocol plugin - RFC 1073
/// Implements http://www.faqs.org/rfcs/rfc1073.html
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnNAWS"/> to set up
/// the callback that will handle window size changes if you need to be notified of client
/// window size updates.
/// </remarks>
[RequiredMethod("OnNAWS", Description = "Configure the callback to handle window size changes (optional but recommended)")]
public class NAWSProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_wontNaws = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NAWS };
    private static readonly byte[] s_doNaws = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS };
    private static readonly byte[] s_willNaws = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS };

    private bool _willingToDoNAWS = false;

    /// <summary>
    /// True once NAWS is enabled with the peer (for a client, after the server sends DO NAWS).
    /// The client must not emit an SB NAWS subnegotiation until this is true; sending one
    /// unsolicited desyncs a strict server's telnet parser (RFC 1073).
    /// </summary>
    public bool WindowSizeReportingEnabled => _willingToDoNAWS;

    private Func<int, int, ValueTask>? _onNAWSNegotiated;

    /// <summary>
    /// Sets the callback that is invoked when NAWS negotiation is complete.
    /// </summary>
    /// <param name="callback">The callback to handle window size changes</param>
    /// <returns>This instance for fluent chaining</returns>
    public NAWSProtocol OnNAWS(Func<int, int, ValueTask>? callback)
    {
        _onNAWSNegotiated = callback;
        return this;
    }



    /// <summary>
    /// The largest window dimension RFC 1073 can carry: both fields are 16-bit unsigned, which is
    /// the option's entire reason for existing — <i>"the 253 character height and width limitation
    /// is too low so the new option has a limit of 65535 characters"</i>.
    /// </summary>
    public const int MaxWindowDimension = ushort.MaxValue;

    /// <summary>
    /// Reports this side's window size to the peer as <c>IAC SB NAWS WIDTH[1] WIDTH[0] HEIGHT[1]
    /// HEIGHT[0] IAC SE</c> (RFC 1073). Does nothing until the peer has enabled NAWS — see
    /// <see cref="WindowSizeReportingEnabled"/>.
    /// </summary>
    /// <param name="width">Window width, 0 to <see cref="MaxWindowDimension"/></param>
    /// <param name="height">Window height, 0 to <see cref="MaxWindowDimension"/></param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A dimension is outside 0 to <see cref="MaxWindowDimension"/>. Truncating it into the two
    /// bytes the wire has would report a size that was never asked for: 65536 would go out as 0.
    /// </exception>
    public async ValueTask SendWindowSizeAsync(int width, int height)
    {
        ThrowIfNotAWindowDimension(width, nameof(width));
        ThrowIfNotAWindowDimension(height, nameof(height));

        // A client must not emit SB NAWS until the server has enabled it with DO NAWS; sending one
        // unsolicited desyncs a strict server's telnet parser and can make it swallow the following
        // line (RFC 1073).
        if (!IsEnabled || !_willingToDoNAWS)
        {
            return;
        }

        // High byte first (network byte order). Shifting explicitly is endian-independent, so this
        // is one code path on every target framework.
        //
        // RFC 1073: "As required by the Telnet protocol, any occurrence of 255 in the subnegotiation
        // must be doubled to distinguish it from the IAC character (which has a value of 255)."
        // A dimension byte of 255 is an ordinary terminal size, not a corner case - a 255-column
        // window, or any height/width whose high or low byte happens to be 255. Sent raw, the peer
        // reads that byte as the IAC that ends the subnegotiation and the rest of the stream desyncs.
        var dimensions = Context.Interpreter.TelnetSafeBytes(new byte[]
        {
            (byte)(width >> 8), (byte)width,
            (byte)(height >> 8), (byte)height
        });

        await Context.SendNegotiationAsync((byte[])
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
            .. dimensions,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);
    }

    private static void ThrowIfNotAWindowDimension(int value, string parameterName)
    {
        if (value is < 0 or > MaxWindowDimension)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"RFC 1073 window dimensions are 16-bit unsigned: must be between 0 and {MaxWindowDimension}.");
        }
    }

    /// <summary>
    /// Currently known Client Height (defaults to 24)
    /// </summary>
    public int ClientHeight { get; private set; } = 24;

    /// <summary>
    /// Currently known Client Width (defaults to 78)
    /// </summary>
    public int ClientWidth { get; private set; } = 78;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(NAWSProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "NAWS (Negotiate About Window Size)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance and the SB NAWS subnegotiation are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/> and <see cref="OnWindowSizeAsync"/>); this hook survives
    /// only to register the initial negotiation, a cross-cutting mechanism independent of which
    /// machine drives byte processing.
    /// </remarks>
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        // RFC 1073: NAWS describes the CLIENT's window. The client offers it with WILL NAWS and
        // the server enables it with DO NAWS. Only then may the client send SB NAWS. Previously
        // this initial negotiation was ungated, so a client also sent DO NAWS (asking the server
        // for a window it has no concept of) and then an unsolicited SB NAWS — which desynced a
        // strict server's telnet parser and swallowed the following line (observed vs SharpMUSH).
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await RequestNAWSAsync(context));
        }
        else
        {
            context.RegisterInitialNegotiation(async () => await OfferNAWSAsync(context));
        }
    }

    private async ValueTask OfferNAWSAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Offering to report window size (WILL NAWS)");
        await context.SendNegotiationAsync(s_willNaws);
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("NAWS Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("NAWS Protocol enabled - requesting window size");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("NAWS Protocol disabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync() => default(ValueTask);

    #region State Machine Handlers

    private async ValueTask ServerWontNAWSAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing refusing to send NAWS, this is a Server!");
        await context.SendNegotiationAsync(s_wontNaws);
    }

    private async ValueTask RequestNAWSAsync(IProtocolContext context)
    {
        if (!_willingToDoNAWS)
        {
            context.Logger.LogDebug("Requesting NAWS details from Client");
            await context.SendNegotiationAsync(s_doNaws);
            _willingToDoNAWS = true;
        }
    }

    /// <summary>
    /// What arriving at each of Willing/Refusing/Do/Dont for NAWS does, independent of which machine got
    /// there — the four branches <see cref="ConfigureStateMachine"/> wires as WillDoNAWS/WontDoNAWS/DoNAWS/DontNAWS.
    /// </summary>
    /// <param name="verb">The verb byte: WILL, WONT, DO or DONT.</param>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        switch (verb)
        {
            case (byte)Trigger.WILL:
                // The peer just offered WILL NAWS. Whether we already sent DO (server mode asks eagerly at
                // connection start) or are about to (RequestNAWSAsync itself), both halves of the exchange
                // are on the wire the moment this fires -- so this is the genuine completion point.
                await RequestNAWSAsync(context);
                await OnNegotiatedAsync(true);
                break;
            case (byte)Trigger.WONT:
                _willingToDoNAWS = false;
                await OnNegotiatedAsync(false);
                break;
            case (byte)Trigger.DO when context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server:
                // A client asking the server to report a window size the server does not have.
                await ServerWontNAWSAsync(context);
                break;
            case (byte)Trigger.DO:
                _willingToDoNAWS = true;
                await OnNegotiatedAsync(true);
                break;
            case (byte)Trigger.DONT when context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server:
                context.Logger.LogDebug("Client won't do NAWS - do nothing");
                break;
            case (byte)Trigger.DONT:
                // Server refused NAWS -- must not report window size.
                _willingToDoNAWS = false;
                context.Logger.LogDebug("Server won't do NAWS - do nothing");
                await OnNegotiatedAsync(false);
                break;
        }
    }

    /// <summary>
    /// What a window size report does, once its two numbers are known — independent of how they were
    /// read, so the same call serves the Stateless configuration above and the generated machine.
    /// </summary>
    internal async ValueTask OnWindowSizeAsync(int width, int height, IProtocolContext context)
    {
        ClientWidth = width;
        ClientHeight = height;

        context.Logger.LogDebug("Negotiated for: {clientWidth} width and {clientHeight} height", ClientWidth, ClientHeight);

        // The interpreter carries the same pair for consumers reading TelnetInterpreter.ClientWidth
        // and ClientHeight.
        context.Interpreter.ClientWidth = ClientWidth;
        context.Interpreter.ClientHeight = ClientHeight;

        // Call the user callback if registered
        if (_onNAWSNegotiated != null)
        {
            await _onNAWSNegotiated(ClientHeight, ClientWidth);
        }
    }

    #endregion
}
