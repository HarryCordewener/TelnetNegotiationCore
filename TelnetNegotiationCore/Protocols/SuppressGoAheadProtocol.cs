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

    // The peer's SUPPRESS-GO-AHEAD state. See the remarks on the State.GoAhead configuration below
    // for why this is the peer's direction only, and never the direction this end suppresses.
    private bool? _doGA = true;

    // This end's own SUPPRESS-GO-AHEAD state -- client mode only, set from State.Do/State.Dont in
    // ConfigureStateMachine's client branch. RFC 858 §5 requires the two directions be negotiated
    // independently, so this cannot reuse _doGA above. Starts false, RFC 858 §3's default ("GO
    // AHEAD is transmitted"), so an opening DONT SUPPRESS-GO-AHEAD finds us already in that mode
    // and is correctly silent under RFC 854 §3(b).
    private bool _ownGoAheadSuppressed;

    private Func<ValueTask>? _onPromptReceived;

    /// <summary>
    /// Sets the callback that is invoked when a prompt is received (Suppress Go-Ahead marker).
    /// </summary>
    /// <remarks>
    /// Runs on the byte-processing loop — the same thread EOR's and Packet Patch's prompt callbacks
    /// run on, so a handler shared across all three (as <c>AddDefaultMUDProtocols</c> does by
    /// default) needs no thread-safety of its own on that account.
    /// </remarks>
    /// <param name="callback">The callback to handle prompts</param>
    /// <returns>This instance for fluent chaining</returns>
    public SuppressGoAheadProtocol OnPrompt(Func<ValueTask>? callback)
    {
        _onPromptReceived = callback;
        return this;
    }

    /// <summary>
    /// Whether this client refuses a server's offer to stop sending Go-Ahead. False by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setting this true makes this client violate RFC 1123 §3.2.2, which updates RFC 854/858 for
    /// Internet hosts: "A User or Server Telnet MUST always accept negotiation of the Suppress Go
    /// Ahead option." Accepting is therefore the default; this switch exists to opt out of a MUST,
    /// not to tune a preference.
    /// </para>
    /// <para>
    /// The reason to opt out anyway: a MUD client whose game pairs SUPPRESS-GO-AHEAD with GA-marked
    /// prompts loses its only prompt boundary the moment it agrees. RFC 1123 forbids this client
    /// from vetoing the option, but does not require the game to honour a refusal either -- a server
    /// is free to negotiate SGA over this client's <c>DONT</c> regardless. So this switch is for the
    /// case where the game does back off, at the cost of standing outside the MUST while it is set.
    /// </para>
    /// </remarks>
    public bool RefusesSuppression { get; private set; }

    /// <summary>
    /// Sets whether a server's <c>WILL SUPPRESS-GO-AHEAD</c> is refused (<c>DONT</c>) instead of
    /// accepted (<c>DO</c>, the RFC 1123 §3.2.2-compliant default). See <see cref="RefusesSuppression"/>.
    /// </summary>
    /// <param name="refuse">True (the default when called) to refuse suppression; false to accept it</param>
    /// <returns>This instance for fluent chaining</returns>
    public SuppressGoAheadProtocol RefuseSuppression(bool refuse = true)
    {
        RefusesSuppression = refuse;
        return this;
    }

    /// <summary>
    /// Indicates whether Go-Ahead is suppressed (true = suppressed, false = enabled)
    /// </summary>
    public bool IsGoAheadSuppressed => _doGA == false;

    /// <summary>
    /// Whether this client has agreed to suppress its own outbound Go-Ahead (client mode only).
    /// Independent of <see cref="IsGoAheadSuppressed"/>, which reflects the peer's direction.
    /// </summary>
    public bool OwnGoAheadSuppressed => _ownGoAheadSuppressed;

    /// <summary>
    /// Whether <em>this</em> end currently suppresses its own outbound Go-Ahead -- the direction
    /// <c>TelnetInterpreter.PromptTerminator</c> needs when deciding whether an outbound prompt may
    /// end with <c>IAC GA</c>.
    /// </summary>
    /// <remarks>
    /// Mode-correct, unlike reading <c>_doGA</c> or <see cref="OwnGoAheadSuppressed"/> directly:
    /// <c>_doGA</c> is mode-dependent (see its declaration) -- in server mode a peer's <c>DO</c> is a
    /// request that <em>we</em> suppress, so <c>_doGA</c> already tracks our own direction there; in
    /// client mode <c>_doGA</c> tracks the <em>peer's</em> direction instead, so this reads
    /// <see cref="OwnGoAheadSuppressed"/> there, which client mode alone maintains. Do not substitute
    /// <see cref="IsGoAheadSuppressed"/> here -- it answers a different question, whether an
    /// <em>inbound</em> GA still means a prompt, which in client mode is the peer's direction too.
    /// </remarks>
    public bool SuppressesOutboundGoAhead =>
        Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server
            ? _doGA == false
            : _ownGoAheadSuppressed;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(SuppressGoAheadProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Suppress Go-Ahead";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();
    // Note: SuppressGA and EOR often work together as fallbacks
    // This could be expressed as a soft dependency if needed

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Suppress Go-Ahead state machine");
        
        // Register SuppressGA protocol handlers with the context
        context.SetSharedState("SuppressGA_Protocol", this);
        
        // Configure state machine transitions for Suppress Go-Ahead protocol
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            stateMachine.Configure(State.Do)
                .Permit(Trigger.SUPPRESSGOAHEAD, State.DoSUPPRESSGOAHEAD);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.SUPPRESSGOAHEAD, State.DontSUPPRESSGOAHEAD);

            stateMachine.Configure(State.DoSUPPRESSGOAHEAD)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await OnDoSuppressGAAsync(x, context));

            stateMachine.Configure(State.DontSUPPRESSGOAHEAD)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDontSuppressGAAsync(context));

            context.RegisterInitialNegotiation(async () => await WillingSuppressGAAsync(context));
        }
        else
        {
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.SUPPRESSGOAHEAD, State.WillSUPPRESSGOAHEAD);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.SUPPRESSGOAHEAD, State.WontSUPPRESSGOAHEAD);

            // The other direction: a server asking *us* to suppress our own outbound Go-Ahead.
            // RFC 854 §3(b) requires a response to a change of mode, even one that changes nothing
            // about what this client actually transmits, and RFC 858 §5 requires the two directions
            // be negotiated independently -- so this reuses the server branch's DoSUPPRESSGOAHEAD /
            // DontSUPPRESSGOAHEAD states rather than adding near-duplicates; only one branch of this
            // `if` ever configures a given interpreter instance, so there is no clash.
            stateMachine.Configure(State.Do)
                .Permit(Trigger.SUPPRESSGOAHEAD, State.DoSUPPRESSGOAHEAD);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.SUPPRESSGOAHEAD, State.DontSUPPRESSGOAHEAD);

            stateMachine.Configure(State.DoSUPPRESSGOAHEAD)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDoOwnSuppressGAAsync(context));

            stateMachine.Configure(State.DontSUPPRESSGOAHEAD)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDontOwnSuppressGAAsync(context));

            stateMachine.Configure(State.WontSUPPRESSGOAHEAD)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await WontSuppressGAAsync(context));

            stateMachine.Configure(State.WillSUPPRESSGOAHEAD)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await OnWillSuppressGAAsync(x, context));

            // A bare IAC GA arriving from the server. The interpreter permits the transition; what
            // the GA *means* is this plugin's knowledge, because RFC 858 is the only thing that ever
            // takes the meaning away. Client mode only: RFC 854 defines GA in the server-to-user
            // direction ("the process must transmit the TELNET Go Ahead (GA) command" when it cannot
            // proceed without input from the other end) and says of the other direction only that
            // "GAs may be sent at any time, but need not ever be sent" — so a GA reaching a server is
            // not a statement about anything and this library does not invent one. It is also the
            // only direction whose suppression this plugin records: in server mode _doGA tracks the
            // peer's DO, which asks *us* to suppress, and says nothing about what the peer sends.
            stateMachine.Configure(State.GoAhead)
                .OnEntryAsync(async () => await OnGoAheadAsync(context));
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
        _doGA = false; // GA suppressed
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Suppress Go-Ahead Protocol disabled");
        _doGA = true; // GA active
        return default(ValueTask);
    }

    /// <summary>
    /// Enables Go-Ahead suppression for the connection
    /// </summary>
    public ValueTask SuppressGoAheadAsync()
    {
        if (!IsEnabled)
            return default(ValueTask);

        _doGA = false;
        Context.Logger.LogInformation("Go-Ahead suppression enabled");
        return default(ValueTask);
    }

    /// <summary>
    /// Disables Go-Ahead suppression (re-enables GA)
    /// </summary>
    public ValueTask EnableGoAheadAsync()
    {
        if (!IsEnabled)
            return default(ValueTask);

        _doGA = true;
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
        _doGA = null;
        return default(ValueTask);
    }

    /// <summary>
    /// Called by the interpreter when a prompt is signaled.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnPromptAsync()
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Server is prompting with Suppress Go-Ahead");
        
        if (_onPromptReceived != null)
            await _onPromptReceived().ConfigureAwait(false);
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
    /// This deliberately does not go through <see cref="OnPromptAsync"/>. That method's
    /// <see cref="TelnetProtocolPluginBase.IsEnabled"/> guard is about plugin lifetime — it is true
    /// from initialisation onwards for every registered plugin — so it is not the negotiated state
    /// and would answer nothing here.
    /// </para>
    /// </remarks>
    private async ValueTask OnGoAheadAsync(IProtocolContext context)
    {
        if (IsGoAheadSuppressed)
        {
            context.Logger.LogTrace(
                "GA received while SUPPRESS-GO-AHEAD is in effect. Treating it as a NOP (RFC 858).");
            return;
        }

        context.Logger.LogDebug("Server is prompting with GA (Go-Ahead)");

        context.TakePartialLineAsPrompt(marked: true);

        if (_onPromptReceived != null)
            await _onPromptReceived().ConfigureAwait(false);
    }

    private async ValueTask OnDontSuppressGAAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client won't do SUPPRESSGOAHEAD - do nothing");
        _doGA = true;
        await OnNegotiatedAsync(false);
    }

    private async ValueTask WontSuppressGAAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server won't do SUPPRESSGOAHEAD - do nothing");
        _doGA = true;
        await OnNegotiatedAsync(false);
    }

    private async ValueTask WillingSuppressGAAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to SUPPRESSGOAHEAD!");
        await context.SendNegotiationAsync(s_willSga);
    }

    private async ValueTask OnDoSuppressGAAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Client supports Suppress Go-Ahead.");
        _doGA = false;
        await OnNegotiatedAsync(true);
    }

    private async ValueTask OnWillSuppressGAAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        if (RefusesSuppression)
        {
            context.Logger.LogDebug(
                "Server offered SUPPRESS-GO-AHEAD; refusing by request, though RFC 1123 §3.2.2 requires accepting it.");
            _doGA = true;
            await OnNegotiatedAsync(false);
            await context.SendNegotiationAsync(s_dontSga);
            return;
        }

        context.Logger.LogDebug("Server supports Suppress Go-Ahead (accepting, per RFC 1123 §3.2.2).");
        _doGA = false;
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_doSga);
    }

    /// <summary>
    /// The server asked this client to suppress its own outbound Go-Ahead (<c>IAC DO
    /// SUPPRESS-GO-AHEAD</c>, client mode).
    /// </summary>
    /// <remarks>
    /// RFC 854 §3(b): a request for a mode already in effect must not be acknowledged, to prevent
    /// negotiation loops -- hence the early return when <see cref="_ownGoAheadSuppressed"/> is
    /// already true. Otherwise this answers <c>WILL</c>, which RFC 1123 §3.2.2 requires here just as
    /// it does for the server's own offer, and the promise is kept, not moot: setting
    /// <see cref="_ownGoAheadSuppressed"/> is what <see cref="SuppressesOutboundGoAhead"/> reads, and
    /// <c>TelnetInterpreter.PromptTerminator</c> consults that before ever putting <c>IAC GA</c> on an
    /// outbound prompt.
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
    /// <remarks>
    /// Same loop-prevention rule as <see cref="OnDoOwnSuppressGAAsync"/>, and RFC 854's "a party ...
    /// must never refuse a request to disable some option" means the answering <c>WONT</c> is always
    /// sent on a genuine change. Clearing <see cref="_ownGoAheadSuppressed"/> is what lets
    /// <see cref="SuppressesOutboundGoAhead"/> go false again, so a prompt sent afterwards may end
    /// with <c>IAC GA</c> once more -- "may", because RFC 854 permits a party to never actually send
    /// one ("GAs may be sent at any time, but need not ever be sent"); this library will, once nothing
    /// else suppresses it.
    /// </remarks>
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

    #endregion
}
