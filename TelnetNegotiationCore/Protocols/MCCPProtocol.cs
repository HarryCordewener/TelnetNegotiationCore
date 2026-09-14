using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// MCCP (Mud Client Compression Protocol) protocol plugin - MCCP2 and MCCP3, and MCCP v1's start marker
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
/// MCCP v1 (option 85, COMPRESS) is never negotiated: an offer is refused and MCCP2 is what gets
/// accepted. A client still honours v1's start marker, <c>IAC SB COMPRESS WILL SE</c>, as the start
/// of the server-to-client stream, because a server can send it after <c>DO COMPRESS2</c> -- GodWars
/// derivatives do -- and the zlib that follows it is there whatever was agreed. Refusing to inflate it
/// cannot make it plain text; it only turns the rest of the connection into garbage.
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

    // Tracked separately from IsNegotiated itself: MCCP2 and MCCP3 negotiate independently, and
    // reporting either one's own true/false straight to OnNegotiatedAsync would let the second
    // version to resolve stomp the first -- MCCP3 being refused would clear IsNegotiated even
    // though MCCP2 compression is genuinely running. The aggregate below is what a consumer of
    // IsNegotiated actually wants to know: is compression happening in either direction at all.
    private bool _mccp2Negotiated;
    private bool _mccp3Negotiated;

    private Func<int, bool, ValueTask>? _onCompressionEnabled;

    private ValueTask ReportAggregateNegotiationAsync() =>
        OnNegotiatedAsync(_mccp2Negotiated || _mccp3Negotiated);

    /// <summary>
    /// Sets the callback that is invoked when compression state changes.
    /// </summary>
    /// <param name="callback">
    /// The callback to handle compression changes (version: 1, 2 or 3; enabled: true/false). Version 1
    /// is reported only to a client, for a server-to-client stream the server started with MCCP v1's
    /// marker; it is the same stream as version 2's, announced differently.
    /// </param>
    /// <returns>This instance for fluent chaining</returns>
    public MCCPProtocol OnCompressionEnabled(Func<int, bool, ValueTask>? callback)
    {
        _onCompressionEnabled = callback;
        return this;
    }

    /// <summary>
    /// Indicates whether server-to-client compression is running: this side is deflating its output
    /// (server) or inflating its input (client). On a client that includes a stream the server
    /// started with MCCP v1's marker rather than MCCP2's.
    /// </summary>
    public bool IsMCCP2Enabled => _mccp2Enabled;

    /// <summary>
    /// Indicates whether MCCP3 (client-to-server) compression is running: this side is deflating
    /// its output (client) or inflating its input (server).
    /// </summary>
    public bool IsMCCP3Enabled => _mccp3Enabled;

    /// <inheritdoc />
    private int _maxExpansionRatio = MCCPInflateTransform.DefaultMaxExpansionRatio;

    /// <summary>
    /// Sets how far a peer's compressed stream may expand before it is refused (default
    /// <c>200</c>:1).
    /// </summary>
    /// <param name="ratio">
    /// The cumulative output-to-input ratio allowed once the stream has produced more than a
    /// mebibyte. Below that it is not judged at all, so a short stream cannot be condemned by a
    /// ratio taken from a handful of bytes.
    /// </param>
    /// <returns>This instance for fluent chaining</returns>
    /// <remarks>
    /// <para>
    /// The downstream limits bound how much memory a peer can make this side hold; they do not bound
    /// the work of getting there. One compressed byte can become 1,032 through the state machine, so
    /// a peer that compresses absurdly well is buying this side's CPU with its own bandwidth:
    /// measured in Release, 4 KiB of deflate holding 4 MiB of zeros costs about 430 ms of a core,
    /// roughly 430 times the same 4 KiB of plain telnet.
    /// </para>
    /// <para>
    /// Exceeding the ratio is treated exactly as a corrupt stream is: an <c>Error</c> log, the
    /// inflater stopped for good, <c>IsMCCP2Enabled</c> / <c>IsMCCP3Enabled</c> back to
    /// <see langword="false"/>, and nothing further delivered from that direction. Nothing is thrown
    /// onto the read loop.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The ratio is less than 1.</exception>
    public MCCPProtocol WithMaxExpansionRatio(int ratio)
    {
        if (ratio < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ratio), ratio, "A compressed stream cannot be allowed to expand less than 1:1.");
        }

        _maxExpansionRatio = ratio;
        return this;
    }

    public override Type ProtocolType => typeof(MCCPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MCCP (Mud Client Compression Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance and the compression markers are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/> and <c>OnMccp1MarkerAsync</c>/<c>OnMccp2MarkerAsync</c>/
    /// <c>OnMccp3MarkerAsync</c>); this hook survives only to register the server's initial offer, a
    /// cross-cutting mechanism independent of which machine drives byte processing.
    /// </remarks>
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await InitiateMCCPServerAsync(context));
        }
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
    /// is ignored rather than obeyed. Once the peer has <i>ended</i> its stream, though, a marker
    /// is an ordinary fresh start and gets a fresh inflater.
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

        context.SetInboundByteTransform(new MCCPInflateTransform(
            context.Logger,
            () => OnInboundStreamFailedAsync(version),
            () => OnInboundStreamEndedAsync(version),
            _maxExpansionRatio));
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
    /// <remarks>
    /// Only what is actually running is stopped. A peer is entitled to refuse an option it was
    /// never using, and to refuse it twice; announcing a state change that did not happen would
    /// have consumers tearing down a compression state they never had.
    /// </remarks>
    private async ValueTask StopCompressionAsync(IProtocolContext context, int version, bool inbound)
    {
        if (!IsCompressionRunning(version))
        {
            return;
        }

        if (inbound)
            context.SetInboundByteTransform(null);
        else
            await context.SetOutboundByteTransformAsync(null);

        SetEnabled(version, false);

        if (_onCompressionEnabled != null)
            await _onCompressionEnabled(version, false);
    }

    /// <summary>
    /// Handles the peer refusing an option whose inbound stream may still be open.
    /// </summary>
    /// <remarks>
    /// An MCCP stream ends when the peer ends it — "an orderly stream end (Z_FINISH)" — and not a
    /// byte sooner. A refusal that arrives while the stream is still open is the peer saying what
    /// it is about to do, and the bytes already in flight behind it are still compressed. Taking
    /// the inflater out here would hand the rest of the zlib stream to the telnet state machine as
    /// if it were telnet, and leave the plugin believing nothing is running — so the peer's next
    /// compression marker would install a fresh inflater onto the middle of the old stream, where
    /// the first thing it reads is not a zlib header. That is the "unsupported compression method"
    /// in issue #66. <see cref="MCCPInflateTransform"/> reports the real end of the stream instead.
    /// </remarks>
    private async ValueTask StopInflatingOnRefusalAsync(IProtocolContext context, int version)
    {
        if (IsCompressionRunning(version))
        {
            context.Logger.LogDebug(
                "MCCP{Version}: the peer refused the option while its stream is still open; inflating "
                + "until it ends the stream", version);
            return;
        }

        await StopCompressionAsync(context, version, inbound: true);
    }

    /// <summary>
    /// The peer ended its compressed stream in the orderly way the specification describes, so the
    /// connection is plain telnet again from here and the inflater comes out.
    /// </summary>
    private async ValueTask OnInboundStreamEndedAsync(int version)
    {
        Context.SetInboundByteTransform(null);
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

    // Versions 1 and 2 are the one server-to-client stream, announced two ways, so they share a flag:
    // either marker arriving while that stream runs is a repeat, and must not replace its inflater.
    private bool IsCompressionRunning(int version) => version == 3 ? _mccp3Enabled : _mccp2Enabled;

    private void SetEnabled(int version, bool enabled)
    {
        if (version == 3)
            _mccp3Enabled = enabled;
        else
            _mccp2Enabled = enabled;
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
        _mccp2Negotiated = true;
        await ReportAggregateNegotiationAsync();
        await StartDeflatingAsync(context, version: 2, s_sbMccp2);
    }

    private async ValueTask OnDontMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Client doesn't support MCCP2");
        _mccp2Negotiated = false;
        await ReportAggregateNegotiationAsync();
        await StopCompressionAsync(context, version: 2, inbound: false);
    }

    private async ValueTask OnDoMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Client will use MCCP3 - awaiting IAC SB MCCP3 IAC SE before inflating");
        _mccp3Negotiated = true;
        await ReportAggregateNegotiationAsync();
    }

    /// <summary>
    /// MCCP2 and MCCP3 are two option numbers on one protocol class, each normally negotiated in the
    /// opposite direction. A client cannot honour a request to compress through MCCP2 or MCCP3, so a
    /// wrong-direction DO is explicitly refused with WONT rather than silently discarded.
    /// </summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, byte option, IProtocolContext context)
    {
        var server = context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server;

        if (option == (byte)Trigger.MCCP2)
        {
            if (server)
            {
                switch (verb)
                {
                    case (byte)Trigger.DO: await OnDoMCCP2Async(context); break;
                    case (byte)Trigger.DONT: await OnDontMCCP2Async(context); break;
                }
            }
            else
            {
                switch (verb)
                {
                    case (byte)Trigger.DO: await Helpers.OptionNegotiation.AnswerAsync(false, verb, option, context); break;
                    case (byte)Trigger.WILL: await OnWillMCCP2Async(context); break;
                    case (byte)Trigger.WONT: await OnWontMCCP2Async(context); break;
                }
            }
        }
        else if (option == (byte)Trigger.MCCP3)
        {
            if (server)
            {
                switch (verb)
                {
                    case (byte)Trigger.DO: await OnDoMCCP3Async(context); break;
                    case (byte)Trigger.DONT: await OnDontMCCP3Async(context); break;
                }
            }
            else
            {
                switch (verb)
                {
                    case (byte)Trigger.DO: await Helpers.OptionNegotiation.AnswerAsync(false, verb, option, context); break;
                    case (byte)Trigger.WILL: await OnWillMCCP3Async(context); break;
                    case (byte)Trigger.WONT: await OnWontMCCP3Async(context); break;
                }
            }
        }
    }

    /// <summary>MCCP2's marker only ever started an inflater on a client -- a server never configured
    /// a route to it at all, so it is a no-op there rather than an assumption about what it means.</summary>
    internal ValueTask OnMccp2MarkerAsync(IProtocolContext context) =>
        context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server
            ? default
            : StartInflatingAsync(context, version: 2);

    /// <summary>MCCP3's marker only ever started an inflater on a server -- the mirror of MCCP2's.</summary>
    internal ValueTask OnMccp3MarkerAsync(IProtocolContext context) =>
        context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server
            ? StartInflatingAsync(context, version: 3)
            : default;

    /// <summary>MCCP1's marker starts the same server-to-client stream MCCP2's does, but only a
    /// client ever inflates it; a server only ever configured itself to consume and ignore it.</summary>
    internal ValueTask OnMccp1MarkerAsync(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.Logger.LogDebug("MCCP1: ignoring a start marker from a peer that cannot compress with it");
            return default;
        }

        return StartInflatingAsync(context, version: 1);
    }

    private async ValueTask OnDontMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Client doesn't support MCCP3");
        _mccp3Negotiated = false;
        await ReportAggregateNegotiationAsync();
        await StopInflatingOnRefusalAsync(context, version: 3);
    }

    private async ValueTask OnWillMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server supports MCCP2 - accepting");
        _mccp2Negotiated = true;
        await ReportAggregateNegotiationAsync();
        // Compression starts at IAC SB MCCP2 IAC SE, not here.
        await context.SendNegotiationAsync(s_doMccp2);
    }

    private async ValueTask OnWontMCCP2Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server doesn't support MCCP2");
        _mccp2Negotiated = false;
        await ReportAggregateNegotiationAsync();
        await StopInflatingOnRefusalAsync(context, version: 2);
    }

    private async ValueTask OnWillMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server supports MCCP3 - will start compressing output");
        _mccp3Negotiated = true;
        await ReportAggregateNegotiationAsync();
        await context.SendNegotiationAsync(s_doMccp3);
        await StartDeflatingAsync(context, version: 3, s_sbMccp3);
    }

    private async ValueTask OnWontMCCP3Async(IProtocolContext context)
    {
        context.Logger.LogDebug("Server doesn't support MCCP3");
        _mccp3Negotiated = false;
        await ReportAggregateNegotiationAsync();
        await StopCompressionAsync(context, version: 3, inbound: false);
    }

    #endregion
}
