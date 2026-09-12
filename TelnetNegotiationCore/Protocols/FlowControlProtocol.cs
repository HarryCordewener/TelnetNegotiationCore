using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Flow Control protocol plugin (RFC 1372)
/// Allows remote control of flow control settings (XON/XOFF)
/// </summary>
/// <remarks>
/// This protocol implements RFC 1372 - Telnet Remote Flow Control Option.
/// It allows the server to remotely enable/disable flow control and configure
/// restart behavior (restart on any character vs. only on XON).
/// 
/// Flow control is primarily used for software flow control where XOFF (Ctrl-S)
/// stops output and XON (Ctrl-Q) restarts it.
/// </remarks>
public class FlowControlProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willFlowControl = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL };
    private static readonly byte[] s_doFlowControl = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL };

    // Flow control command constants per RFC 1372
    private const int CMD_OFF = 0;
    private const int CMD_ON = 1;
    private const int CMD_RESTART_ANY = 2;
    private const int CMD_RESTART_XON = 3;
    
    private bool _flowControlEnabled = false;
    private FlowControlRestartMode _restartMode = FlowControlRestartMode.SystemDefault;
    
    private Func<bool, ValueTask>? _onFlowControlStateChanged;
    private Func<FlowControlRestartMode, ValueTask>? _onRestartModeChanged;

    /// <summary>
    /// Flow control restart modes
    /// </summary>
    public enum FlowControlRestartMode
    {
        /// <summary>
        /// System-dependent default (typically RESTART_XON)
        /// </summary>
        SystemDefault = 0,
        
        /// <summary>
        /// Any character (except XOFF) can restart output
        /// </summary>
        RestartAny = 1,
        
        /// <summary>
        /// Only XON character can restart output
        /// </summary>
        RestartXON = 2
    }

    /// <summary>
    /// Gets whether flow control is currently enabled
    /// </summary>
    public bool IsFlowControlEnabled => _flowControlEnabled;

    /// <summary>
    /// Gets the current restart mode
    /// </summary>
    public FlowControlRestartMode RestartMode => _restartMode;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(FlowControlProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Flow Control (RFC 1372)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Sets the callback that is invoked when flow control state changes.
    /// </summary>
    /// <param name="callback">The callback to handle flow control state changes (receives true if enabled, false otherwise)</param>
    /// <returns>This instance for fluent chaining</returns>
    public FlowControlProtocol OnFlowControlStateChanged(Func<bool, ValueTask>? callback)
    {
        _onFlowControlStateChanged = callback;
        return this;
    }

    /// <summary>
    /// Sets the callback that is invoked when restart mode changes.
    /// </summary>
    /// <param name="callback">The callback to handle restart mode changes</param>
    /// <returns>This instance for fluent chaining</returns>
    public FlowControlProtocol OnRestartModeChanged(Func<FlowControlRestartMode, ValueTask>? callback)
    {
        _onRestartModeChanged = callback;
        return this;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Negotiation acceptance and the subnegotiation are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/> and <see cref="OnFlowControlCommandAsync"/>); this hook
    /// survives only to register the server's initial offer, a cross-cutting mechanism independent
    /// of which machine drives byte processing.
    /// </remarks>
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        if (context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Client)
        {
            context.RegisterInitialNegotiation(async () => await SendDoFlowControlAsync(context));
        }
    }

    private async ValueTask OnWillFlowControlAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Client is willing to toggle flow control");
        await OnNegotiatedAsync(true);
    }

    private async ValueTask OnWontFlowControlAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Client won't toggle flow control");
        await OnNegotiatedAsync(false);
    }

    /// <summary>
    /// What arriving at WILL/WONT/DO/DONT for FLOWCONTROL does. Only one direction has a real
    /// handler in a given mode; the other two verbs are no-ops for that mode.
    /// </summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        var client = context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client;
        switch (verb)
        {
            case (byte)Trigger.WILL when !client:
                await OnWillFlowControlAsync(context);
                break;
            case (byte)Trigger.WONT when !client:
                await OnWontFlowControlAsync(context);
                break;
            case (byte)Trigger.DO when client:
                await WillFlowControlAsync(context);
                break;
            case (byte)Trigger.DONT when client:
                await OnDontFlowControlAsync(context);
                break;
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Flow Control Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Flow Control Protocol enabled");
        // Flow control state will be set by WillFlowControlAsync when negotiation completes
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override async ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Flow Control Protocol disabled");
        await SetFlowControlStateAsync(false);
        _restartMode = FlowControlRestartMode.SystemDefault;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _flowControlEnabled = false;
        _restartMode = FlowControlRestartMode.SystemDefault;
        _onFlowControlStateChanged = null;
        _onRestartModeChanged = null;
        return default(ValueTask);
    }

    #region Public API Methods

    /// <summary>
    /// Sends a command to enable flow control (server mode only)
    /// </summary>
    public async ValueTask EnableFlowControlAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        Context.Logger.LogDebug("Sending command to enable flow control");
        await Context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_ON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    /// <summary>
    /// Sends a command to disable flow control (server mode only)
    /// </summary>
    public async ValueTask DisableFlowControlAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        Context.Logger.LogDebug("Sending command to disable flow control");
        await Context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_OFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    /// <summary>
    /// Sends a command to set restart mode to RESTART-ANY (server mode only)
    /// </summary>
    public async ValueTask SetRestartAnyAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        Context.Logger.LogDebug("Sending command to set RESTART-ANY mode");
        await Context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_ANY,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    /// <summary>
    /// Sends a command to set restart mode to RESTART-XON (server mode only)
    /// </summary>
    public async ValueTask SetRestartXONAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        Context.Logger.LogDebug("Sending command to set RESTART-XON mode");
        await Context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_XON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    #endregion

    #region State Machine Handlers

    private async ValueTask WillFlowControlAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the server, Willing to toggle flow control.");
        await context.SendNegotiationAsync(s_willFlowControl);
        await OnNegotiatedAsync(true);

        // Per RFC 1372, flow control should be enabled when WILL is sent
        await SetFlowControlStateAsync(true);
    }

    private async ValueTask OnDontFlowControlAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Server telling us not to toggle flow control");
        await OnNegotiatedAsync(false);
        await SetFlowControlStateAsync(false);
    }

    private async ValueTask SendDoFlowControlAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the client to toggle flow control.");
        await context.SendNegotiationAsync(s_doFlowControl);
    }

    internal async ValueTask OnFlowControlCommandAsync(int command, IProtocolContext context)
    {
        if (command < CMD_OFF || command > CMD_RESTART_XON)
        {
            context.Logger.LogWarning("Invalid flow control command: {Command}", command);
            return;
        }

        context.Logger.LogDebug("Connection: {ConnectionState}: Command {Command}",
            "Received Flow Control subnegotiation", command);

        switch (command)
        {
            case CMD_OFF:
                await SetFlowControlStateAsync(false);
                break;
            case CMD_ON:
                await SetFlowControlStateAsync(true);
                break;
            case CMD_RESTART_ANY:
                await SetRestartModeAsync(FlowControlRestartMode.RestartAny);
                break;
            case CMD_RESTART_XON:
                await SetRestartModeAsync(FlowControlRestartMode.RestartXON);
                break;
        }
    }

    private async ValueTask SetFlowControlStateAsync(bool enabled)
    {
        if (_flowControlEnabled == enabled)
            return;

        _flowControlEnabled = enabled;
        Context.Logger.LogInformation("Flow control {State}", enabled ? "enabled" : "disabled");

        if (_onFlowControlStateChanged != null)
            await _onFlowControlStateChanged(enabled);
    }

    private async ValueTask SetRestartModeAsync(FlowControlRestartMode mode)
    {
        if (_restartMode == mode)
            return;

        _restartMode = mode;
        Context.Logger.LogInformation("Flow control restart mode set to {Mode}", mode);

        if (_onRestartModeChanged != null)
            await _onRestartModeChanged(mode);
    }

    #endregion
}
