using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;
using TelnetNegotiationCore.Generated;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// MSSP (Mud Server Status Protocol) plugin
/// Provides server information to clients
/// </summary>
/// <remarks>
/// This protocol requires configuration before use. Call <see cref="OnMSSP"/> to set up
/// the callback that will handle MSSP requests and provide server information. Without
/// this configuration, the protocol will not be able to respond to client MSSP queries.
/// </remarks>
[RequiredMethod("OnMSSP", Description = "Configure the callback to handle MSSP requests and provide server information")]
public class MSSPProtocol : TelnetProtocolPluginBase
{
    private Func<MSSPConfig, ValueTask>? _onMSSPRequest;

    /// <summary>
    /// Sets the callback that is invoked when an MSSP request is received.
    /// </summary>
    /// <param name="callback">The callback to handle MSSP requests</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSSPProtocol OnMSSP(Func<MSSPConfig, ValueTask>? callback)
    {
        _onMSSPRequest = callback;
        return this;
    }



    private Func<MSSPConfig> _msspConfig = () => new MSSPConfig();
    private List<byte> _currentMSSPVariable = [];
    private List<List<byte>> _currentMSSPValueList = [];
    private List<byte> _currentMSSPValue = [];
    private List<List<byte>> _currentMSSPVariableList = [];

    // No longer needs reflection - uses generated MSSPConfigAccessor instead

    /// <inheritdoc />
    public override Type ProtocolType => typeof(MSSPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MSSP (Mud Server Status Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Sets the MSSP configuration provider
    /// </summary>
    public void SetMSSPConfig(Func<MSSPConfig> config)
    {
        _msspConfig = config ?? (() => new MSSPConfig());
    }

    /// <summary>
    /// Gets the current MSSP configuration
    /// </summary>
    public MSSPConfig GetMSSPConfig() => _msspConfig();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring MSSP state machine");
        
        // Register MSSP protocol handlers with the context
        context.SetSharedState("MSSP_Protocol", this);
        
        // Configure state machine transitions for MSSP protocol
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            stateMachine.Configure(State.Do)
                .Permit(Trigger.MSSP, State.DoMSSP);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.MSSP, State.DontMSSP);

            stateMachine.Configure(State.DoMSSP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await OnDoMSSPAsync(x, context));

            stateMachine.Configure(State.DontMSSP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Client won't do MSSP - do nothing"));

            context.RegisterInitialNegotiation(async () => await WillingMSSPAsync(context));
        }
        else
        {
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.MSSP, State.WillMSSP);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.MSSP, State.WontMSSP);

            stateMachine.Configure(State.WillMSSP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnWillMSSPAsync(context));

            stateMachine.Configure(State.WontMSSP)
                .SubstateOf(State.Accepting)
                .OnEntry(() => context.Logger.LogDebug("Server won't do MSSP - do nothing"));

            stateMachine.Configure(State.SubNegotiation)
                .Permit(Trigger.MSSP, State.AlmostNegotiatingMSSP)
                .OnEntry(() =>
                {
                    _currentMSSPValue = [];
                    _currentMSSPVariable = [];
                    _currentMSSPValueList = [];
                    _currentMSSPVariableList = [];
                });

            stateMachine.Configure(State.AlmostNegotiatingMSSP)
                .Permit(Trigger.MSSP_VAR, State.EvaluatingMSSPVar);

            stateMachine.Configure(State.EvaluatingMSSPVar)
                .Permit(Trigger.MSSP_VAL, State.EvaluatingMSSPVal)
                .Permit(Trigger.IAC, State.EscapingMSSPVar)
                .OnEntryFrom(Trigger.MSSP_VAR, RegisterMSSPVal);

            stateMachine.Configure(State.EscapingMSSPVar)
                .Permit(Trigger.IAC, State.EvaluatingMSSPVar);

            stateMachine.Configure(State.EvaluatingMSSPVal)
                .Permit(Trigger.MSSP_VAR, State.EvaluatingMSSPVar)
                .Permit(Trigger.IAC, State.EscapingMSSPVal)
                .OnEntryFrom(Trigger.MSSP_VAL, RegisterMSSPVar);

            stateMachine.Configure(State.EscapingMSSPVal)
                .Permit(Trigger.IAC, State.EvaluatingMSSPVal)
                .Permit(Trigger.SE, State.CompletingMSSP);

            stateMachine.Configure(State.CompletingMSSP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await ReadMSSPValues(context));

