using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Terminal Speed protocol plugin - RFC 1079
/// Allows exchange of terminal speed information (transmit and receive speeds in bps)
/// https://datatracker.ietf.org/doc/html/rfc1079
/// </summary>
public class TerminalSpeedProtocol : TelnetProtocolPluginBase
{
    private int _transmitSpeed = 38400;  // Default speeds
    private int _receiveSpeed = 38400;
    private Func<int, int, ValueTask>? _onTerminalSpeed;
    private readonly List<byte> _speedBuffer = new();
    private bool _isCapturingSpeed = false;

    /// <summary>
    /// Gets the current transmit speed in bits per second
    /// </summary>
    public int TransmitSpeed => _transmitSpeed;

    /// <summary>
    /// Gets the current receive speed in bits per second
    /// </summary>
    public int ReceiveSpeed => _receiveSpeed;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(TerminalSpeedProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Terminal Speed (RFC 1079)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Sets the callback that is invoked when terminal speed information is received.
    /// </summary>
    /// <param name="callback">The callback to handle terminal speed (transmitSpeed, receiveSpeed in bps)</param>
    /// <returns>This instance for fluent chaining</returns>
    public TerminalSpeedProtocol OnTerminalSpeed(Func<int, int, ValueTask>? callback)
    {
        _onTerminalSpeed = callback;
        return this;
    }

    /// <summary>
    /// Sets the terminal speeds to send when requested by server (client mode).
    /// </summary>
    /// <param name="transmitSpeed">The transmit speed in bits per second</param>
    /// <param name="receiveSpeed">The receive speed in bits per second</param>
    /// <returns>This instance for fluent chaining</returns>
    public TerminalSpeedProtocol WithClientTerminalSpeed(int transmitSpeed, int receiveSpeed)
    {
        if (transmitSpeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(transmitSpeed), "Transmit speed must be positive");
        if (receiveSpeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(receiveSpeed), "Receive speed must be positive");

        _transmitSpeed = transmitSpeed;
        _receiveSpeed = receiveSpeed;
        return this;
    }

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Terminal Speed state machine");
        
        // Register Terminal Speed protocol handlers with the context
        context.SetSharedState("TerminalSpeed_Protocol", this);
        
        // Common state machine configuration
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.TSPEED, State.WillTSPEED);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.TSPEED, State.WontTSPEED);

        stateMachine.Configure(State.Do)
            .Permit(Trigger.TSPEED, State.DoTSPEED);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.TSPEED, State.DontTSPEED);
        
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
        // Client responds to server's DO TSPEED
        stateMachine.Configure(State.DoTSPEED)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await WillTerminalSpeedAsync(context));

        stateMachine.Configure(State.DontTSPEED)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Server telling us not to send Terminal Speed"));

        // Handle subnegotiation: IAC SB TSPEED SEND IAC SE
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.TSPEED, State.AlmostNegotiatingTSPEED);

        stateMachine.Configure(State.AlmostNegotiatingTSPEED)
            .Permit(Trigger.SEND, State.NegotiatingTSPEED);

        stateMachine.Configure(State.NegotiatingTSPEED)
            .Permit(Trigger.IAC, State.CompletingTSPEED);

        stateMachine.Configure(State.CompletingTSPEED)
            .SubstateOf(State.EndSubNegotiation)
            .OnEntryAsync(async () => await SendTerminalSpeedAsync(context));
    }
    
    private void ConfigureAsServer(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        // Server sends DO TSPEED to client
        stateMachine.Configure(State.WillTSPEED)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await RequestTerminalSpeedAsync(context));

        stateMachine.Configure(State.WontTSPEED)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client won't send Terminal Speed"));

        // Handle subnegotiation: IAC SB TSPEED IS <speed> IAC SE
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.TSPEED, State.AlmostNegotiatingTSPEED);

        stateMachine.Configure(State.AlmostNegotiatingTSPEED)
            .Permit(Trigger.IS, State.NegotiatingTSPEED);

        stateMachine.Configure(State.NegotiatingTSPEED)
            .Permit(Trigger.IAC, State.EscapingTSPEEDValue)
            .OnEntry(() => StartCapturingSpeed());

        // Configure all triggers except IAC to permit transition to EvaluatingTSPEED
        TriggerHelper.ForAllTriggersButIAC(t =>
            stateMachine.Configure(State.NegotiatingTSPEED).Permit(t, State.EvaluatingTSPEED));

        // Configure parameterized trigger handlers for all triggers
        var interpreter = context.Interpreter;
        TriggerHelper.ForAllTriggers(t =>
            stateMachine.Configure(State.EvaluatingTSPEED).OnEntryFrom(interpreter.ParameterizedTrigger(t), CaptureSpeedByte));

        // Configure reentry for all triggers except IAC
        TriggerHelper.ForAllTriggersButIAC(t =>
            stateMachine.Configure(State.EvaluatingTSPEED).PermitReentry(t));

        stateMachine.Configure(State.EvaluatingTSPEED)
            .Permit(Trigger.IAC, State.EscapingTSPEEDValue);

        stateMachine.Configure(State.EscapingTSPEEDValue)
            .Permit(Trigger.IAC, State.EvaluatingTSPEED)
            .Permit(Trigger.SE, State.CompletingTSPEED);

        stateMachine.Configure(State.CompletingTSPEED)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await CompleteTerminalSpeedAsServerAsync(context));

        context.RegisterInitialNegotiation(async () => await SendDoTerminalSpeedAsync(context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Terminal Speed Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Terminal Speed Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Terminal Speed Protocol disabled");
        _speedBuffer.Clear();
        _isCapturingSpeed = false;
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _speedBuffer.Clear();
        _isCapturingSpeed = false;
        _onTerminalSpeed = null;
        return default(ValueTask);
    }

    #region State Machine Handlers

    private void StartCapturingSpeed()
    {
        _speedBuffer.Clear();
        _isCapturingSpeed = true;
    }

    private void CaptureSpeedByte(OneOf<byte, Trigger> b)
    {
        if (!_isCapturingSpeed) return;
        _speedBuffer.Add(b.AsT0);
    }

    private async ValueTask CompleteTerminalSpeedAsServerAsync(IProtocolContext context)
    {
        _isCapturingSpeed = false;
        
        if (_speedBuffer.Count == 0)
        {
            context.Logger.LogWarning("No speed data received");
            return;
        }

#if NET5_0_OR_GREATER
        var speedString = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_speedBuffer));
