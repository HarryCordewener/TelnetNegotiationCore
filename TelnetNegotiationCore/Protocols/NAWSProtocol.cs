using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// NAWS (Negotiate About Window Size) protocol plugin - RFC 1073
/// Implements http://www.faqs.org/rfcs/rfc1073.html
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnNAWS"/> to set up
/// the callback that will handle window size changes if you need to be notified of client
/// window size updates.
/// </remarks>
[RequiredMethod("OnNAWS", Description = "Configure the callback to handle window size changes (optional but recommended)")]
public class NAWSProtocol : TelnetProtocolPluginBase
{
    private byte[] _nawsByteState = [];
    private int _nawsIndex = 0;
    private bool _willingToDoNAWS = false;

    private Func<int, int, ValueTask>? _onNAWSNegotiated;

    /// <summary>
    /// Sets the callback that is invoked when NAWS negotiation is complete.
    /// </summary>
    /// <param name="callback">The callback to handle window size changes</param>
    /// <returns>This instance for fluent chaining</returns>
    public NAWSProtocol OnNAWS(Func<int, int, ValueTask>? callback)
    {
        _onNAWSNegotiated = callback;
        return this;
    }



    /// <summary>
    /// Currently known Client Height (defaults to 24)
    /// </summary>
    public int ClientHeight { get; private set; } = 24;

    /// <summary>
    /// Currently known Client Width (defaults to 78)
    /// </summary>
    public int ClientWidth { get; private set; } = 78;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(NAWSProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "NAWS (Negotiate About Window Size)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring NAWS state machine");
        
        // Register NAWS protocol handlers with the context
        context.SetSharedState("NAWS_Protocol", this);
        
