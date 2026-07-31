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
/// MCCP (Mud Client Compression Protocol) protocol plugin - MCCP2 and MCCP3
/// Implements https://tintin.mudhalla.net/protocols/mccp
/// Uses zlib compression (RFC 1950) via System.IO.Compression.ZLibStream
/// </summary>
/// <remarks>
/// MCCP2 compresses server-to-client, MCCP3 compresses client-to-server. Both work the same way:
/// the side that is going to compress announces it with <c>IAC SB MCCPn IAC SE</c>, and from the
/// byte after that <c>SE</c> the rest of the connection in that direction is a single zlib stream —
/// text and telnet negotiation alike. Nothing marks the end of a message and nothing goes back to
/// plain telnet.
///
/// So both directions are handled with a stream transform installed on the interpreter rather than
/// with per-message calls: <see cref="MCCPInflateTransform"/> on the way in, ahead of the telnet
/// state machine, and <see cref="MCCPDeflateTransform"/> on the way out, behind everything else.
///
/// RFC 1950 Compliance:
/// - Uses DEFLATE compression algorithm (compression method 8)
/// - Includes standard zlib header with checksum validation
/// - Includes ADLER-32 checksum for data integrity
/// - See https://tintin.mudhalla.net/rfc/rfc1950 for specification
/// </remarks>
[RequiredMethod("OnCompressionEnabled", Description = "Configure the callback to handle compression state changes (optional)")]
public class MCCPProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willMccp2 = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 };
    private static readonly byte[] s_willMccp3 = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP3 };
    private static readonly byte[] s_doMccp2 = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 };
    private static readonly byte[] s_doMccp3 = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP3 };
    private static readonly byte[] s_sbMccp2 = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP2, (byte)Trigger.IAC, (byte)Trigger.SE };
    private static readonly byte[] s_sbMccp3 = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP3, (byte)Trigger.IAC, (byte)Trigger.SE };

    private bool _mccp2Enabled;
    private bool _mccp3Enabled;

    private Func<int, bool, ValueTask>? _onCompressionEnabled;

    /// <summary>
    /// Sets the callback that is invoked when compression state changes.
    /// </summary>
    /// <param name="callback">The callback to handle compression changes (version: 2 or 3, enabled: true/false)</param>
    /// <returns>This instance for fluent chaining</returns>
    public MCCPProtocol OnCompressionEnabled(Func<int, bool, ValueTask>? callback)
    {
        _onCompressionEnabled = callback;
        return this;
    }

    /// <summary>
    /// Indicates whether MCCP2 (server-to-client) compression is running: this side is deflating
    /// its output (server) or inflating its input (client).
    /// </summary>
    public bool IsMCCP2Enabled => _mccp2Enabled;

    /// <summary>
    /// Indicates whether MCCP3 (client-to-server) compression is running: this side is deflating
    /// its output (client) or inflating its input (server).
    /// </summary>
    public bool IsMCCP3Enabled => _mccp3Enabled;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(MCCPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MCCP (Mud Client Compression Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring MCCP state machine");

        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            // MCCP2: the server compresses its output once the client says DO.
            stateMachine.Configure(State.Do)
                .Permit(Trigger.MCCP2, State.DoMCCP2);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.MCCP2, State.DontMCCP2);

            stateMachine.Configure(State.DoMCCP2)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDoMCCP2Async(context));

            stateMachine.Configure(State.DontMCCP2)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDontMCCP2Async(context));

            // MCCP3: the client compresses its output; the server inflates what it receives.
            stateMachine.Configure(State.Do)
                .Permit(Trigger.MCCP3, State.DoMCCP3);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.MCCP3, State.DontMCCP3);

            stateMachine.Configure(State.DoMCCP3)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug(
                    "Client will use MCCP3 - awaiting IAC SB MCCP3 IAC SE before inflating"));

            stateMachine.Configure(State.DontMCCP3)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDontMCCP3Async(context));

            // Only the client sends IAC SB MCCP3 IAC SE, so only the server listens for it.
            ConfigureCompressionMarker(stateMachine, context, version: 3,
                Trigger.MCCP3, State.NegotiatingMCCP3, State.CompletingMCCP3);

            // Server initiates MCCP on connection
            context.RegisterInitialNegotiation(async () => await InitiateMCCPServerAsync(context));
        }
        else
        {
            // MCCP2: the server compresses its output; the client inflates what it receives.
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.MCCP2, State.WillMCCP2);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.MCCP2, State.WontMCCP2);

            stateMachine.Configure(State.WillMCCP2)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnWillMCCP2Async(context));

            stateMachine.Configure(State.WontMCCP2)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnWontMCCP2Async(context));

            // MCCP3: the client compresses its output once the server offers it.
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.MCCP3, State.WillMCCP3);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.MCCP3, State.WontMCCP3);

            stateMachine.Configure(State.WillMCCP3)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnWillMCCP3Async(context));

            stateMachine.Configure(State.WontMCCP3)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnWontMCCP3Async(context));

            // Only the server sends IAC SB MCCP2 IAC SE, so only the client listens for it.
            ConfigureCompressionMarker(stateMachine, context, version: 2,
                Trigger.MCCP2, State.NegotiatingMCCP2, State.CompletingMCCP2);
        }
    }

    /// <summary>
    /// Wires up <c>IAC SB MCCPn IAC SE</c>, the marker after which everything the peer sends is
    /// compressed.
    /// </summary>
    /// <remarks>
    /// The completing state is entered on the marker's <b>second IAC</b> — the <c>SE</c> has not
    /// been read yet — so the inflater goes in on the way <i>out</i> of that state, which is the
    /// moment the <c>SE</c> is consumed and the compressed stream begins. Installing it on entry
    /// instead would feed the <c>SE</c> itself to zlib.
    /// </remarks>
    private void ConfigureCompressionMarker(
        StateMachine<State, Trigger> stateMachine,
        IProtocolContext context,
        int version,
        Trigger option,
        State negotiating,
        State completing)
    {
        stateMachine.Configure(State.SubNegotiation)
            .Permit(option, negotiating);

        stateMachine.Configure(negotiating)
            .Permit(Trigger.IAC, completing);

        stateMachine.Configure(completing)
            .SubstateOf(State.EndSubNegotiation)
            .OnExitAsync(async transition =>
            {
                // Any other trigger out of here is the safety net recovering from a malformed
                // marker, not the peer starting to compress.
                if (transition.Trigger == Trigger.SE)
                {
                    await StartInflatingAsync(context, version);
                }
            });
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MCCP Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("MCCP Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override async ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MCCP Protocol disabled");
        await DisableCompressionAsync();
    }

    /// <inheritdoc />
    protected override async ValueTask OnDisposeAsync() => await DisableCompressionAsync();

    /// <summary>
    /// Starts inflating everything the peer sends from here on, using one zlib stream for the rest
    /// of the connection.
    /// </summary>
    /// <remarks>
    /// A peer chooses when markers arrive and can send a second one. Replacing a running inflater
    /// would throw away the deflate window the rest of its stream is encoded against, so a repeat
    /// is ignored rather than obeyed.
    /// </remarks>
    private async ValueTask StartInflatingAsync(IProtocolContext context, int version)
    {
        if (IsCompressionRunning(version))
        {
            context.Logger.LogDebug(
                "MCCP{Version}: already inflating, ignoring a repeated compression marker", version);
            return;
        }

        context.Logger.LogInformation("MCCP{Version}: inbound stream is compressed from here on", version);

        context.SetInboundByteTransform(
            new MCCPInflateTransform(context.Logger, () => OnInboundStreamFailedAsync(version)));
        SetEnabled(version, true);

        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(version, true);
    }

    /// <summary>
    /// Announces that this side is about to start compressing, and starts, as one step: the marker
    /// is the last thing that goes out in the clear and nothing can be written between the two.
    /// </summary>
    private async ValueTask StartDeflatingAsync(IProtocolContext context, int version, byte[] marker)
    {
        if (IsCompressionRunning(version))
        {
            context.Logger.LogDebug(
                "MCCP{Version}: already deflating, ignoring a repeated request to start", version);
            return;
        }

        context.Logger.LogInformation("MCCP{Version}: outbound stream is compressed from here on", version);

        await context.SetOutboundByteTransformAsync(new MCCPDeflateTransform(), marker);
        SetEnabled(version, true);

        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(version, true);
    }

    /// <summary>
    /// Stops compressing in one direction, and says so.
    /// </summary>
    private async ValueTask StopCompressionAsync(IProtocolContext context, int version, bool inbound)
    {
        if (inbound)
            context.SetInboundByteTransform(null);
        else
            await context.SetOutboundByteTransformAsync(null);

        SetEnabled(version, false);

        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(version, false);
    }

    /// <summary>
    /// A deflate stream that has gone wrong cannot be resynchronized, so the inflater stops for
    /// good and the connection carries on receiving nothing rather than receiving garbage. The
    /// consumer is told, because otherwise it would go on believing compression was live.
    /// </summary>
    private async ValueTask OnInboundStreamFailedAsync(int version)
    {
        Context.Logger.LogError("MCCP{Version}: decompression failed, no further input can be decoded", version);
        SetEnabled(version, false);

        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(version, false);
    }

    private bool IsCompressionRunning(int version) => version == 2 ? _mccp2Enabled : _mccp3Enabled;

    private void SetEnabled(int version, bool enabled)
    {
        if (version == 2)
            _mccp2Enabled = enabled;
        else
            _mccp3Enabled = enabled;
    }

    private async ValueTask DisableCompressionAsync()
    {
        Context.SetInboundByteTransform(null);
        await Context.SetOutboundByteTransformAsync(null);
        _mccp2Enabled = false;
        _mccp3Enabled = false;
    }

    #region State Machine Handlers

    private async ValueTask InitiateMCCPServerAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server announcing MCCP2 and MCCP3 support");
        await context.SendNegotiationAsync(s_willMccp2);
        await context.SendNegotiationAsync(s_willMccp3);
    }

    private async ValueTask OnDoMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Client supports MCCP2 - will start compression");
        await StartDeflatingAsync(context, version: 2, s_sbMccp2);
    }

    private async ValueTask OnDontMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Client doesn't support MCCP2");
        await StopCompressionAsync(context, version: 2, inbound: false);
    }

    private async ValueTask OnDontMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Client doesn't support MCCP3");
        await StopCompressionAsync(context, version: 3, inbound: true);
    }

    private async ValueTask OnWillMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server supports MCCP2 - accepting");
        // Compression starts at IAC SB MCCP2 IAC SE, not here.
        await context.SendNegotiationAsync(s_doMccp2);
    }

    private async ValueTask OnWontMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server doesn't support MCCP2");
        await StopCompressionAsync(context, version: 2, inbound: true);
    }

    private async ValueTask OnWillMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server supports MCCP3 - will start compressing output");
        await context.SendNegotiationAsync(s_doMccp3);
        await StartDeflatingAsync(context, version: 3, s_sbMccp3);
    }

    private async ValueTask OnWontMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server doesn't support MCCP3");
        await StopCompressionAsync(context, version: 3, inbound: false);
    }

    #endregion
}