#else
        var speedString = Encoding.ASCII.GetString(_speedBuffer.ToArray());
#endif
        context.Logger.LogDebug("Connection: {ConnectionState}: {SpeedString}",
            "Received Terminal Speed", speedString);

        // Parse the speed string (format: "transmit,receive")
        var parts = speedString.Split(',');
        if (parts.Length == 2 && 
            int.TryParse(parts[0], out var transmit) && 
            int.TryParse(parts[1], out var receive))
        {
            _transmitSpeed = transmit;
            _receiveSpeed = receive;
            
            context.Logger.LogInformation("Terminal Speed set to {Transmit} bps transmit, {Receive} bps receive",
                transmit, receive);

            if (_onTerminalSpeed != null)
                await _onTerminalSpeed(transmit, receive);
        }
        else
        {
            context.Logger.LogWarning("Invalid terminal speed format: {SpeedString}. Expected format: transmit,receive",
                speedString);
        }

        _speedBuffer.Clear();
    }

    private async ValueTask WillTerminalSpeedAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the server, Willing to send Terminal Speed.");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });
    }

    private async ValueTask SendDoTerminalSpeedAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the client to send Terminal Speed.");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TSPEED });
    }

    private async ValueTask RequestTerminalSpeedAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Requesting Terminal Speed from client.");
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.SEND, (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    private async ValueTask SendTerminalSpeedAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Sending Terminal Speed to server.");
        
        // Format: "transmit,receive" (e.g., "38400,38400")
        var speedString = $"{_transmitSpeed},{_receiveSpeed}";
        
        byte[] terminalSpeed =
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS,
            .. Encoding.ASCII.GetBytes(speedString),
            (byte)Trigger.IAC, (byte)Trigger.SE
        ];

        await context.SendNegotiationAsync(terminalSpeed);
    }

    #endregion
}
