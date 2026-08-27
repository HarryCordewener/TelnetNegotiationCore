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

    // Cached from ConfigureStateMachine's context parameter (which always runs before
    // InitializeAsync) so SuppressesOutboundGoAhead below can answer without Context throwing for a
    // plugin that was configured but never fully initialized.
    private Interpreters.TelnetInterpreter.TelnetMode? _mode;

    // The peer's SUPPRESS-GO-AHEAD state -- never this end's own direction; see State.GoAhead below.
    private bool? _doGA = true;

    // This end's own SUPPRESS-GO-AHEAD state, client mode only. RFC 858 §5 requires the two
    // directions negotiated independently, so this cannot reuse _doGA. Starts false (RFC 858 §3's
    // default), so an opening DONT SUPPRESS-GO-AHEAD is silently absorbed per RFC 854 §3(b).
    private bool _ownGoAheadSuppressed;

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
    /// Not <c>_doGA</c> or <see cref="OwnGoAheadSuppressed"/> directly: <c>_doGA</c> tracks this
    /// end's own direction in server mode but the peer's in client mode, where
    /// <see cref="OwnGoAheadSuppressed"/> is the field that actually tracks this end. Not
    /// <see cref="IsGoAheadSuppressed"/> either -- that answers whether an inbound GA still means a
    /// prompt, the peer's direction, not this one.
    /// </remarks>
    public bool SuppressesOutboundGoAhead =>
        _mode == Interpreters.TelnetInterpreter.TelnetMode.Server
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
        _mode = context.Mode;
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

            // The other direction: a server asking us to suppress our own outbound Go-Ahead.
            // RFC 858 §5 requires the two directions negotiated independently. Reuses the server
            // branch's DoSUPPRESSGOAHEAD/DontSUPPRESSGOAHEAD states rather than duplicating them --
            // only one branch of this `if` ever configures a given interpreter instance.
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
        // RFC 1123 §3.2.2: "A User or Server Telnet MUST always accept negotiation of the Suppress
        // Go Ahead option."
        context.Logger.LogDebug("Server supports Suppress Go-Ahead.");
        _doGA = false;
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

    #endregion
}