            var interpreter = context.Interpreter;
            TriggerHelper.ForAllTriggersExcept([Trigger.MSSP_VAL, Trigger.MSSP_VAR, Trigger.IAC], 
                t => stateMachine.Configure(State.EvaluatingMSSPVal).OnEntryFrom(interpreter.ParameterizedTrigger(t), CaptureMSSPValue));
            TriggerHelper.ForAllTriggersExcept([Trigger.MSSP_VAL, Trigger.MSSP_VAR, Trigger.IAC], 
                t => stateMachine.Configure(State.EvaluatingMSSPVar).OnEntryFrom(interpreter.ParameterizedTrigger(t), CaptureMSSPVariable));

            TriggerHelper.ForAllTriggersExcept([Trigger.IAC, Trigger.MSSP_VAR],
                t => stateMachine.Configure(State.EvaluatingMSSPVal).PermitReentry(t));
            TriggerHelper.ForAllTriggersExcept([Trigger.IAC, Trigger.MSSP_VAL],
                t => stateMachine.Configure(State.EvaluatingMSSPVar).PermitReentry(t));
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MSSP Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("MSSP Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MSSP Protocol disabled");
        ClearMSSPState();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Adds a variable byte to the current MSSP variable
    /// </summary>
    public void AddMSSPVariableByte(byte value)
    {
        if (!IsEnabled)
            return;

        _currentMSSPVariable.Add(value);
    }

    /// <summary>
    /// Adds a value byte to the current MSSP value
    /// </summary>
    public void AddMSSPValueByte(byte value)
    {
        if (!IsEnabled)
            return;

        _currentMSSPValue.Add(value);
    }

    /// <summary>
    /// Completes the current MSSP variable
    /// </summary>
    public void CompleteMSSPVariable()
    {
        if (!IsEnabled)
            return;

        _currentMSSPVariableList.Add(new List<byte>(_currentMSSPVariable));
        _currentMSSPVariable.Clear();
    }

    /// <summary>
    /// Completes the current MSSP value
    /// </summary>
    public void CompleteMSSPValue()
    {
        if (!IsEnabled)
            return;

        _currentMSSPValueList.Add(new List<byte>(_currentMSSPValue));
        _currentMSSPValue.Clear();
    }

    /// <summary>
    /// Processes the complete MSSP message
    /// </summary>
    public async ValueTask ProcessMSSPMessageAsync()
    {
        if (!IsEnabled)
            return;

        try
        {
            var config = new MSSPConfig();

            // Process all variable-value pairs
            for (int i = 0; i < Math.Min(_currentMSSPVariableList.Count, _currentMSSPValueList.Count); i++)
            {
                // Use CollectionsMarshal.AsSpan for zero-copy string conversion
                var variableBytes = CollectionsMarshal.AsSpan(_currentMSSPVariableList[i]);
                var variableName = System.Text.Encoding.ASCII.GetString(variableBytes).ToUpper();
                
                // Use generated accessor instead of reflection
                if (MSSPConfigAccessor.PropertyMap.ContainsKey(variableName))
                {
                    // Get the value bytes and convert to string using span
                    var valueBytes = CollectionsMarshal.AsSpan(_currentMSSPValueList[i]);
                    var valueString = System.Text.Encoding.ASCII.GetString(valueBytes);
                    
                    // Use the generated accessor to set the property (zero reflection!)
                    if (MSSPConfigAccessor.TrySetProperty(config, variableName, valueString))
                    {
                        Context.Logger.LogDebug("MSSP variable set: {Variable} = {Value}", 
                            variableName, valueString);
                    }
                    else
                    {
                        Context.Logger.LogWarning("Failed to set MSSP variable: {Variable}", variableName);
                    }
                }
            }

            // Trigger callback if registered
            if (Context.TryGetSharedState<Func<MSSPConfig, ValueTask>>("MSSP_Callback", out var callback) && callback != null)
            {
                await callback(config);
            }
        }
        finally
        {
            ClearMSSPState();
        }
    }

    private void ClearMSSPState()
    {
        _currentMSSPVariable.Clear();
        _currentMSSPValueList.Clear();
        _currentMSSPValue.Clear();
        _currentMSSPVariableList.Clear();
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        ClearMSSPState();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called by the interpreter when an MSSP request is received.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnMSSPRequestAsync(MSSPConfig config)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Received MSSP request");
        
        if (_onMSSPRequest != null)
            await _onMSSPRequest(config).ConfigureAwait(false);
    }

    #region State Machine Handlers

