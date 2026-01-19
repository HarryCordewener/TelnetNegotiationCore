using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Terminal Type protocol plugin - RFC 1091 and MTTS
/// https://datatracker.ietf.org/doc/html/rfc1091
/// https://tintin.mudhalla.net/protocols/mtts/
/// </summary>
public class TerminalTypeProtocol : TelnetProtocolPluginBase
{
    private ImmutableList<string> _terminalTypes = [];
    private int _currentTerminalType = -1;
    private byte[] _ttypeByteState = [];
    private int _ttypeIndex = 0;
    
    private readonly Dictionary<int, string> _MTTS = new()
    {
        { 1, "ANSI" },
        { 2, "VT100" },
        { 4, "UTF8" },
        { 8, "256 COLORS" },
        { 16, "MOUSE_TRACKING" },
        { 32, "OSC_COLOR_PALETTE" },
        { 64, "SCREEN_READER" },
        { 128, "PROXY" },
        { 256, "TRUECOLOR" },
        { 512, "MNES" },
        { 1024, "MSLP" }
    };

    /// <summary>
    /// A list of terminal types for this connection
    /// </summary>
    public ImmutableList<string> TerminalTypes => _terminalTypes;

    /// <summary>
    /// The current selected Terminal Type
    /// </summary>
    public string CurrentTerminalType => _currentTerminalType == -1
        ? "unknown"
        : _terminalTypes[Math.Min(_currentTerminalType, _terminalTypes.Count - 1)];

