using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Echo protocol plugin (RFC 857)
/// Allows control over whether the server or client echoes characters back
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnEchoStateChanged"/> to set up
/// the callback that will be notified when echo state changes.
/// To enable automatic echoing, call <see cref="UseDefaultEchoHandler"/> which will echo received bytes back to the client.
/// </remarks>
[RequiredMethod("OnEchoStateChanged", Description = "Configure the callback to handle echo state changes (optional but recommended)")]
public class EchoProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willEcho = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO };
    private static readonly byte[] s_doEcho = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO };
    private static readonly byte[] s_wontEcho = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.ECHO };
    private static readonly byte[] s_dontEcho = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.ECHO };

    private bool? _willEcho = null;

    // True while a WILL/WONT (server) or DO/DONT (client) this side sent from EnableEchoAsync or
    // DisableEchoAsync is unanswered: RFC 1143's WANTYES/WANTNO. The peer's next ECHO verb is the
    // answer to it, and an answer is not answered again.
    private bool _awaitingAnswer;

    private Func<bool, ValueTask>? _onEchoStateChanged;
    
    private Func<byte, System.Text.Encoding, ValueTask>? _echoHandler;

    /// <summary>
    /// Sets the callback that is invoked when echo state changes.
    /// </summary>
    /// <param name="callback">The callback to handle echo state changes (receives true if echoing is enabled, false otherwise)</param>
    /// <returns>This instance for fluent chaining</returns>
    public EchoProtocol OnEchoStateChanged(Func<bool, ValueTask>? callback)
    {
        _onEchoStateChanged = callback;
        return this;
    }

    /// <summary>
    /// Configures this protocol to use the default echo handler.
    /// When echo is enabled (server mode), received bytes will automatically be sent back to the client.
    /// </summary>
    /// <returns>This instance for fluent chaining</returns>
    public EchoProtocol UseDefaultEchoHandler()
    {
        _echoHandler = DefaultEchoHandlerAsync;
        return this;
    }

    /// <summary>
    /// Sets a custom echo handler to process bytes when echo is enabled.
    /// </summary>
    /// <param name="handler">Custom handler that receives byte and encoding</param>
    /// <returns>This instance for fluent chaining</returns>
    public EchoProtocol WithEchoHandler(Func<byte, System.Text.Encoding, ValueTask>? handler)
    {
        _echoHandler = handler;
        return this;
    }

    /// <summary>
    /// Gets the echo handler for processing bytes. This can be installed as the interpreter's CallbackOnByteAsync.
    /// </summary>
    /// <returns>The echo handler function, or null if not configured</returns>
    public Func<byte, System.Text.Encoding, ValueTask>? GetEchoHandler() => _echoHandler;

    /// <summary>
    /// Indicates whether this end is echoing characters (true = this end echoes, false = this end does not echo)
    /// </summary>
    public bool IsEchoing => _willEcho == true;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(EchoProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Echo";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

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
            context.RegisterInitialNegotiation(async () => await WillingEchoAsync(context));
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Echo Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Echo Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Echo Protocol disabled");
        _willEcho = false;
        return default(ValueTask);
    }

    /// <summary>
    /// Turns echo on and tells the peer: a server sends <c>WILL ECHO</c>, a client <c>DO ECHO</c>.
    /// Nothing is sent while echo is already on (RFC 854).
    /// </summary>
    /// <remarks>
    /// A server calls this after a password prompt. Changing the state without telling the peer
    /// left a client that had turned its local echo off for the password with no echo at all.
    /// </remarks>
    public async ValueTask EnableEchoAsync()
    {
        if (!IsEnabled || _willEcho == true)
            return;

        _willEcho = true;
        _awaitingAnswer = true;
        Context.Logger.LogInformation("Echo enabled");
        await Context.SendNegotiationAsync(Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client ? s_doEcho : s_willEcho);

        if (_onEchoStateChanged != null)
            await _onEchoStateChanged(true);
    }

    /// <summary>
    /// Turns echo off and tells the peer: a server sends <c>WONT ECHO</c>, a client <c>DONT ECHO</c>.
    /// Nothing is sent while echo is already off. A server's initial <c>WILL ECHO</c> that is still
    /// unanswered counts as on, so the peer does not go on believing the offer stands.
    /// </summary>
    /// <remarks>
    /// A server calls this before a password prompt, so the client stops echoing what is typed.
    /// </remarks>
    public async ValueTask DisableEchoAsync()
    {
        if (!IsEnabled || _willEcho == false)
            return;

        var previousState = _willEcho;
        _willEcho = false;
        _awaitingAnswer = true;
        Context.Logger.LogInformation("Echo disabled");
        await Context.SendNegotiationAsync(Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client ? s_dontEcho : s_wontEcho);

        if (previousState == true && _onEchoStateChanged != null)
            await _onEchoStateChanged(false);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _willEcho = null;
        _awaitingAnswer = false;
        _echoHandler = null;
        return default(ValueTask);
    }

    /// <summary>
    /// Default echo handler that sends received bytes back to the client.
    /// Only echoes when echo is enabled.
    /// </summary>
    private ValueTask DefaultEchoHandlerAsync(byte b, System.Text.Encoding encoding)
    {
        if (!IsEnabled || !IsEchoing)
            return default(ValueTask);

        Context.Logger.LogTrace("Echoing byte: {Byte}", b);

        // The byte handed to us has already been un-escaped by the interpreter, so a peer's
        // correctly-doubled IAC IAC arrives here as a single 0xFF. RFC 854: "only the IAC need be
        // doubled to be sent as data". Echoing it raw put a bare IAC back on the wire and desynced
        // the peer's parser - reachable from one ordinary keypress (U+00FF in ISO-8859-1, я in
        // CP1251, Ъ in KOI8-R). Reuse the same escaping SendAsync applies rather than a second copy.
        return Context.SendNegotiationAsync(Context.Interpreter.TelnetSafeBytes([b]));
    }

    /// <summary>
    /// Processes a byte through the echo handler if configured and echo is enabled.
    /// This should be called from the interpreter's byte processing pipeline.
    /// </summary>
    /// <param name="b">The byte to process</param>
    /// <param name="encoding">The current encoding</param>
    public async ValueTask ProcessByteAsync(byte b, System.Text.Encoding encoding)
    {
        if (_echoHandler != null && IsEnabled && IsEchoing)
        {
            await _echoHandler(b, encoding).ConfigureAwait(false);
        }
    }

    #region State Machine Handlers

    /// <summary>
    /// What arriving at DO/DONT (server) or WILL/WONT (client) for ECHO does -- each mode only ever
    /// sees one direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gated on which role this side has, because every handler below was written for one direction
    /// only — <see cref="OnDoEchoAsync"/> is the server accepting a client's request, and
    /// <see cref="OnWillEchoAsync"/> is the client accepting a server's offer. Running either in the
    /// wrong role is not merely useless: it answers wrongly or not at all. RFC 857 requires an answer
    /// either way, since a <c>DO</c> is met with <c>WILL</c> or <c>WONT</c>.
    /// </para>
    /// <para>
    /// Wrong-direction offers are refused here rather than left to the interpreter's
    /// unsupported-option refusal, which cannot reach them: that runs only when no plugin claims the
    /// option, and a registered <see cref="EchoProtocol"/> claims it. v3.0.0 got the refusal for free
    /// precisely because it had no handler for those directions; this has to send it explicitly. See
    /// <see href="https://github.com/HarryCordewener/TelnetNegotiationCore/issues/118">#118</see>.
    /// </para>
    /// </remarks>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        var client = context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client;
        var answering = _awaitingAnswer;
        _awaitingAnswer = false;

        // RFC 1143: the peer may not refuse to turn an option off. A DO or WILL answering this
        // side's own WONT or DONT is an error and is dropped, not taken as a new request.
        if (answering && _willEcho == false
            && ((verb == (byte)Trigger.DO && !client) || (verb == (byte)Trigger.WILL && client)))
        {
            context.Logger.LogDebug("Peer answered a disable of ECHO with a request to enable it; ignoring");
            return;
        }

        switch (verb)
        {
            // A client asks the server to echo, and the server accepts. It announced WILL ECHO on
            // initialisation, so RFC 1143 does not want that repeated here.
            case (byte)Trigger.DO when !client:
                await OnDoEchoAsync(context);
                break;

            case (byte)Trigger.DONT when !client:
                await OnDontEchoAsync(context, answering);
                break;

            // A server offers to echo, and the client accepts with DO ECHO.
            case (byte)Trigger.WILL when client:
                await OnWillEchoAsync(context);
                break;

            case (byte)Trigger.WONT when client:
                await WontEchoAsync(context, answering);
                break;

            // A peer asking this client to echo. RFC 857 allows either side to echo -- "neither,
            // either, or both directions may be operating simultaneously in echo mode" -- so this is
            // not refused because clients may not echo. It is refused because this one does not
            // implement echoing, and the RFC's default condition is WONT ECHO: agreeing to something
            // it will not do would be worse than saying no.
            case (byte)Trigger.DO:
                context.Logger.LogDebug("A peer asked this client to echo; refusing, this client does not echo");
                await Helpers.OptionNegotiation.AnswerAsync(
                    honour: false, (byte)Trigger.DO, (byte)Trigger.ECHO, context);
                break;

            // A peer offering to echo to this server. RFC 857 permits accepting, but this server
            // announced WILL ECHO on initialisation, so it is already echoing -- and the RFC is
            // explicit about that combination: "if BOTH hosts enter the mode of echoing characters
            // transmitted by the other host, then any character transmitted in either direction will
            // be echoed back and forth indefinitely ... care should be taken in each implementation
            // that if one site is echoing, echoing is not permitted to be turned on at the other."
            // Refusing is that care.
            case (byte)Trigger.WILL:
                context.Logger.LogDebug("A peer offered to echo to this server, which already echoes; refusing to avoid an echo loop");
                await Helpers.OptionNegotiation.AnswerAsync(
                    honour: false, (byte)Trigger.WILL, (byte)Trigger.ECHO, context);
                break;

            // A refusal in the wrong direction needs no answer: refusing a refusal is not an
            // exchange, which is the same rule the interpreter's own RefuseAsync follows.
            case (byte)Trigger.DONT:
            case (byte)Trigger.WONT:
                break;
        }
    }

    // The four handlers below follow RFC 1143's rules for an option that is on or off, which RFC 854
    // states as two sentences: "if a party receives what appears to be a request to enter some mode
    // it is already in, the request should not be acknowledged", and a request to leave a mode that
    // is on is acknowledged, because the other side cannot tell an unanswered disable from a lost
    // one. _willEcho is that state: true on, false off, null not yet negotiated (off, except that a
    // server has offered WILL ECHO on initialisation and is waiting for the answer).

    /// <summary>
    /// Server: the client asks it to stop echoing. Acknowledged with <c>WONT ECHO</c> only when echo
    /// was on (RFC 1143: us=YES, DONT → us=NO, send WONT). A <c>DONT</c> answering the initial
    /// <c>WILL ECHO</c>, or one sent by <see cref="EnableEchoAsync"/>, is a refusal of that offer,
    /// and a refusal is not answered.
    /// </summary>
    private async ValueTask OnDontEchoAsync(IProtocolContext context, bool answering)
    {
        context.Logger.LogDebug("Client doesn't want server to echo - disabling echo");
        var previousState = _willEcho;
        _willEcho = false;
        await OnNegotiatedAsync(false);

        if (previousState == true && !answering)
            await context.SendNegotiationAsync(s_wontEcho);

        if (previousState != _willEcho && _onEchoStateChanged != null)
            await _onEchoStateChanged(false);
    }

    /// <summary>
    /// Client: the server stops echoing. Acknowledged with <c>DONT ECHO</c> only when the server was
    /// echoing (RFC 1143: him=YES, WONT → him=NO, send DONT). A <c>WONT</c> while echo is already off
    /// is a mode this side is already in, and is not answered.
    /// </summary>
    private async ValueTask WontEchoAsync(IProtocolContext context, bool answering)
    {
        context.Logger.LogDebug("Server won't echo - disabling echo");
        var previousState = _willEcho;
        _willEcho = false;
        await OnNegotiatedAsync(false);

        if (previousState == true && !answering)
            await context.SendNegotiationAsync(s_dontEcho);

        if (previousState != _willEcho && _onEchoStateChanged != null)
            await _onEchoStateChanged(false);
    }

    private async ValueTask WillingEchoAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to ECHO!");
        await context.SendNegotiationAsync(s_willEcho);
    }

    /// <summary>
    /// Server: the client asks it to echo. The answer to its own initial <c>WILL ECHO</c> is agreement
    /// and is not answered again; while echo is on a repeat is not answered either. After echo was
    /// turned off, a <c>DO</c> is a new request and is accepted with <c>WILL ECHO</c> (RFC 1143:
    /// us=NO, DO → us=YES, send WILL).
    /// </summary>
    private async ValueTask OnDoEchoAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client requests server to echo - enabling echo");
        var previousState = _willEcho;
        if (previousState == true)
            return;

        _willEcho = true;
        await OnNegotiatedAsync(true);

        if (previousState == false)
            await context.SendNegotiationAsync(s_willEcho);

        if (_onEchoStateChanged != null)
            await _onEchoStateChanged(true);
    }

    /// <summary>
    /// Client: the server offers to echo, and is accepted with <c>DO ECHO</c> — once. A repeated
    /// <c>WILL ECHO</c> while the server is already echoing asks for a mode this side is already in
    /// and is not answered (RFC 854; RFC 1143: him=YES, WILL → ignore), since answering each one is
    /// how two implementations loop.
    /// </summary>
    private async ValueTask OnWillEchoAsync(IProtocolContext context)
    {
        if (_willEcho == true)
        {
            context.Logger.LogDebug("Server will echo - already echoing, not answered");
            return;
        }

        context.Logger.LogDebug("Server will echo - client accepting");
        _willEcho = true;
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_doEcho);

        if (_onEchoStateChanged != null)
            await _onEchoStateChanged(true);
    }

    #endregion
}
