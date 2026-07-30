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
    private static readonly byte[] s_willMssp = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP };
    private static readonly byte[] s_doMssp = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP };

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

    /// <summary>
    /// The field currently being accumulated -- a variable name or one of its values, depending on
    /// which marker byte opened it.
    /// </summary>
    /// <remarks>
    /// One buffer, not two, and a flag rather than a pair of parallel lists. MSSP delimits fields
    /// with <c>MSSP_VAR</c> and <c>MSSP_VAL</c> markers, and a variable may carry several values;
    /// parallel name/value lists cannot express that, and the previous implementation silently ran
    /// consecutive values together into one buffer.
    /// </remarks>
    private readonly List<byte> _currentField = [];

    private bool _currentFieldIsValue;
    private string? _currentVariable;
    private readonly MSSPVariableCollection _received = new();

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
                .OnEntry(ClearMSSPState);

            stateMachine.Configure(State.AlmostNegotiatingMSSP)
                .Permit(Trigger.MSSP_VAR, State.EvaluatingMSSPVar);

            stateMachine.Configure(State.EvaluatingMSSPVar)
                .Permit(Trigger.MSSP_VAL, State.EvaluatingMSSPVal)
                .Permit(Trigger.IAC, State.EscapingMSSPVar)
                .OnEntryFrom(Trigger.MSSP_VAR, OnMSSPVariableMarker);

            // SE is permitted here as well as after a value: a payload whose last field is a variable
            // name (IAC SB MSSP MSSP_VAR "FOO" IAC SE) is malformed but real, and without this the
            // trigger went unhandled and the MSSP state machine stayed wedged for the connection.
            stateMachine.Configure(State.EscapingMSSPVar)
                .Permit(Trigger.IAC, State.EvaluatingMSSPVar)
                .Permit(Trigger.SE, State.CompletingMSSP);

            stateMachine.Configure(State.EvaluatingMSSPVal)
                .Permit(Trigger.MSSP_VAR, State.EvaluatingMSSPVar)
                .Permit(Trigger.IAC, State.EscapingMSSPVal)
                .OnEntryFrom(Trigger.MSSP_VAL, OnMSSPValueMarker);

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
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("MSSP Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MSSP Protocol disabled");
        ClearMSSPState();
        return default(ValueTask);
    }

    /// <summary>
    /// Adds a variable byte to the current MSSP variable
    /// </summary>
    public void AddMSSPVariableByte(byte value)
    {
        if (!IsEnabled)
            return;

        if (_currentFieldIsValue) FlushField();
        _currentFieldIsValue = false;
        _currentField.Add(value);
    }

    /// <summary>
    /// Adds a value byte to the current MSSP value
    /// </summary>
    public void AddMSSPValueByte(byte value)
    {
        if (!IsEnabled)
            return;

        if (!_currentFieldIsValue) FlushField();
        _currentFieldIsValue = true;
        _currentField.Add(value);
    }

    /// <summary>
    /// Completes the current MSSP variable
    /// </summary>
    public void CompleteMSSPVariable()
    {
        if (!IsEnabled)
            return;

        _currentFieldIsValue = false;
        FlushField();
    }

    /// <summary>
    /// Completes the current MSSP value
    /// </summary>
    public void CompleteMSSPValue()
    {
        if (!IsEnabled)
            return;

        _currentFieldIsValue = true;
        FlushField();
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
            var config = BuildReceivedConfig(Context.Logger);

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
        _currentField.Clear();
        _currentFieldIsValue = false;
        _currentVariable = null;
        _received.Clear();
    }

    /// <summary>
    /// Closes the field currently being accumulated, attributing it to the variable it belongs to.
    /// </summary>
    /// <remarks>
    /// A value is recorded against the variable most recently named, so repeated <c>MSSP_VAL</c>
    /// markers append rather than run together. An empty value is legitimate -- the specification
    /// says "The value can be an empty string" -- while an empty name is not, so only the latter is
    /// dropped.
    /// </remarks>
    private void FlushField()
    {
        if (_currentField.Count == 0 && !_currentFieldIsValue)
        {
            return;
        }

#if NET5_0_OR_GREATER
        var text = System.Text.Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_currentField));
#else
        var text = System.Text.Encoding.ASCII.GetString(_currentField.ToArray());
