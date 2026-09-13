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

    private bool? _doEOR = null;

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
    /// Indicates whether EOR is enabled
    /// </summary>
    public bool IsEOREnabled => _doEOR == true;

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
        Context.Logger.LogInformation("EOR Protocol enabled");
        _doEOR = true;
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("EOR Protocol disabled");
        _doEOR = false;
        return default(ValueTask);
    }

    /// <summary>
    /// Enables EOR for the connection
    /// </summary>
    public ValueTask EnableEORAsync()
    {
        if (!IsEnabled)
            return default(ValueTask);

        _doEOR = true;
        Context.Logger.LogInformation("EOR enabled for connection");
        return default(ValueTask);
    }

    /// <summary>
    /// Disables EOR for the connection
    /// </summary>
    public ValueTask DisableEORAsync()
    {
        if (!IsEnabled)
            return default(ValueTask);

        _doEOR = false;
        Context.Logger.LogInformation("EOR disabled for connection");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _doEOR = null;
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
    /// The condition is <see cref="IsEOREnabled"/> — the negotiated state — and not
    /// <see cref="TelnetProtocolPluginBase.IsEnabled"/>, which is the guard
    /// <see cref="OnPromptAsync"/> applies and which is about plugin lifetime: it is true from
    /// initialisation onwards for every registered plugin, so it let an unnegotiated EOR through as
    /// a prompt on every connection that merely had this plugin added.
    /// </para>
    /// </remarks>
    private async ValueTask OnEORPromptAsync()
    {
        if (!IsEOREnabled)
        {
            Context.Logger.LogTrace(
                "EOR received while the END-OF-RECORD option is not in effect. Treating it as a NOP (RFC 885).");
            return;
        }

        Context.Logger.LogDebug("Server is prompting EOR");
        await OnPromptAsync();
    }

    private async ValueTask OnDontEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client won't do EOR - do nothing");
        _doEOR = false;
        await OnNegotiatedAsync(false);
    }

    private async ValueTask WontEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server won't do EOR - do nothing");
        _doEOR = false;
        await OnNegotiatedAsync(false);
    }

    private async ValueTask WillingEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to EOR!");
        await context.SendNegotiationAsync(s_willEor);
    }

    private async ValueTask OnDoEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Peer agreed to End of Record.");
        _doEOR = true;
        await OnNegotiatedAsync(true);
    }

    /// <summary>
    /// An unsolicited <c>DO EOR</c>: the peer is asking this end to mark its records, and this end
    /// agrees. See <see cref="OnPeerNegotiatedAsync"/> for why the answer is owed and why it is
    /// <c>WILL</c>.
    /// </summary>
    private async ValueTask OnAskedToSendEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Peer asked this end to mark records with End of Record. Agreeing.");
        _doEOR = true;
        await OnNegotiatedAsync(true);
        await Helpers.OptionNegotiation.AnswerAsync(
            honour: true, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR, context);
    }

    private async ValueTask OnWillEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server supports End of Record.");
        _doEOR = true;
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_doEor);
    }

    #endregion
}
