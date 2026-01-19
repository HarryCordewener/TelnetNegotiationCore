using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
/// the callback that will handle MSSP requests and provide server information.
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
                
                // Use generated accessor instead of reflection
                if (MSSPConfigAccessor.PropertyMap.ContainsKey(variableName))
                {
                    // Get the value bytes and convert to string
                    var valueString = System.Text.Encoding.ASCII.GetString(_currentMSSPValueList[i].ToArray());
                    
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
}
