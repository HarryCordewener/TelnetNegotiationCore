using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Suppress Go-Ahead protocol plugin
/// Allows half-duplex operation without requiring GA after each transmission
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnPrompt"/> to set up
/// the callback that will handle prompts if you need to be notified when prompts are received.
/// </remarks>
[RequiredMethod("OnPrompt", Description = "Configure the callback to handle prompt events (optional but recommended)")]
public class SuppressGoAheadProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willSga = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD };
    private static readonly byte[] s_wontSga = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.SUPPRESSGOAHEAD };
    private static readonly byte[] s_doSga = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD };
    private static readonly byte[] s_dontSga = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD };

    // This end's own SUPPRESS-GO-AHEAD state: whether we have agreed to suppress our own outbound GA.
    private bool _ownGoAheadSuppressed;

    // The peer's SUPPRESS-GO-AHEAD state: whether the peer has agreed to suppress its own outbound GA.
    private bool _peerGoAheadSuppressed;

    private Func<ValueTask>? _onPromptReceived;

    /// <summary>
    /// Sets the callback that is invoked when a prompt is received (Suppress Go-Ahead marker).
    /// </summary>
    /// <remarks>
    /// Runs on the byte-processing loop — the same thread EOR's and Packet Patch's prompt callbacks
    /// run on, so a handler shared across all three (as <c>AddDefaultMUDProtocols</c> does when
    /// given one) needs no thread-safety of its own on that account.
    /// </remarks>
    /// <param name="callback">The callback to handle prompts</param>
    /// <returns>This instance for fluent chaining</returns>
    public SuppressGoAheadProtocol OnPrompt(Func<ValueTask>? callback)
    {
        _onPromptReceived = callback;
        return this;
    }

    /// <summary>
    /// Whether the peer has agreed to suppress its own outbound Go-Ahead -- the direction that
    /// decides whether an inbound <c>IAC GA</c> still means a prompt.
    /// </summary>
    public bool IsGoAheadSuppressed => _peerGoAheadSuppressed;

    /// <summary>
    /// Whether <em>this</em> end has agreed to suppress its own outbound Go-Ahead -- the direction
    /// <c>TelnetInterpreter.PromptTerminator</c> needs when deciding whether an outbound prompt may
    /// end with <c>IAC GA</c>. Independent of <see cref="IsGoAheadSuppressed"/>, the peer's
    /// direction.
    /// </summary>
    public bool SuppressesOutboundGoAhead => _ownGoAheadSuppressed;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(SuppressGoAheadProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Suppress Go-Ahead";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();
    // Note: SuppressGA and EOR often work together as fallbacks
    // This could be expressed as a soft dependency if needed

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance is wired to the generated machine (see <see cref="OnPeerNegotiatedAsync"/>);
    /// this hook survives only to register the server's initial offer, a cross-cutting mechanism
    /// independent of which machine drives byte processing.
    /// </remarks>
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await WillingSuppressGAAsync(context));
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Suppress Go-Ahead Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Suppress Go-Ahead Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Suppress Go-Ahead Protocol disabled");
        return default(ValueTask);
    }

    /// <summary>
    /// Manually suppresses this end's own outbound Go-Ahead, without a negotiation round-trip.
    /// </summary>
    public ValueTask SuppressGoAheadAsync()
    {
        if (!IsEnabled)
            return default(ValueTask);

        _ownGoAheadSuppressed = true;
        Context.Logger.LogInformation("Go-Ahead suppression enabled");
        return default(ValueTask);
    }

    /// <summary>
    /// Manually resumes this end's own outbound Go-Ahead, without a negotiation round-trip.
    /// </summary>
    public ValueTask EnableGoAheadAsync()
    {
        if (!IsEnabled)
            return default(ValueTask);

        _ownGoAheadSuppressed = false;
        Context.Logger.LogInformation("Go-Ahead suppression disabled (GA active)");
        return default(ValueTask);
    }

    /// <summary>
    /// Checks if prompting should use EOR as fallback
    /// </summary>
    public bool ShouldUseEORFallback()
    {
        if (!IsEnabled || !IsGoAheadSuppressed)
            return false;

        // Check if EOR plugin is available and enabled
        var eorPlugin = Context.GetPlugin<EORProtocol>();
        return eorPlugin != null && eorPlugin.IsEnabled;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _ownGoAheadSuppressed = false;
        _peerGoAheadSuppressed = false;
        return default(ValueTask);
    }

    #region State Machine Handlers

    /// <summary>
    /// A bare <c>IAC GA</c> arrived from the server: the RFC 854 Go-Ahead signal, which is a prompt
    /// boundary unless RFC 858 suppression is in effect, in which case it is a NOP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC 854 gives GA one meaning in this direction and it is exactly the prompt case: a process
    /// that "cannot proceed without input from the other end" must send GA. So the default NVT — no
    /// options negotiated, which is most MU* servers — ends its prompts with <c>IAC GA</c> and
    /// nothing else, and this is the only signal a client will get for them.
    /// </para>
    /// <para>
    /// The one thing that takes that meaning away is RFC 858, whose rule is quoted rather than
    /// paraphrased because it is the whole condition: once suppression is in effect "the IAC GA
    /// command should be treated as a NOP if received, although IAC GA should not normally be sent in
    /// this mode". <see cref="IsGoAheadSuppressed"/> is that state, and nothing else is consulted —
    /// notably not EOR, which is RFC 885 and says nothing about GA. A server that negotiated EOR and
    /// sends GA anyway is still saying it cannot proceed, and a client with nothing buffered loses
    /// nothing by being told twice.
    /// </para>
    /// <para>
    /// Reports directly through <see cref="_onPromptReceived"/> rather than through a shared
    /// intermediary gated on <see cref="TelnetProtocolPluginBase.IsEnabled"/> — that flag is about
    /// plugin lifetime, true from initialisation onwards for every registered plugin, and is not the
    /// negotiated state this handler actually needs to answer to.
    /// </para>
    /// </remarks>
    /// <summary>
    /// What arriving at each of WILL/WONT/DO/DONT for SUPPRESS-GO-AHEAD does. Both directions are
    /// negotiated independently (RFC 858 §5): a server answers DO/DONT for its own outbound GA and
    /// WILL/WONT for the peer's, a client answers the mirror image of that.
    /// </summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        var server = context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server;
        switch (verb)
        {
            case (byte)Trigger.DO when server:
                await OnDoSuppressGAAsync(context);
                break;
            case (byte)Trigger.DONT when server:
                await OnDontSuppressGAAsync(context);
                break;
            case (byte)Trigger.WILL when server:
                await OnWillPeerSuppressGAAsync(context);
                break;
            case (byte)Trigger.WONT when server:
                await OnWontPeerSuppressGAAsync(context);
                break;
            case (byte)Trigger.DO:
                await OnDoOwnSuppressGAAsync(context);
                break;
            case (byte)Trigger.DONT:
                await OnDontOwnSuppressGAAsync(context);
                break;
            case (byte)Trigger.WONT:
                await WontSuppressGAAsync(context);
                break;
            case (byte)Trigger.WILL:
                await OnWillSuppressGAAsync(context);
                break;
        }
    }

    /// <summary>
    /// A bare IAC GA. Client mode only: RFC 854 defines GA in the server-to-user direction ("the
    /// process must transmit the TELNET Go Ahead (GA) command" when it cannot proceed without input
    /// from the other end) and says of the other direction only that "GAs may be sent at any time,
    /// but need not ever be sent" -- so a GA reaching a server is not a statement about anything and
    /// this library does not invent one.
    /// </summary>
    internal ValueTask OnBareGoAheadAsync(IProtocolContext context) =>
        context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client ? OnGoAheadAsync(context) : default;

    private async ValueTask OnGoAheadAsync(IProtocolContext context)
    {
        if (IsGoAheadSuppressed)
        {
            context.Logger.LogTrace(
                "GA received while SUPPRESS-GO-AHEAD is in effect. Treating it as a NOP (RFC 858).");
            return;
        }

        context.Logger.LogDebug("Server is prompting with GA (Go-Ahead)");

        context.Interpreter.TakePartialLineAsPrompt(marked: true);

        if (_onPromptReceived != null)
            await _onPromptReceived().ConfigureAwait(false);
    }

    private async ValueTask OnDontSuppressGAAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client won't do SUPPRESSGOAHEAD - do nothing");
        _ownGoAheadSuppressed = false;
        await OnNegotiatedAsync(false);
    }

    private async ValueTask WontSuppressGAAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server won't do SUPPRESSGOAHEAD - do nothing");
        _peerGoAheadSuppressed = false;
        await OnNegotiatedAsync(false);
    }

    private async ValueTask WillingSuppressGAAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to SUPPRESSGOAHEAD!");
        await context.SendNegotiationAsync(s_willSga);
    }

    private async ValueTask OnDoSuppressGAAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client supports Suppress Go-Ahead.");
        _ownGoAheadSuppressed = true;
        await OnNegotiatedAsync(true);
    }

    private async ValueTask OnWillSuppressGAAsync(IProtocolContext context)
    {
        // RFC 1123 §3.2.2: "A User or Server Telnet MUST always accept negotiation of the Suppress
        // Go Ahead option."
        context.Logger.LogDebug("Server supports Suppress Go-Ahead.");
        _peerGoAheadSuppressed = true;
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_doSga);
    }

    /// <summary>
    /// The server asked this client to suppress its own outbound Go-Ahead (<c>IAC DO
    /// SUPPRESS-GO-AHEAD</c>, client mode).
    /// </summary>
    /// <remarks>
    /// RFC 854 §3(b): a request for a mode already in effect must not be acknowledged. RFC 1123
    /// §3.2.2 requires accepting, so this answers <c>WILL</c> on a genuine change.
    /// </remarks>
    private async ValueTask OnDoOwnSuppressGAAsync(IProtocolContext context)
    {
        if (_ownGoAheadSuppressed)
        {
            return;
        }

        _ownGoAheadSuppressed = true;
        context.Logger.LogDebug(
            "Server asked us to suppress our own Go-Ahead; agreeing (RFC 1123 §3.2.2).");
        await context.SendNegotiationAsync(s_willSga);
    }

    /// <summary>
    /// The server asked this client to resume sending Go-Ahead (<c>IAC DONT SUPPRESS-GO-AHEAD</c>,
    /// client mode).
    /// </summary>
    /// <remarks>Same loop-prevention rule as <see cref="OnDoOwnSuppressGAAsync"/>.</remarks>
    private async ValueTask OnDontOwnSuppressGAAsync(IProtocolContext context)
    {
        if (!_ownGoAheadSuppressed)
        {
            return;
        }

        _ownGoAheadSuppressed = false;
        context.Logger.LogDebug("Server asked us to resume Go-Ahead on our side; agreeing.");
        await context.SendNegotiationAsync(s_wontSga);
    }

    /// <summary>
    /// The peer offered to suppress its own outbound Go-Ahead (<c>IAC WILL SUPPRESS-GO-AHEAD</c>,
    /// server mode).
    /// </summary>
    /// <remarks>
    /// RFC 854 §3(b): a request for a mode already in effect must not be acknowledged. RFC 1123
    /// §3.2.2 requires accepting, so this answers <c>DO</c> on a genuine change.
    /// </remarks>
    private async ValueTask OnWillPeerSuppressGAAsync(IProtocolContext context)
    {
        if (_peerGoAheadSuppressed)
        {
            return;
        }

        _peerGoAheadSuppressed = true;
        context.Logger.LogDebug(
            "Peer will suppress its own Go-Ahead; agreeing (RFC 1123 §3.2.2).");
        await context.SendNegotiationAsync(s_doSga);
    }

    /// <summary>
    /// The peer asked to resume sending its own Go-Ahead (<c>IAC WONT SUPPRESS-GO-AHEAD</c>, server
    /// mode).
    /// </summary>
    /// <remarks>Same loop-prevention rule as <see cref="OnWillPeerSuppressGAAsync"/>.</remarks>
    private async ValueTask OnWontPeerSuppressGAAsync(IProtocolContext context)
    {
        if (!_peerGoAheadSuppressed)
        {
            return;
        }

        _peerGoAheadSuppressed = false;
        context.Logger.LogDebug("Peer will resume Go-Ahead on its side; agreeing.");
        await context.SendNegotiationAsync(s_dontSga);
    }

    #endregion
}