#endif
        _currentField.Clear();

        if (_currentFieldIsValue)
        {
            // A value with no variable ahead of it is malformed; there is nothing to attach it to.
            if (_currentVariable != null)
            {
                _received.Add(_currentVariable, text);
            }

            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        // Declared before any value arrives, so a variable a peer sends with no MSSP_VAL at all is
        // still reported as present-but-empty rather than vanishing.
        _currentVariable = text;
        _received.Declare(text);
    }

    /// <summary>
    /// Projects everything received into an <see cref="MSSPConfig"/>: the lossless variable map, the
    /// strongly typed properties it can fill, and <see cref="MSSPConfig.Extended"/> for the rest.
    /// </summary>
    private MSSPConfig BuildReceivedConfig(ILogger logger)
    {
        FlushField();

        var config = new MSSPConfig();

        foreach (var entry in _received)
        {
            var variableName = entry.Key;
            var values = entry.Value;

            foreach (var value in values)
            {
                config.Variables.Add(variableName, value);
            }

            if (values.Count == 0)
            {
                config.Variables.Declare(variableName);
                continue;
            }

            if (MSSPConfigAccessor.TrySetValues(config, variableName, values))
            {
                logger.LogDebug("MSSP variable set: {Variable} = {Value}", variableName, values);
                continue;
            }

            // Not a variable this library models -- an unofficial extra, or a name a codebase
            // invented. Kept rather than dropped: describing itself is what MSSP is for.
            config.Extended[variableName] = values;
            logger.LogDebug("MSSP variable kept as extended: {Variable} = {Value}", variableName, values);
        }

        return config;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        ClearMSSPState();
        return default(ValueTask);
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
        await context.SendNegotiationAsync(s_willMssp);
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
        await context.SendNegotiationAsync(s_doMssp);
    }

    private async ValueTask SendMSSPDataAsync(MSSPConfig config, IProtocolContext context)
    {
        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.MSSP
        };

        // A configuration that carries a verbatim variable map -- one received from a peer -- is
        // written from it, so a report round-trips with its arrays and its unknown variables intact.
        // A configuration built by hand has an empty map, and nothing below changes for it.
        var written = new HashSet<string>(StringComparer.Ordinal);

        foreach (var variable in config.Variables)
        {
            written.Add(variable.Key);
            bytes.AddRange(ConvertToMSSP(variable.Key, variable.Value));
        }

        // Serialize MSSP configuration using reflection
        var fields = typeof(MSSPConfig).GetProperties();
        var knownFields = fields.Where(field => Attribute.IsDefined(field, typeof(NameAttribute)));

        foreach (var field in knownFields)
        {
            var value = field.GetValue(config);
            if (value == null) continue;

            var attr = Attribute.GetCustomAttribute(field, typeof(NameAttribute)) as NameAttribute;
            if (attr == null) continue;

            if (!written.Add(MSSPVariables.Canonicalize(attr.Name))) continue;

            bytes.AddRange(ConvertToMSSP(attr.Name, value));
        }

        foreach (var item in config.Extended ?? new Dictionary<string, dynamic>())
        {
            if (item.Value == null) continue;
            if (!written.Add(MSSPVariables.Canonicalize(item.Key))) continue;

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

    /// <summary>
    /// An <c>MSSP_VAR</c> marker: whatever was being accumulated is finished, and a variable name
    /// starts. Fires on re-entry too, so a repeated variable is a new entry rather than a no-op.
    /// </summary>
    private void OnMSSPVariableMarker()
    {
        FlushField();
        _currentFieldIsValue = false;
    }

    /// <summary>
    /// An <c>MSSP_VAL</c> marker: whatever was being accumulated is finished, and a value starts.
    /// Fires on re-entry, which is what keeps consecutive values of one variable separate.
    /// </summary>
    private void OnMSSPValueMarker()
    {
        FlushField();
        _currentFieldIsValue = true;
    }

    private void CaptureMSSPValue(OneOf<byte, Trigger> b)
    {
        _currentField.Add(b.AsT0);
    }

    private void CaptureMSSPVariable(OneOf<byte, Trigger> b)
    {
        _currentField.Add(b.AsT0);
    }

    private async ValueTask ReadMSSPValues(IProtocolContext context)
    {
        var config = BuildReceivedConfig(context.Logger);

        // Call user callback
        if (_onMSSPRequest != null)
        {
            await _onMSSPRequest(config);
        }

        ClearMSSPState();
    }

    #endregion
}