        // Configure state machine transitions for NAWS protocol
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.NAWS, State.WillDoNAWS);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.NAWS, State.WontDoNAWS);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.NAWS, State.DontNAWS);

        stateMachine.Configure(State.Do)
            .Permit(Trigger.NAWS, State.DoNAWS);

        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            stateMachine.Configure(State.DontNAWS)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Client won't do NAWS - do nothing"));

            stateMachine.Configure(State.DoNAWS)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await ServerWontNAWSAsync(context));
        }

        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Client)
        {
            stateMachine.Configure(State.DontNAWS)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Server won't do NAWS - do nothing"));

            stateMachine.Configure(State.DoNAWS)
                .SubstateOf(State.Accepting)
                .OnEntry(() => _willingToDoNAWS = true);
        }

        stateMachine.Configure(State.WillDoNAWS)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await RequestNAWSAsync(x, context));

        stateMachine.Configure(State.WontDoNAWS)
            .SubstateOf(State.Accepting)
            .OnEntry(() => _willingToDoNAWS = false);

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.NAWS, State.NegotiatingNAWS);

        stateMachine.Configure(State.NegotiatingNAWS)
            .Permit(Trigger.IAC, State.EscapingNAWSValue)
            .OnEntry(GetNAWS);

        // Configure all triggers except IAC to permit transition to EvaluatingNAWS
        TriggerHelper.ForAllTriggersButIAC(t => stateMachine.Configure(State.NegotiatingNAWS).Permit(t, State.EvaluatingNAWS));

        stateMachine.Configure(State.EvaluatingNAWS)
            .PermitDynamic(Trigger.IAC, () => _nawsIndex < 4 ? State.EscapingNAWSValue : State.CompletingNAWS);

        // Configure parameterized trigger handlers for all triggers
        var interpreter = context.Interpreter;
        TriggerHelper.ForAllTriggers(t => stateMachine.Configure(State.EvaluatingNAWS)
            .OnEntryFrom(interpreter.ParameterizedTrigger(t), CaptureNAWS));

        // Configure reentry for all triggers except IAC
        TriggerHelper.ForAllTriggersButIAC(t => stateMachine.Configure(State.EvaluatingNAWS).PermitReentry(t));

        stateMachine.Configure(State.EscapingNAWSValue)
            .Permit(Trigger.IAC, State.EvaluatingNAWS);

        stateMachine.Configure(State.CompletingNAWS)
            .SubstateOf(State.EndSubNegotiation)
            .OnEntryAsync(async x => await CompleteNAWSAsync(x, context));

        context.RegisterInitialNegotiation(async () => await RequestNAWSAsync(null, context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("NAWS Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("NAWS Protocol enabled - requesting window size");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("NAWS Protocol disabled");
        _nawsByteState = [];
        _nawsIndex = 0;
        return default(ValueTask);
    }

    /// <summary>
    /// Processes a NAWS byte from the client
    /// </summary>
    public void ProcessNAWSByte(byte value)
    {
        if (!IsEnabled)
            return;

        if (_nawsIndex >= _nawsByteState.Length)
        {
            Array.Resize(ref _nawsByteState, _nawsIndex + 1);
        }

        _nawsByteState[_nawsIndex++] = value;
    }

    /// <summary>
    /// Completes NAWS negotiation and updates width/height
    /// </summary>
    public async ValueTask CompleteNAWSNegotiationAsync()
    {
        if (!IsEnabled || _nawsByteState.Length < 4)
            return;

        try
        {
            ClientWidth = (_nawsByteState[0] << 8) + _nawsByteState[1];
            ClientHeight = (_nawsByteState[2] << 8) + _nawsByteState[3];

            Context.Logger.LogInformation("NAWS negotiation complete: Width={Width}, Height={Height}", 
                ClientWidth, ClientHeight);

            // Trigger callback if registered
            if (Context.TryGetSharedState<Func<int, int, ValueTask>>("NAWS_Callback", out var callback) && callback != null)
            {
                await callback(ClientWidth, ClientHeight);
            }
        }
        finally
        {
            _nawsByteState = [];
            _nawsIndex = 0;
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _nawsByteState = [];
        _nawsIndex = 0;
        return default(ValueTask);
    }

    /// <summary>
    /// Called by the interpreter when NAWS negotiation is complete.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnNAWSNegotiatedAsync(int height, int width)
    {
        if (!IsEnabled)
            return;

        ClientWidth = width;
        ClientHeight = height;

        Context.Logger.LogInformation("NAWS negotiation complete: Width={Width}, Height={Height}", width, height);
        
        if (_onNAWSNegotiated != null)
            await _onNAWSNegotiated(height, width).ConfigureAwait(false);
    }

    #region State Machine Handlers

    private async ValueTask ServerWontNAWSAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing refusing to send NAWS, this is a Server!");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NAWS });
    }

    private async ValueTask RequestNAWSAsync(StateMachine<State, Trigger>.Transition? _, IProtocolContext context)
    {
        if (!_willingToDoNAWS)
        {
            context.Logger.LogDebug("Requesting NAWS details from Client");
            await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
            _willingToDoNAWS = true;
        }
    }

    private void GetNAWS(StateMachine<State, Trigger>.Transition _)
    {
        _nawsByteState = new byte[4];
        _nawsIndex = 0;
    }

    private void CaptureNAWS(OneOf.OneOf<byte, Trigger> b)
    {
        if (_nawsIndex > _nawsByteState.Length) return;
        _nawsByteState[_nawsIndex] = b.AsT0;
        _nawsIndex++;
    }

    private async ValueTask CompleteNAWSAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        byte[] width = [_nawsByteState[0], _nawsByteState[1]];
        byte[] height = [_nawsByteState[2], _nawsByteState[3]];

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(width);
            Array.Reverse(height);
        }

        ClientWidth = BitConverter.ToInt16(width);
        ClientHeight = BitConverter.ToInt16(height);

        context.Logger.LogDebug("Negotiated for: {clientWidth} width and {clientHeight} height", ClientWidth, ClientHeight);
        
        // Update interpreter properties for backward compatibility
        var interpreter = context.Interpreter;
        var widthProp = interpreter.GetType().GetProperty("ClientWidth");
        var heightProp = interpreter.GetType().GetProperty("ClientHeight");
        if (widthProp != null && widthProp.CanWrite)
            widthProp.SetValue(interpreter, ClientWidth);
        if (heightProp != null && heightProp.CanWrite)
            heightProp.SetValue(interpreter, ClientHeight);
        
        // Call the user callback if registered
        if (_onNAWSNegotiated != null)
        {
            await _onNAWSNegotiated(ClientHeight, ClientWidth);
        }
    }

    #endregion
}