    /// <inheritdoc />
    public override Type ProtocolType => typeof(TerminalTypeProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Terminal Type (RFC 1091 + MTTS)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Terminal Type state machine");
        
        // Register TerminalType protocol handlers with the context
        context.SetSharedState("TerminalType_Protocol", this);
        
        // Common state machine configuration
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.TTYPE, State.WillDoTType);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.TTYPE, State.WontDoTType);

        stateMachine.Configure(State.Do)
            .Permit(Trigger.TTYPE, State.DoTType);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.TTYPE, State.DontTType);
        
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            ConfigureAsServer(stateMachine, context);
        }
        else
        {
            ConfigureAsClient(stateMachine, context);
        }
    }
    
    private void ConfigureAsClient(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        _currentTerminalType = -1;
        _terminalTypes = _terminalTypes.AddRange(["TNC", "XTERM", "MTTS 3853"]);

        stateMachine.Configure(State.DoTType)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await WillDoTerminalTypeAsync(context));

        stateMachine.Configure(State.DontTType)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Server telling us not to Terminal Type"));

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.TTYPE, State.AlmostNegotiatingTerminalType);

        stateMachine.Configure(State.AlmostNegotiatingTerminalType)
            .Permit(Trigger.SEND, State.NegotiatingTerminalType);

        stateMachine.Configure(State.NegotiatingTerminalType)
            .Permit(Trigger.IAC, State.CompletingTerminalType)
            .OnEntry(GetTerminalType);

        stateMachine.Configure(State.CompletingTerminalType)
            .OnEntryAsync(async () => await ReportNextAvailableTerminalTypeAsync(context))
            .Permit(Trigger.SE, State.Accepting);
    }
    
    private void ConfigureAsServer(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        stateMachine.Configure(State.WillDoTType)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await RequestTerminalTypeAsync(context));

        stateMachine.Configure(State.WontDoTType)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client won't do Terminal Type"));

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.TTYPE, State.AlmostNegotiatingTerminalType);

        stateMachine.Configure(State.AlmostNegotiatingTerminalType)
            .Permit(Trigger.IS, State.NegotiatingTerminalType);

        stateMachine.Configure(State.NegotiatingTerminalType)
            .Permit(Trigger.IAC, State.EscapingTerminalTypeValue)
            .OnEntry(GetTerminalType);

        TriggerHelper.ForAllTriggersButIAC(t =>
            stateMachine.Configure(State.NegotiatingTerminalType).Permit(t, State.EvaluatingTerminalType));
        TriggerHelper.ForAllTriggers(t =>
            stateMachine.Configure(State.EvaluatingTerminalType).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureTerminalType));
        TriggerHelper.ForAllTriggersButIAC(t => stateMachine.Configure(State.EvaluatingTerminalType).PermitReentry(t));

        stateMachine.Configure(State.EvaluatingTerminalType)
            .Permit(Trigger.IAC, State.EscapingTerminalTypeValue);

        stateMachine.Configure(State.EscapingTerminalTypeValue)
            .Permit(Trigger.IAC, State.EvaluatingTerminalType)
            .Permit(Trigger.SE, State.CompletingTerminalType);

        stateMachine.Configure(State.CompletingTerminalType)
            .OnEntryAsync(async () => await CompleteTerminalTypeAsServerAsync(context))
            .SubstateOf(State.Accepting);

        context.RegisterInitialNegotiation(async () => await SendDoTerminalTypeAsync(context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Terminal Type Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Terminal Type Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Terminal Type Protocol disabled");
        _terminalTypes = [];
        _currentTerminalType = -1;
        _ttypeByteState = [];
        _ttypeIndex = 0;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Processes a terminal type byte
    /// </summary>
    public void ProcessTerminalTypeByte(byte value)
    {
        if (!IsEnabled)
            return;

        if (_ttypeIndex >= _ttypeByteState.Length)
        {
            Array.Resize(ref _ttypeByteState, _ttypeIndex + 1);
        }

        _ttypeByteState[_ttypeIndex++] = value;
    }

    /// <summary>
    /// Requests the next terminal type from the client
    /// </summary>
    public async ValueTask RequestTerminalTypeAsync(IProtocolContext context)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the client, to send the next Terminal Type.");
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _terminalTypes = [];
        _ttypeByteState = [];
        _ttypeIndex = 0;
        return ValueTask.CompletedTask;
    }
    
    #region State Machine Handlers

    private void GetTerminalType(StateMachine<State, Trigger>.Transition _)
    {
        _ttypeByteState = new byte[1024];
        _ttypeIndex = 0;
    }

    private void CaptureTerminalType(OneOf<byte, Trigger> b)
    {
        if (_ttypeIndex >= _ttypeByteState.Length) return;
        _ttypeByteState[_ttypeIndex] = b.AsT0;
        _ttypeIndex++;
    }

    private async ValueTask CompleteTerminalTypeAsServerAsync(IProtocolContext context)
    {
        var TType = Encoding.ASCII.GetString(_ttypeByteState, 0, _ttypeIndex);
        if (_terminalTypes.Contains(TType))
        {
            _currentTerminalType = (_currentTerminalType + 1) % _terminalTypes.Count;
            
            var MTTS = _terminalTypes.FirstOrDefault(x => x.StartsWith("MTTS"));
            if (MTTS != null)
            {
                var mttsVal = int.Parse(MTTS.Remove(0, 5));

                _terminalTypes = _terminalTypes.AddRange(_MTTS.Where(x => (mttsVal & x.Key) != 0).Select(x => x.Value));
                _terminalTypes = _terminalTypes.Remove(MTTS);
            }

            context.Logger.LogDebug("Connection: {ConnectionState}: {@TerminalTypes}",
                "Completing Terminal Type negotiation. List as follows", _terminalTypes);
                
            // Update interpreter properties for backward compatibility
            UpdateInterpreterProperties(context);
        }
        else
        {
            context.Logger.LogTrace("Connection: {ConnectionState}: {TerminalType}",
                "Registering Terminal Type. Requesting the next", TType);
            _terminalTypes = _terminalTypes.Add(TType);
            _currentTerminalType++;
            
            // Update interpreter properties for backward compatibility
            UpdateInterpreterProperties(context);
            
            await RequestTerminalTypeAsync(context);
        }
    }

    private async ValueTask WillDoTerminalTypeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the other party, Willing to do Terminal Type.");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE });
    }

    private async ValueTask SendDoTerminalTypeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the other party, to do Terminal Type.");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE });
    }

    private async ValueTask ReportNextAvailableTerminalTypeAsync(IProtocolContext context)
    {
        _currentTerminalType = (_currentTerminalType + 1) % (_terminalTypes.Count + 1);
        context.Logger.LogDebug("Connection: {ConnectionState}", "Reporting the next Terminal Type to the server.");
        byte[] terminalType =
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS,
            .. Encoding.ASCII.GetBytes(CurrentTerminalType),
            (byte)Trigger.IAC, (byte)Trigger.SE
        ];

        await context.SendNegotiationAsync(terminalType);
        
        // Update interpreter properties for backward compatibility
        UpdateInterpreterProperties(context);
    }
    
    private void UpdateInterpreterProperties(IProtocolContext context)
    {
        var interpreter = context.Interpreter;
        var terminalTypesProp = interpreter.GetType().GetProperty("TerminalTypes");
        var currentTerminalTypeProp = interpreter.GetType().GetProperty("CurrentTerminalType");
        
        if (terminalTypesProp != null && terminalTypesProp.CanWrite)
            terminalTypesProp.SetValue(interpreter, _terminalTypes);
        if (currentTerminalTypeProp != null && currentTerminalTypeProp.CanWrite)
            currentTerminalTypeProp.SetValue(interpreter, CurrentTerminalType);
            
        // Also update the internal field _CurrentTerminalType
        var currentTerminalTypeField = interpreter.GetType().GetField("_CurrentTerminalType", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (currentTerminalTypeField != null)
            currentTerminalTypeField.SetValue(interpreter, _currentTerminalType);
    }

    #endregion
}