    private async ValueTask WillingMSSPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to MSSP!");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP });
    }

    private async ValueTask OnDoMSSPAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Client wants MSSP data. Sending...");
        
        var config = _msspConfig();
        await SendMSSPDataAsync(config, context);
    }

    private async ValueTask OnWillMSSPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server will send MSSP data");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
    }

    private async ValueTask SendMSSPDataAsync(MSSPConfig config, IProtocolContext context)
    {
        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.MSSP
        };

        // Serialize MSSP configuration using reflection
        var fields = typeof(MSSPConfig).GetProperties();
        var knownFields = fields.Where(field => Attribute.IsDefined(field, typeof(NameAttribute)));

        foreach (var field in knownFields)
        {
            var value = field.GetValue(config);
            if (value == null) continue;

            var attr = Attribute.GetCustomAttribute(field, typeof(NameAttribute)) as NameAttribute;
            if (attr == null) continue;

            bytes.AddRange(ConvertToMSSP(attr.Name, value));
        }

        foreach (var item in config.Extended ?? new Dictionary<string, dynamic>())
        {
            if (item.Value == null) continue;
            bytes.AddRange(ConvertToMSSP(item.Key, item.Value));
        }

        bytes.Add((byte)Trigger.IAC);
        bytes.Add((byte)Trigger.SE);

        await context.SendNegotiationAsync(bytes.ToArray());
    }

    private byte[] ConvertToMSSP(string name, dynamic val)
    {
        var bt = new List<byte> { (byte)Trigger.MSSP_VAR };
        bt.AddRange(System.Text.Encoding.ASCII.GetBytes(name));

        switch (val)
        {
            case string s:
                bt.Add((byte)Trigger.MSSP_VAL);
                bt.AddRange(System.Text.Encoding.ASCII.GetBytes(s));
                break;
            case int i:
                bt.Add((byte)Trigger.MSSP_VAL);
                bt.AddRange(System.Text.Encoding.ASCII.GetBytes(i.ToString()));
                break;
            case bool b:
                bt.Add((byte)Trigger.MSSP_VAL);
                bt.AddRange(System.Text.Encoding.ASCII.GetBytes(b ? "1" : "0"));
                break;
            case System.Collections.IEnumerable enumerable:
                foreach (var item in enumerable)
                {
                    bt.Add((byte)Trigger.MSSP_VAL);
                    bt.AddRange(System.Text.Encoding.ASCII.GetBytes(item?.ToString() ?? string.Empty));
                }
                break;
        }

        return bt.ToArray();
    }

    private void RegisterMSSPVal()
    {
        if (_currentMSSPValue.Count == 0) return;
        _currentMSSPValueList.Add(_currentMSSPValue);
        _currentMSSPValue = [];
    }

    private void RegisterMSSPVar()
    {
        if (_currentMSSPVariable.Count == 0) return;
        _currentMSSPVariableList.Add(_currentMSSPVariable);
        _currentMSSPVariable = [];
    }

    private void CaptureMSSPValue(OneOf<byte, Trigger> b)
    {
        _currentMSSPValue.Add(b.AsT0);
    }

    private void CaptureMSSPVariable(OneOf<byte, Trigger> b)
    {
        _currentMSSPVariable.Add(b.AsT0);
    }

    private async ValueTask ReadMSSPValues(IProtocolContext context)
    {
        if (_currentMSSPVariableList.Count == 0 && _currentMSSPVariable.Count > 0)
        {
            _currentMSSPVariableList.Add(_currentMSSPVariable);
        }
        if (_currentMSSPValueList.Count == 0 && _currentMSSPValue.Count > 0)
        {
            _currentMSSPValueList.Add(_currentMSSPValue);
        }

        var config = new MSSPConfig();

        for (int i = 0; i < Math.Min(_currentMSSPVariableList.Count, _currentMSSPValueList.Count); i++)
        {
            var variableBytes = CollectionsMarshal.AsSpan(_currentMSSPVariableList[i]);
            var variableName = System.Text.Encoding.ASCII.GetString(variableBytes).ToUpper();
            
            if (MSSPConfigAccessor.PropertyMap.ContainsKey(variableName))
            {
                var valueBytes = CollectionsMarshal.AsSpan(_currentMSSPValueList[i]);
                var valueString = System.Text.Encoding.ASCII.GetString(valueBytes);
                
                if (MSSPConfigAccessor.TrySetProperty(config, variableName, valueString))
                {
                    context.Logger.LogDebug("MSSP variable set: {Variable} = {Value}", variableName, valueString);
                }
            }
        }

        // Call user callback
        if (_onMSSPRequest != null)
        {
            await _onMSSPRequest(config);
        }

        ClearMSSPState();
    }

    #endregion
}
