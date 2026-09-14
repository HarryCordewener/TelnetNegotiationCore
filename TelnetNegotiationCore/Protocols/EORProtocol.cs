using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// EOR (End of Record) protocol plugin
/// Used for prompting without requiring Go-Ahead
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnPrompt"/> to set up
/// the callback that will handle EOR prompts if you need to be notified when prompts are received.
/// </remarks>
[RequiredMethod("OnPrompt", Description = "Configure the callback to handle prompt events (optional but recommended)")]
public class EORProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willEor = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR };
    private static readonly byte[] s_doEor = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR };

    // The peer's END-OF-RECORD state: whether the peer has agreed to send EOR when it transmits.
    // Established by the peer's WILL/WONT, per RFC 885's "IAC WILL END-OF-RECORD: The sender of this
    // command requests permission to begin transmission of the Telnet END-OF-RECORD (EOR) code".
    private bool _peerMarksRecords;

    // This end's own END-OF-RECORD state: whether this end has agreed to mark its own records.
    // Established by the peer's DO/DONT, per RFC 885's "IAC DO END-OF-RECORD: The sender of this
    // command requests that the sender of data start transmitting the EOR code".
    private bool _marksOutboundRecords;

    private Func<ValueTask>? _onPromptReceived;

    /// <summary>
    /// Sets the callback that is invoked when a prompt is received (EOR marker).
    /// </summary>
    /// <remarks>
    /// Runs on the byte-processing loop — the same thread Suppress Go-Ahead's and Packet Patch's
    /// prompt callbacks run on, so a handler shared across all three (as
    /// <c>AddDefaultMUDProtocols</c> does when given one) needs no thread-safety of its own on that
    /// account.
    /// </remarks>
    /// <param name="callback">The callback to handle prompts</param>
    /// <returns>This instance for fluent chaining</returns>
    public EORProtocol OnPrompt(Func<ValueTask>? callback)
    {
        _onPromptReceived = callback;
        return this;
    }



    /// <summary>
    /// Whether the peer has agreed to send <c>IAC EOR</c> when it transmits -- the direction that
    /// decides whether an inbound <c>IAC EOR</c> still means a prompt.
    /// </summary>
    /// <remarks>
    /// Set by the peer's <c>WILL</c> and cleared by its <c>WONT</c>. Independent of
    /// <see cref="MarksOutboundRecords"/>, this end's own direction, because RFC 885 is explicit
    /// that "the use of EORs must be negotiated independently for each direction".
    /// </remarks>
    public bool PeerMarksRecords => _peerMarksRecords;

    /// <summary>
    /// Whether <em>this</em> end has agreed to mark its own records with <c>IAC EOR</c> -- the
    /// direction <c>TelnetInterpreter.PromptTerminator</c> needs when deciding whether an outbound
    /// prompt may end with <c>IAC EOR</c>. Independent of <see cref="PeerMarksRecords"/>, the peer's
    /// direction.
    /// </summary>
    /// <remarks>
    /// Set by the peer's <c>DO</c> -- which asks this end to send the marker, not the other way
    /// round: "IAC DO END-OF-RECORD: The sender of this command requests that the sender of data
    /// start transmitting the EOR code when transmitting data" -- and cleared by its <c>DONT</c>.
    /// </remarks>
    public bool MarksOutboundRecords => _marksOutboundRecords;

    /// <summary>
    /// Whether EOR is in effect in either direction.
    /// </summary>
    /// <remarks>
    /// The aggregate, and deliberately so: this is what the single flag behind it reported before the
    /// two directions were separated, so a consumer reading it sees exactly the value it saw before.
    /// It is the wrong question for either of the two decisions the library itself makes -- an
    /// outbound prompt's terminator needs <see cref="MarksOutboundRecords"/>, and an inbound bare
    /// <c>IAC EOR</c> needs <see cref="PeerMarksRecords"/> -- so prefer whichever of those matches
    /// the direction you mean.
    /// </remarks>
    public bool IsEOREnabled => _peerMarksRecords || _marksOutboundRecords;

    /// <summary>
    /// Reports the aggregate to <see cref="TelnetProtocolPluginBase.OnNegotiatedAsync"/>, so that a
    /// refusal in one direction does not clear <c>IsNegotiated</c> while the other is still live.
    /// </summary>
    /// <remarks>
    /// <see cref="MCCPProtocol"/> does the same for the same reason, and for the same reason it
    /// cannot be skipped: <c>OnNegotiatedAsync</c> is transition-only, so handing it one direction's
    /// own outcome would let the second direction to resolve stomp the first. A <c>WONT</c> arriving
    /// after a <c>DO</c> would report "not negotiated" while this end is still, correctly, marking
    /// every prompt it sends.
    /// </remarks>
    private ValueTask ReportNegotiatedAsync() => OnNegotiatedAsync(IsEOREnabled);

    /// <inheritdoc />
    public override Type ProtocolType => typeof(EORProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "EOR (End of Record)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();
    // Note: EOR and SuppressGA often work together as fallbacks
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
            context.RegisterInitialNegotiation(async () => await WillingEORAsync(context));
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("EOR Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        // No negotiated state written here. Enabling the plugin is not a negotiation, and writing
        // "EOR is on" from a lifecycle hook fabricated an agreement no peer had made -- reachable by
        // disabling and re-enabling the plugin through the manager. SuppressGoAheadProtocol's
        // equivalents write nothing for the same reason.
        Context.Logger.LogInformation("EOR Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("EOR Protocol disabled");
        return default(ValueTask);
    }

    /// <summary>
    /// Enables EOR for the connection
    /// </summary>
    public ValueTask EnableEORAsync()
    {
        if (!IsEnabled)
            return default(ValueTask);

        _marksOutboundRecords = true;
        Context.Logger.LogInformation("EOR enabled for this end's outbound records");
        return default(ValueTask);
    }

    /// <summary>
    /// Disables EOR for the connection
    /// </summary>
    public ValueTask DisableEORAsync()
    {
        if (!IsEnabled)
            return default(ValueTask);

        _marksOutboundRecords = false;
        Context.Logger.LogInformation("EOR disabled for this end's outbound records");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _peerMarksRecords = false;
        _marksOutboundRecords = false;
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

        Context.Logger.LogDebug("Server is prompting with EOR");

        Context.Interpreter.TakePartialLineAsPrompt(marked: true);

        if (_onPromptReceived != null)
            await _onPromptReceived().ConfigureAwait(false);
    }

    #region State Machine Handlers

    /// <summary>
    /// What arriving at DO/DONT or WILL/WONT for TELOPT_EOR does, in either role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>DO</c> means two different things depending on whether this end already offered. A server
    /// announced <c>WILL EOR</c> on initialisation (see <see cref="ConfigureStateMachine"/>), so the
    /// <c>DO</c> that follows is the peer <em>agreeing</em> to that offer, and RFC 1143 has an
    /// agreement noted rather than answered -- answering it would invite the loop the RFC warns
    /// about. A client made no such offer, so the same <c>DO</c> is an unsolicited request, and there
    /// RFC 1143 is equally clear that an answer is owed: "a TELNET implementation MUST refuse
    /// (DONT/WONT) a request to enable an option for which it does not comply with the appropriate
    /// protocol specification". Silence is not one of the choices, and a client used to give it.
    /// </para>
    /// <para>
    /// The answer is <c>WILL</c>, because this end genuinely complies. RFC 885 asks the receiver of a
    /// <c>DO</c> to emit the marker itself -- "the sender of this command requests that the sender of
    /// data start transmitting the EOR code when transmitting data" -- and this library does, in
    /// either role: <c>SendPromptAsync</c> terminates a prompt with <c>IAC EOR</c> whenever the
    /// option is in effect, with no reference to the interpreter's mode. The option is also
    /// per-direction rather than symmetric -- "the use of EORs must be negotiated independently for
    /// each direction" -- so being asked to send EOR carries no claim about what the peer will send.
    /// </para>
    /// </remarks>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        var weAlreadyOffered = context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server;

        switch (verb)
        {
            case (byte)Trigger.DO when weAlreadyOffered:
                await OnDoEORAsync(context);
                break;
            case (byte)Trigger.DO:
                await OnAskedToSendEORAsync(context);
                break;
            case (byte)Trigger.DONT:
                await OnDontEORAsync(context);
                break;
            case (byte)Trigger.WILL:
                await OnWillEORAsync(context);
                break;
            case (byte)Trigger.WONT:
                await WontEORAsync(context);
                break;
        }
    }

    /// <summary>A bare IAC EOR. Delegates to the same guarded prompt logic.</summary>
    internal ValueTask OnBareEorAsync() => OnEORPromptAsync();

    /// <summary>
    /// A bare <c>IAC EOR</c> arrived: a prompt boundary where the option is in effect, and a NOP
    /// where it is not.
    /// </summary>
    /// <remarks>
    /// RFC 885: "When the END-OF-RECORD option is not in effect, the IAC EOR command should be
    /// treated as a NOP if received, although IAC EOR should not normally be sent in this mode."
    /// <para>
    /// The condition is <see cref="PeerMarksRecords"/> — the peer's own direction, since this is
    /// the peer's marker arriving — and not <see cref="IsEOREnabled"/>, which is the aggregate and
    /// would treat an inbound marker as a prompt on the strength of an agreement about the
    /// <em>outbound</em> direction. Nor <see cref="TelnetProtocolPluginBase.IsEnabled"/>, the guard
    /// <see cref="OnPromptAsync"/> applies and which is about plugin lifetime: it is true from
    /// initialisation onwards for every registered plugin, so it let an unnegotiated EOR through as
    /// a prompt on every connection that merely had this plugin added.
    /// </para>
    /// </remarks>
    private async ValueTask OnEORPromptAsync()
    {
        if (!PeerMarksRecords)
        {
            Context.Logger.LogTrace(
                "EOR received while the peer has not agreed to send it. Treating it as a NOP (RFC 885).");
            return;
        }

        Context.Logger.LogDebug("Server is prompting EOR");
        await OnPromptAsync();
    }

    private async ValueTask OnDontEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Peer does not want this end to mark records - leaving its own direction alone");
        _marksOutboundRecords = false;
        await ReportNegotiatedAsync();
    }

    private async ValueTask WontEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Peer will not send EOR - leaving this end's own direction alone");
        _peerMarksRecords = false;
        await ReportNegotiatedAsync();
    }

    private async ValueTask WillingEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to EOR!");
        await context.SendNegotiationAsync(s_willEor);
    }

    private async ValueTask OnDoEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Peer agreed to this end marking its records with End of Record.");
        _marksOutboundRecords = true;
        await ReportNegotiatedAsync();
    }

    /// <summary>
    /// An unsolicited <c>DO EOR</c>: the peer is asking this end to mark its records, and this end
    /// agrees. See <see cref="OnPeerNegotiatedAsync"/> for why the answer is owed and why it is
    /// <c>WILL</c>.
    /// </summary>
    private async ValueTask OnAskedToSendEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Peer asked this end to mark records with End of Record. Agreeing.");
        _marksOutboundRecords = true;
        await ReportNegotiatedAsync();
        await Helpers.OptionNegotiation.AnswerAsync(
            honour: true, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR, context);
    }

    private async ValueTask OnWillEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Peer will send End of Record.");
        _peerMarksRecords = true;
        await ReportNegotiatedAsync();
        await context.SendNegotiationAsync(s_doEor);
    }

    #endregion
}
