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
/// X-Display Location protocol plugin - RFC 1096
/// Allows exchange of X Window System display location information
/// https://datatracker.ietf.org/doc/html/rfc1096
/// </summary>
public class XDisplayProtocol : TelnetProtocolPluginBase
{
    private string _displayLocation = string.Empty;
    private Func<string, ValueTask>? _onDisplayLocation;
    private readonly List<byte> _displayBuffer = new();
    private bool _isCapturingDisplay = false;

    /// <summary>
    /// Gets the current X display location
    /// </summary>
    public string DisplayLocation => _displayLocation;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(XDisplayProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "X-Display Location (RFC 1096)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Sets the callback that is invoked when X display location information is received.
    /// </summary>
    /// <param name="callback">The callback to handle X display location. Pass null to clear any existing callback.</param>
    /// <returns>This instance for fluent chaining</returns>
    public XDisplayProtocol OnDisplayLocation(Func<string, ValueTask>? callback)
    {
        _onDisplayLocation = callback;
        return this;
    }

    /// <summary>
    /// Sets the X display location to send when requested by server (client mode).
    /// </summary>
    /// <param name="displayLocation">The X display location (e.g., "localhost:0.0", "host.example.com:0")</param>
    /// <returns>This instance for fluent chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when displayLocation is null</exception>
    /// <exception cref="ArgumentException">Thrown when displayLocation is empty</exception>
    public XDisplayProtocol WithClientDisplayLocation(string displayLocation)
    {
        if (displayLocation == null)
            throw new ArgumentNullException(nameof(displayLocation), "Display location cannot be null");
        if (string.IsNullOrEmpty(displayLocation))
            throw new ArgumentException("Display location cannot be empty", nameof(displayLocation));

        _displayLocation = displayLocation;
        return this;
    }

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring X-Display Location state machine");
        
        // Register X-Display protocol handlers with the context
        context.SetSharedState("XDisplay_Protocol", this);
        
        // Common state machine configuration
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.XDISPLOC, State.WillXDISPLOC);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.XDISPLOC, State.WontXDISPLOC);

        stateMachine.Configure(State.Do)
            .Permit(Trigger.XDISPLOC, State.DoXDISPLOC);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.XDISPLOC, State.DontXDISPLOC);
        
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
        // Client responds to server's DO XDISPLOC
        stateMachine.Configure(State.DoXDISPLOC)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await WillXDisplayAsync(context));

        stateMachine.Configure(State.DontXDISPLOC)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Server telling us not to send X Display Location"));

        // Handle subnegotiation: IAC SB XDISPLOC SEND IAC SE
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.XDISPLOC, State.AlmostNegotiatingXDISPLOC);

        stateMachine.Configure(State.AlmostNegotiatingXDISPLOC)
            .Permit(Trigger.SEND, State.NegotiatingXDISPLOC);

        stateMachine.Configure(State.NegotiatingXDISPLOC)
            .Permit(Trigger.IAC, State.CompletingXDISPLOC);

        stateMachine.Configure(State.CompletingXDISPLOC)
            .SubstateOf(State.EndSubNegotiation)
            .OnEntryAsync(async () => await SendXDisplayLocationAsync(context));
    }
    
    private void ConfigureAsServer(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        // Server sends DO XDISPLOC to client
        stateMachine.Configure(State.WillXDISPLOC)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await RequestXDisplayLocationAsync(context));

        stateMachine.Configure(State.WontXDISPLOC)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client won't send X Display Location"));

        // Handle subnegotiation: IAC SB XDISPLOC IS <display> IAC SE
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.XDISPLOC, State.AlmostNegotiatingXDISPLOC);

        stateMachine.Configure(State.AlmostNegotiatingXDISPLOC)
            .Permit(Trigger.IS, State.NegotiatingXDISPLOC);

        stateMachine.Configure(State.NegotiatingXDISPLOC)
            .Permit(Trigger.IAC, State.EscapingXDISPLOCValue)
            .OnEntry(() => StartCapturingDisplay());

        // Configure all triggers except IAC to permit transition to EvaluatingXDISPLOC
        TriggerHelper.ForAllTriggersButIAC(t =>
            stateMachine.Configure(State.NegotiatingXDISPLOC).Permit(t, State.EvaluatingXDISPLOC));

        // Configure parameterized trigger handlers for all triggers
        var interpreter = context.Interpreter;
        TriggerHelper.ForAllTriggers(t =>
            stateMachine.Configure(State.EvaluatingXDISPLOC).OnEntryFrom(interpreter.ParameterizedTrigger(t), CaptureDisplayByte));

        // Configure reentry for all triggers except IAC
        TriggerHelper.ForAllTriggersButIAC(t =>
            stateMachine.Configure(State.EvaluatingXDISPLOC).PermitReentry(t));

        stateMachine.Configure(State.EvaluatingXDISPLOC)
            .Permit(Trigger.IAC, State.EscapingXDISPLOCValue);

        stateMachine.Configure(State.EscapingXDISPLOCValue)
            .Permit(Trigger.IAC, State.EvaluatingXDISPLOC)
            .Permit(Trigger.SE, State.CompletingXDISPLOC);

        stateMachine.Configure(State.CompletingXDISPLOC)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await CompleteXDisplayLocationAsServerAsync(context));

        context.RegisterInitialNegotiation(async () => await SendDoXDisplayLocationAsync(context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("X-Display Location Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("X-Display Location Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("X-Display Location Protocol disabled");
        _displayBuffer.Clear();
        _isCapturingDisplay = false;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _displayBuffer.Clear();
        _isCapturingDisplay = false;
        _onDisplayLocation = null;
        return ValueTask.CompletedTask;
    }

    #region State Machine Handlers

    private void StartCapturingDisplay()
    {
        _displayBuffer.Clear();
        _isCapturingDisplay = true;
    }

    private void CaptureDisplayByte(OneOf<byte, Trigger> b)
    {
        if (!_isCapturingDisplay) return;
        _displayBuffer.Add(b.AsT0);
    }

    private async ValueTask CompleteXDisplayLocationAsServerAsync(IProtocolContext context)
    {
        _isCapturingDisplay = false;
        
        if (_displayBuffer.Count == 0)
        {
            context.Logger.LogWarning("No X display location data received");
            return;
        }

        var displayString = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_displayBuffer));
        context.Logger.LogDebug("Connection: {ConnectionState}: {DisplayLocation}",
            "Received X Display Location", displayString);

        _displayLocation = displayString;
        
        context.Logger.LogInformation("X Display Location set to {DisplayLocation}", displayString);

        if (_onDisplayLocation != null)
            await _onDisplayLocation(displayString);

        _displayBuffer.Clear();
    }

    private async ValueTask WillXDisplayAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the server, Willing to send X Display Location.");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
    }

    private async ValueTask SendDoXDisplayLocationAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the client to send X Display Location.");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.XDISPLOC });
    }

    private async ValueTask RequestXDisplayLocationAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Requesting X Display Location from client.");
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.SEND, (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    private async ValueTask SendXDisplayLocationAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Sending X Display Location to server.");
        
        // Use configured display location or empty string if not configured
        var displayString = _displayLocation;
        
        byte[] xDisplayLocation =
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.IS,
            .. Encoding.ASCII.GetBytes(displayString),
            (byte)Trigger.IAC, (byte)Trigger.SE
        ];

        await context.SendNegotiationAsync(xDisplayLocation);
    }

    #endregion
}
