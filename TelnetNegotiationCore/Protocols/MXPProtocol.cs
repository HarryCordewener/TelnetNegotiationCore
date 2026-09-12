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
/// MXP (MUD eXtension Protocol) plugin implementation.
/// MXP uses telnet option 91 (0x5B) and enables rich content tags in MUD output.
/// </summary>
/// <remarks>
/// <para>
/// MXP takes two steps, not one. <c>WILL</c>/<c>DO</c> settles the telnet option, and then the
/// server sends <c>IAC SB MXP IAC SE</c> — the marker that means "everything after this is MXP".
/// Only at that marker does a client start parsing tags and decoding entities; before it, a client
/// is in plain telnet and shows the server's <c>&lt;send&gt;</c> tags and <c>&amp;quot;</c>
/// entities to the player verbatim. Both halves live here: the server sends the marker when the
/// client says <c>DO</c>, and the client recognises it when a server sends it.
/// </para>
/// <para>
/// What the marker does <i>not</i> do is change how the byte stream is framed — unlike MCCP's
/// identically shaped one, which turns everything after it into a zlib stream. Ordinary text keeps
/// flowing through the same state machine.
/// </para>
/// <para>
/// Line modes (<c>ESC[0z</c> open, <c>ESC[1z</c> secure, <c>ESC[2z</c> locked, <c>ESC[6z</c> lock
/// secure, …) are in-band output, not negotiation, so they are the host application's to write and
/// deliberately not sent from here: which lines a game is willing to let carry live tags is its
/// policy, not this library's.
/// </para>
/// <para>
/// Call <see cref="OnMXPEnabled"/> to be told when MXP output actually begins — after the marker
/// on both sides, so a host that switches renderers in that callback cannot emit a tag ahead of it.
/// </para>
/// <seealso href="https://www.zuggsoft.com/zmud/mxp.htm">MXP specification</seealso>
/// <seealso href="https://www.gammon.com.au/mushclient/addingservermxp.htm">Adding MXP support to a MUD server</seealso>
/// </remarks>
[RequiredMethod("OnMXPEnabled", Description = "Configure the callback to handle MXP activation (optional but recommended)")]
public class MXPProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willMxp = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP];
    private static readonly byte[] s_doMxp = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP];

    /// <summary><c>IAC SB MXP IAC SE</c> — the marker that starts MXP mode.</summary>
    private static readonly byte[] s_sbMxp =
        [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MXP, (byte)Trigger.IAC, (byte)Trigger.SE];

    private bool? _mxpEnabled = null;

    // Written on the byte-processing loop, read by whatever thread asks IsMxpModeStarted -- the same
    // treatment IsNegotiated gets in the base class, and for the same reason.
    private volatile bool _mxpModeStarted;
    private Func<ValueTask>? _onMXPEnabled;

    /// <summary>
    /// Sets the callback that is invoked when MXP output begins — once the start marker has been
    /// sent (server) or received (client), not when the option is merely negotiated.
    /// </summary>
    /// <param name="callback">The callback to handle MXP activation</param>
    /// <returns>This instance for fluent chaining</returns>
    public MXPProtocol OnMXPEnabled(Func<ValueTask>? callback)
    {
        _onMXPEnabled = callback;
        return this;
    }

    /// <summary>
    /// Indicates whether the MXP telnet option has been negotiated. This is not yet a licence to
    /// write tags — see <see cref="IsMxpModeStarted"/>.
    /// </summary>
    public bool IsMXPActive => _mxpEnabled == true;

    /// <summary>
    /// Indicates whether MXP mode has actually started: this side has sent <c>IAC SB MXP IAC SE</c>
    /// (server) or seen it (client). Until then a peer treats tags and entities as literal text.
    /// </summary>
    public bool IsMxpModeStarted => _mxpModeStarted;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(MXPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MXP (MUD eXtension Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring MXP state machine");

        context.SetSharedState("MXP_Protocol", this);

        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            stateMachine.Configure(State.Do)
                .Permit(Trigger.MXP, State.DoMXP);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.MXP, State.DontMXP);

            stateMachine.Configure(State.DoMXP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async _ => await OnDoMXPAsync(context));

            stateMachine.Configure(State.DontMXP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDontMXPAsync(context));

            context.RegisterInitialNegotiation(async () => await WillingMXPAsync(context));
        }
        else
        {
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.MXP, State.WillMXP);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.MXP, State.WontMXP);

            stateMachine.Configure(State.WontMXP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await WontMXPAsync(context));

            stateMachine.Configure(State.WillMXP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async _ => await OnWillMXPAsync(context));

            // Only the server sends the start marker, so only the client listens for it. Without
            // these transitions option 91 has no route out of SubNegotiation and a correct server's
            // marker is swallowed by the safety net as an unsupported subnegotiation.
            ConfigureStartMarker(stateMachine, context);
        }
    }

    /// <summary>
    /// Wires up <c>IAC SB MXP IAC SE</c> on the receiving side.
    /// </summary>
    /// <remarks>
    /// <see cref="State.CompletingMXP"/> is entered on the marker's <b>second IAC</b>, with the
    /// <c>SE</c> not yet read, so the mode starts on the way <i>out</i> of that state — the moment
    /// the <c>SE</c> is consumed. Any other trigger out of it is the safety net recovering from a
    /// malformed marker, not a server starting MXP.
    /// </remarks>
    private void ConfigureStartMarker(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.MXP, State.NegotiatingMXP);

        stateMachine.Configure(State.NegotiatingMXP)
            .Permit(Trigger.IAC, State.CompletingMXP);

        stateMachine.Configure(State.CompletingMXP)
            .SubstateOf(State.EndSubNegotiation)
            .OnExitAsync(async transition =>
            {
                if (transition.Trigger == Trigger.SE)
                {
                    await StartMxpModeAsync(context);
                }
            });
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MXP Protocol initialized");
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        // Deliberately does not touch _mxpEnabled. This hook means "the plugin is attached and
        // processing", which is what IsEnabled reports; IsMXPActive answers the different question of
        // whether the peer agreed to option 91, and only a real DO/WILL from the peer may set it.
        // Conflating the two is the confusion IsNegotiated was added in 2.9.0 to end.
        Context.Logger.LogInformation("MXP Protocol enabled");
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MXP Protocol disabled");
        _mxpEnabled = false;
        _mxpModeStarted = false;
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _mxpEnabled = null;
        _mxpModeStarted = false;
        return default;
    }

    #region State Machine Handlers

    /// <summary>
    /// Mirrors the asymmetry in <see cref="ConfigureStateMachine"/>: a server only ever configured
    /// DO/DONT, a client only ever configured WILL/WONT, so the verb the other role never wired for
    /// this option is a no-op here too rather than an assumption about what the peer meant.
    /// </summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            switch (verb)
            {
                case (byte)Trigger.DO:
                    await OnDoMXPAsync(context);
                    break;
                case (byte)Trigger.DONT:
                    await OnDontMXPAsync(context);
                    break;
            }
        }
        else
        {
            switch (verb)
            {
                case (byte)Trigger.WILL:
                    await OnWillMXPAsync(context);
                    break;
                case (byte)Trigger.WONT:
                    await WontMXPAsync(context);
                    break;
            }
        }
    }

    private async ValueTask WillingMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to MXP!");
        await context.SendNegotiationAsync(s_willMxp);
    }

    private async ValueTask OnDoMXPAsync(IProtocolContext context)
    {
        // A peer may re-affirm DO mid-session. The marker below means "MXP output starts here", and
        // restating it in a session whose tags are already flowing is not something to obey; nor
        // should the host's activation callback — which is typically what swaps its renderer over —
        // run a second time for a state that did not move.
        if (_mxpEnabled == true)
        {
            context.Logger.LogDebug("MXP is already active; ignoring a repeated DO MXP.");
            return;
        }

        context.Logger.LogDebug("Client supports MXP.");
        _mxpEnabled = true;
        await OnNegotiatedAsync(true);

        // MXP does not begin at DO. Until this marker arrives the client is in plain telnet and
        // renders every tag and entity literally, so it goes out before the callback that lets the
        // host start writing them.
        await context.SendNegotiationAsync(s_sbMxp);
        await StartMxpModeAsync(context);
    }

    private async ValueTask OnDontMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client won't do MXP - do nothing");
        _mxpEnabled = false;
        _mxpModeStarted = false;
        await OnNegotiatedAsync(false);
    }

    private async ValueTask WontMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server won't do MXP - do nothing");
        _mxpEnabled = false;
        _mxpModeStarted = false;
        await OnNegotiatedAsync(false);
    }

    private async ValueTask OnWillMXPAsync(IProtocolContext context)
    {
        if (_mxpEnabled == true)
        {
            context.Logger.LogDebug("MXP is already active; ignoring a repeated WILL MXP.");
            return;
        }

        context.Logger.LogDebug("Server supports MXP.");
        _mxpEnabled = true;
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_doMxp);

        // No callback here: the server has agreed to speak MXP but has not started. That is
        // IAC SB MXP IAC SE, handled in ConfigureStartMarker.
    }

    /// <summary>
    /// Marks MXP mode started and tells the host, once.
    /// </summary>
    internal async ValueTask StartMxpModeAsync(IProtocolContext context)
    {
        // The marker says when a negotiated option begins, and cannot stand in for negotiating it.
        // A peer that sends IAC SB MXP IAC SE without a WILL/DO exchange behind it would otherwise
        // switch this side into MXP mode -- running the host's activation callback, and leaving
        // IsMxpModeStarted true while IsMXPActive said the option was never agreed to.
        if (_mxpEnabled != true)
        {
            context.Logger.LogWarning("Ignoring an MXP start marker: option 91 was never negotiated.");
            return;
        }

        if (_mxpModeStarted)
        {
            context.Logger.LogDebug("MXP mode already started; ignoring a repeated start marker.");
            return;
        }

        _mxpModeStarted = true;
        context.Logger.LogInformation("MXP mode has started; tags and entities are live from here on.");

        if (_onMXPEnabled != null)
            await _onMXPEnabled().ConfigureAwait(false);
    }

    #endregion
}
