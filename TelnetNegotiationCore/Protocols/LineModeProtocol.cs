using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Line Mode protocol plugin (RFC 1184)
/// Allows negotiation of line editing and signal trapping modes
/// </summary>
/// <remarks>
/// This protocol implements RFC 1184 - Telnet Linemode Option.
/// It allows the client and server to negotiate whether line editing
/// should be done locally (on the client) or remotely (on the server).
/// 
/// The protocol supports:
/// - EDIT mode: Client performs line editing locally
/// - TRAPSIG mode: Client traps signals (interrupt, quit, etc.)
/// - MODE_ACK: Mode acknowledgment bit
/// - SOFT_TAB: Soft tab processing
/// - LIT_ECHO: Literal echo of non-printable characters
/// 
/// SLC (Set Local Characters) and FORWARDMASK subnegotiations are not
/// currently implemented but may be added in future versions.
/// </remarks>
public class LineModeProtocol : TelnetProtocolPluginBase
{
    // Mode bit constants per RFC 1184
    private const byte MODE_EDIT = 0x01;
    private const byte MODE_TRAPSIG = 0x02;
    private const byte MODE_ACK = 0x04;
    private const byte MODE_SOFT_TAB = 0x08;
    private const byte MODE_LIT_ECHO = 0x10;
    
    // Subnegotiation type constants
    private const int SUBNEG_TYPE_MODE = 1;
    private const int SUBNEG_TYPE_FORWARDMASK = 2;
    private const int SUBNEG_TYPE_SLC = 3;
    
    private byte _currentMode = 0;
    private bool _lineModeEnabled = false;
    
    private Func<byte, ValueTask>? _onModeChanged;

    /// <summary>
    /// Gets whether line mode is currently enabled
    /// </summary>
    public bool IsLineModeEnabled => _lineModeEnabled;

    /// <summary>
    /// Gets the current line mode settings
    /// </summary>
    public byte CurrentMode => _currentMode;

    /// <summary>
    /// Gets whether EDIT mode is enabled (client does local editing)
    /// </summary>
    public bool IsEditModeEnabled => (_currentMode & MODE_EDIT) != 0;

    /// <summary>
    /// Gets whether TRAPSIG mode is enabled (client traps signals)
    /// </summary>
    public bool IsTrapSigModeEnabled => (_currentMode & MODE_TRAPSIG) != 0;

    /// <summary>
    /// Gets whether SOFT_TAB mode is enabled
    /// </summary>
    public bool IsSoftTabEnabled => (_currentMode & MODE_SOFT_TAB) != 0;

