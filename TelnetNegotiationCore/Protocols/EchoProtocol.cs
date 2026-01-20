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
    private bool? _willEcho = null;

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
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Echo state machine");
        
        // Register Echo protocol handlers with the context
        context.SetSharedState("Echo_Protocol", this);
        
        // Configure state machine transitions for Echo protocol
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            // Server side: Can offer to echo for the client (WILL ECHO) or be requested to echo (DO ECHO)
            stateMachine.Configure(State.Do)
                .Permit(Trigger.ECHO, State.DoECHO);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.ECHO, State.DontECHO);

            stateMachine.Configure(State.DoECHO)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await OnDoEchoAsync(x, context));

            stateMachine.Configure(State.DontECHO)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDontEchoAsync(context));

            // Server can announce willingness to echo
            context.RegisterInitialNegotiation(async () => await WillingEchoAsync(context));
        }
        else
        {
            // Client side: Can request server to echo (DO ECHO) or respond to server's offer (WILL ECHO)
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.ECHO, State.WillECHO);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.ECHO, State.WontECHO);

            stateMachine.Configure(State.WontECHO)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await WontEchoAsync(context));

            stateMachine.Configure(State.WillECHO)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await OnWillEchoAsync(x, context));
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Echo Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Echo Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Echo Protocol disabled");
        _willEcho = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Enables echoing for the connection
    /// </summary>
    public async ValueTask EnableEchoAsync()
    {
        if (!IsEnabled)
            return;

        var previousState = _willEcho;
        _willEcho = true;
        Context.Logger.LogInformation("Echo enabled");
        
        if (previousState != _willEcho && _onEchoStateChanged != null)
            await _onEchoStateChanged(true);
    }

    /// <summary>
    /// Disables echoing for the connection
    /// </summary>
    public async ValueTask DisableEchoAsync()
    {
        if (!IsEnabled)
            return;

        var previousState = _willEcho;
        _willEcho = false;
        Context.Logger.LogInformation("Echo disabled");
        
        if (previousState != _willEcho && _onEchoStateChanged != null)
            await _onEchoStateChanged(false);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _willEcho = null;
        _echoHandler = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Default echo handler that sends received bytes back to the client.
    /// Only echoes when echo is enabled.
    /// </summary>
    private async ValueTask DefaultEchoHandlerAsync(byte b, System.Text.Encoding encoding)
    {
        if (!IsEnabled || !IsEchoing)
            return;

        Context.Logger.LogTrace("Echoing byte: {Byte}", b);
        await Context.SendNegotiationAsync(new byte[] { b });
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
            await _echoHandler(b, encoding);
        }
    }

    #region State Machine Handlers

    private async ValueTask OnDontEchoAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client doesn't want server to echo - disabling echo");
        var previousState = _willEcho;
        _willEcho = false;
        
        if (previousState != _willEcho && _onEchoStateChanged != null)
            await _onEchoStateChanged(false);
    }

    private async ValueTask WontEchoAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server won't echo - disabling echo");
        var previousState = _willEcho;
        _willEcho = false;
        
        if (previousState != _willEcho && _onEchoStateChanged != null)
            await _onEchoStateChanged(false);
    }

    private async ValueTask WillingEchoAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to ECHO!");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
    }

    private async ValueTask OnDoEchoAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Client requests server to echo - enabling echo");
        var previousState = _willEcho;
        _willEcho = true;
        
        if (previousState != _willEcho && _onEchoStateChanged != null)
            await _onEchoStateChanged(true);
    }

    private async ValueTask OnWillEchoAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Server will echo - client accepting");
        var previousState = _willEcho;
        _willEcho = true;
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        
        if (previousState != _willEcho && _onEchoStateChanged != null)
            await _onEchoStateChanged(true);
    }

    #endregion
}
