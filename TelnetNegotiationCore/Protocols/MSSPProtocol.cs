using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// MSSP (Mud Server Status Protocol) plugin
/// Provides server information to clients
/// </summary>
public class MSSPProtocol : TelnetProtocolPluginBase
{
    /// <summary>
    /// Event that fires when an MSSP request is received.
    /// Users should subscribe to this event to handle MSSP requests.
    /// </summary>
    public event Func<MSSPConfig, ValueTask>? OnMSSPRequest;

    private Func<MSSPConfig> _msspConfig = () => new MSSPConfig();
    private List<byte> _currentMSSPVariable = [];
    private List<List<byte>> _currentMSSPValueList = [];
    private List<byte> _currentMSSPValue = [];
    private List<List<byte>> _currentMSSPVariableList = [];

    private readonly IImmutableDictionary<string, (MemberInfo Member, NameAttribute Attribute)> _msspAttributeMembers;

    public MSSPProtocol()
    {
        _msspAttributeMembers = typeof(MSSPConfig)
            .GetMembers()
            .Select(x => (Member: x, Attribute: x.GetCustomAttribute<NameAttribute>()))
            .Where(x => x.Attribute != null)
            .Select(x => (x.Member, Attribute: x.Attribute!))
            .ToImmutableDictionary(x => x.Attribute.Name.ToUpper());
    }

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
                var variableName = System.Text.Encoding.ASCII.GetString(_currentMSSPVariableList[i].ToArray()).ToUpper();
                
                if (_msspAttributeMembers.TryGetValue(variableName, out var memberInfo))
                {
                    // Set the value on the config object
                    var values = _currentMSSPValueList[i]
                        .Select(b => System.Text.Encoding.ASCII.GetString(new[] { b }))
                        .ToList();

                    // This is simplified - full implementation would properly set the member values
                    Context.Logger.LogDebug("MSSP variable: {Variable} = {Values}", 
                        variableName, string.Join(", ", values));
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
    /// Internal method that fires the public event.
    /// </summary>
    internal async ValueTask OnMSSPRequestAsync(MSSPConfig config)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Received MSSP request");
        
        if (OnMSSPRequest != null)
            await OnMSSPRequest(config).ConfigureAwait(false);
    }
}