    /// <summary>
    /// Gets whether LIT_ECHO mode is enabled
    /// </summary>
    public bool IsLitEchoEnabled => (_currentMode & MODE_LIT_ECHO) != 0;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(LineModeProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Line Mode (RFC 1184)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Sets the callback that is invoked when line mode settings change.
    /// </summary>
    /// <param name="callback">The callback to handle mode changes (receives the new mode byte)</param>
    /// <returns>This instance for fluent chaining</returns>
    public LineModeProtocol OnModeChanged(Func<byte, ValueTask>? callback)
    {
        _onModeChanged = callback;
        return this;
    }

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Line Mode state machine");
        
        // Register Line Mode protocol handlers with the context
        context.SetSharedState("LineMode_Protocol", this);
        
        // Common state machine configuration
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.LINEMODE, State.WillLINEMODE);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.LINEMODE, State.WontLINEMODE);

        stateMachine.Configure(State.Do)
            .Permit(Trigger.LINEMODE, State.DoLINEMODE);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.LINEMODE, State.DontLINEMODE);
        
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client)
        {
            ConfigureAsClient(stateMachine, context);
        }
        else
        {
            ConfigureAsServer(stateMachine, context);
        }
    }
    
    private void ConfigureAsClient(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        // Client responds to server's DO LINEMODE
        stateMachine.Configure(State.DoLINEMODE)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await WillLineModeAsync(context));

        stateMachine.Configure(State.DontLINEMODE)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await OnDontLineModeAsync(context));

        // Handle subnegotiations: IAC SB LINEMODE MODE <mode> IAC SE
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.LINEMODE, State.AlmostNegotiatingLINEMODE);

        stateMachine.Configure(State.AlmostNegotiatingLINEMODE)
            .Permit(Trigger.LINEMODE_MODE, State.NegotiatingLINEMODE)
            .Permit(Trigger.LINEMODE_FORWARDMASK, State.NegotiatingLINEMODE)
            .Permit(Trigger.LINEMODE_SLC, State.NegotiatingLINEMODE);

        // Configure parameterized trigger handlers to capture the mode data
        var interpreter = context.Interpreter;
        stateMachine.Configure(State.NegotiatingLINEMODE)
            .OnEntryFrom(interpreter.ParameterizedTrigger(Trigger.LINEMODE_MODE), _ => CaptureSubnegotiationType(SUBNEG_TYPE_MODE))
            .OnEntryFrom(interpreter.ParameterizedTrigger(Trigger.LINEMODE_FORWARDMASK), _ => CaptureSubnegotiationType(SUBNEG_TYPE_FORWARDMASK))
            .OnEntryFrom(interpreter.ParameterizedTrigger(Trigger.LINEMODE_SLC), _ => CaptureSubnegotiationType(SUBNEG_TYPE_SLC))
            .Permit(Trigger.IAC, State.CompletingLINEMODE)
            .PermitDynamic(Trigger.ReadNextCharacter, () => State.EvaluatingLINEMODE);

        stateMachine.Configure(State.EvaluatingLINEMODE)
            .OnEntryFromAsync(interpreter.ParameterizedTrigger(Trigger.ReadNextCharacter), async (b) => await CaptureLineModeDataAsync(b))
            .Permit(Trigger.IAC, State.CompletingLINEMODE)
            .PermitReentry(Trigger.ReadNextCharacter);

        stateMachine.Configure(State.CompletingLINEMODE)
            .SubstateOf(State.EndSubNegotiation)
            .OnEntryAsync(async () => await CompleteLineModeAsync(context));
    }
    
    private void ConfigureAsServer(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        // Server sends DO LINEMODE to client
        stateMachine.Configure(State.WillLINEMODE)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client is willing to use line mode"));

        stateMachine.Configure(State.WontLINEMODE)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client won't use line mode"));

        // Server can receive MODE subnegotiations from client
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.LINEMODE, State.AlmostNegotiatingLINEMODE);

        stateMachine.Configure(State.AlmostNegotiatingLINEMODE)
            .Permit(Trigger.LINEMODE_MODE, State.NegotiatingLINEMODE)
            .Permit(Trigger.LINEMODE_FORWARDMASK, State.NegotiatingLINEMODE)
            .Permit(Trigger.LINEMODE_SLC, State.NegotiatingLINEMODE);

        var interpreter = context.Interpreter;
        stateMachine.Configure(State.NegotiatingLINEMODE)
            .OnEntryFrom(interpreter.ParameterizedTrigger(Trigger.LINEMODE_MODE), _ => CaptureSubnegotiationType(SUBNEG_TYPE_MODE))
            .OnEntryFrom(interpreter.ParameterizedTrigger(Trigger.LINEMODE_FORWARDMASK), _ => CaptureSubnegotiationType(SUBNEG_TYPE_FORWARDMASK))
            .OnEntryFrom(interpreter.ParameterizedTrigger(Trigger.LINEMODE_SLC), _ => CaptureSubnegotiationType(SUBNEG_TYPE_SLC))
            .Permit(Trigger.IAC, State.CompletingLINEMODE)
            .PermitDynamic(Trigger.ReadNextCharacter, () => State.EvaluatingLINEMODE);

        stateMachine.Configure(State.EvaluatingLINEMODE)
            .OnEntryFromAsync(interpreter.ParameterizedTrigger(Trigger.ReadNextCharacter), async (b) => await CaptureLineModeDataAsync(b))
            .Permit(Trigger.IAC, State.CompletingLINEMODE)
            .PermitReentry(Trigger.ReadNextCharacter);

        stateMachine.Configure(State.CompletingLINEMODE)
            .SubstateOf(State.EndSubNegotiation)
            .OnEntryAsync(async () => await CompleteLineModeAsync(context));

        context.RegisterInitialNegotiation(async () => await SendDoLineModeAsync(context));
    }

    private int _subnegotiationType = -1;
    private readonly List<byte> _buffer = new();

    private void CaptureSubnegotiationType(int type)
    {
        _subnegotiationType = type;
        _buffer.Clear();
    }

    private ValueTask CaptureLineModeDataAsync(OneOf.OneOf<byte, Trigger> data)
    {
        if (data.IsT0)
        {
            _buffer.Add(data.AsT0);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Line Mode Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Line Mode Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Line Mode Protocol disabled");
        await SetLineModeStateAsync(false);
        _currentMode = 0;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _lineModeEnabled = false;
        _currentMode = 0;
        _onModeChanged = null;
        _buffer.Clear();
        return ValueTask.CompletedTask;
    }

    #region Public API Methods

    /// <summary>
    /// Sends a MODE command to set line mode settings (server mode only)
    /// </summary>
    /// <param name="mode">Mode byte with flags (EDIT, TRAPSIG, MODE_ACK, etc.)</param>
    public async ValueTask SetModeAsync(byte mode)
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        Context.Logger.LogDebug("Sending MODE command with mode byte: {Mode:X2}", mode);
        await Context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, mode,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    /// <summary>
    /// Sends a MODE command to enable EDIT mode (client does local line editing)
    /// </summary>
    public async ValueTask EnableEditModeAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        var newMode = (byte)(_currentMode | MODE_EDIT);
        await SetModeAsync(newMode);
    }

    /// <summary>
    /// Sends a MODE command to disable EDIT mode (server does line editing)
    /// </summary>
    public async ValueTask DisableEditModeAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        var newMode = (byte)(_currentMode & ~MODE_EDIT);
        await SetModeAsync(newMode);
    }

    /// <summary>
    /// Sends a MODE command to enable TRAPSIG mode (client traps signals)
    /// </summary>
    public async ValueTask EnableTrapSigModeAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        var newMode = (byte)(_currentMode | MODE_TRAPSIG);
        await SetModeAsync(newMode);
    }

    /// <summary>
    /// Sends a MODE command to disable TRAPSIG mode (server handles signals)
    /// </summary>
    public async ValueTask DisableTrapSigModeAsync()
    {
        if (!IsEnabled || Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
            return;

        var newMode = (byte)(_currentMode & ~MODE_TRAPSIG);
        await SetModeAsync(newMode);
    }

    #endregion

    #region State Machine Handlers

    private async ValueTask SetLineModeStateAsync(bool enabled)
    {
        if (_lineModeEnabled == enabled)
            return;

        _lineModeEnabled = enabled;
        Context.Logger.LogInformation("Line mode {State}", enabled ? "enabled" : "disabled");
    }

    private async ValueTask WillLineModeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client willing to use line mode - sending WILL");
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE
        });
        await SetLineModeStateAsync(true);
    }

    private async ValueTask OnDontLineModeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server doesn't want line mode - do nothing");
        await SetLineModeStateAsync(false);
    }

    private async ValueTask SendDoLineModeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server requesting line mode support - sending DO");
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE
        });
    }

    private async ValueTask CompleteLineModeAsync(IProtocolContext context)
    {
        if (_subnegotiationType == SUBNEG_TYPE_MODE)
        {
            if (_buffer.Count > 0)
            {
                var mode = _buffer[0];
                context.Logger.LogDebug("Received MODE subnegotiation: {Mode:X2}", mode);
                
                // Check if this is an acknowledgment
                var isAck = (mode & MODE_ACK) != 0;
                
                if (isAck)
                {
                    // This is an acknowledgment of a mode we sent
                    context.Logger.LogDebug("Received MODE acknowledgment");
                    // Don't send another acknowledgment back - that would create a loop
                }
                else if (Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
                {
                    // Client is proposing a mode (without ACK bit) - we should acknowledge it
                    context.Logger.LogDebug("Client proposing mode, sending acknowledgment");
                    var ackMode = (byte)(mode | MODE_ACK);
                    await context.SendNegotiationAsync(new byte[]
                    {
                        (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
                        (byte)Trigger.LINEMODE_MODE, ackMode,
                        (byte)Trigger.IAC, (byte)Trigger.SE
                    });
                }
                // Client mode: If we receive a mode without ACK bit, it means the server
                // is commanding us to use this mode. We should acknowledge it.
                else if (Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client && !isAck)
                {
                    context.Logger.LogDebug("Server commanding mode, sending acknowledgment");
                    var ackMode = (byte)(mode | MODE_ACK);
                    await context.SendNegotiationAsync(new byte[]
                    {
                        (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
                        (byte)Trigger.LINEMODE_MODE, ackMode,
                        (byte)Trigger.IAC, (byte)Trigger.SE
                    });
                }
                
                // Update current mode (remove ACK bit for storage)
                _currentMode = (byte)(mode & ~MODE_ACK);
                
                // Invoke callback if registered
                if (_onModeChanged != null)
                {
                    await _onModeChanged(_currentMode);
                }
            }
        }
        else if (_subnegotiationType == SUBNEG_TYPE_FORWARDMASK)
        {
            context.Logger.LogDebug("Received FORWARDMASK subnegotiation (not implemented)");
        }
        else if (_subnegotiationType == SUBNEG_TYPE_SLC)
        {
            context.Logger.LogDebug("Received SLC subnegotiation (not implemented)");
        }
        
        _buffer.Clear();
        _subnegotiationType = -1;
    }

    #endregion
}
