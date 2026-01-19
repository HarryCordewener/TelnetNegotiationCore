using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// GMCP (Generic MUD Communication Protocol) plugin implementation.
/// This demonstrates the plugin architecture pattern.
/// </summary>
public class GMCPProtocol : TelnetProtocolPluginBase
{
    private const int MaxMessageSize = 8192; // 8KB DOS protection
    private readonly List<byte> _gmcpBytes = new();

    private Func<(string Package, string Info), ValueTask>? _onGMCPReceived;

    /// <summary>
    /// Sets the callback that is invoked when a GMCP message is received.
    /// </summary>
    /// <param name="callback">The callback to handle GMCP messages</param>
    /// <returns>This instance for fluent chaining</returns>
    public GMCPProtocol OnGMCPMessage(Func<(string Package, string Info), ValueTask>? callback)
    {
        _onGMCPReceived = callback;
        return this;
    }

    /// <summary>
    /// Gets or sets the GMCP message callback.
    /// Can be set directly or using the fluent OnGMCPMessage method.
    /// </summary>
    public Func<(string Package, string Info), ValueTask>? OnGMCPReceived
    {
        get => _onGMCPReceived;
        set => _onGMCPReceived = value;
    }

    /// <inheritdoc />
    public override Type ProtocolType => typeof(GMCPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "GMCP (Generic MUD Communication Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();
    // Note: In the original code, GMCP had a dependency on MSDP for message forwarding
    // This can be expressed as: new[] { typeof(MSDPProtocol) }

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        // This method would configure the state machine transitions for GMCP
        // For now, this is a placeholder showing the pattern
        context.Logger.LogInformation("Configuring GMCP state machine");
        
        // Example of how this would work (simplified):
        // stateMachine.Configure(State.Accepting)
        //     .Permit(Trigger.GMCP_Start, State.GMCP_Collecting);
        //
        // stateMachine.Configure(State.GMCP_Collecting)
        //     .OnEntry(() => _gmcpBytes.Clear())
        //     .Permit(Trigger.GMCP_Data, State.GMCP_Collecting)
        //     .Permit(Trigger.GMCP_End, State.Accepting);
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("GMCP Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("GMCP Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("GMCP Protocol disabled");
        _gmcpBytes.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called by the interpreter when a GMCP message is received.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnGMCPMessageAsync((string Package, string Info) message)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Received GMCP message: Package={Package}", message.Package);
        
        if (_onGMCPReceived != null)
            await _onGMCPReceived(message).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a GMCP message byte.
    /// </summary>
    public void AddGMCPByte(byte value)
    {
        if (!IsEnabled)
            return;

        // DOS protection: enforce max message size
        if (_gmcpBytes.Count >= MaxMessageSize)
        {
            Context.Logger.LogWarning("GMCP message exceeded max size of {MaxSize} bytes, truncating", MaxMessageSize);
            return;
        }

        _gmcpBytes.Add(value);
    }

    /// <summary>
    /// Processes a complete GMCP message.
    /// </summary>
    public async ValueTask ProcessGMCPMessageAsync()
    {
        if (!IsEnabled || _gmcpBytes.Count == 0)
            return;

        try
        {
            // Process the GMCP message
            var message = System.Text.Encoding.UTF8.GetString(_gmcpBytes.ToArray());
            Context.Logger.LogDebug("Received GMCP message: {Message}", message);

            // Example: Check if this should be forwarded to MSDP
            // This demonstrates plugin-to-plugin communication via context
            if (message.StartsWith("MSDP", StringComparison.OrdinalIgnoreCase))
            {
                // Check if MSDP plugin is available and forward the message
                var msdpPlugin = Context.GetPlugin<MSDPProtocol>();
                if (msdpPlugin != null && msdpPlugin.IsEnabled)
                {
                    // Forward to MSDP - this replaces the hardcoded dependency
                    Context.SetSharedState("GMCP_to_MSDP_Message", _gmcpBytes.ToArray());
                    Context.Logger.LogDebug("Forwarded GMCP message to MSDP plugin");
                }
            }

            // Trigger callback if registered
            if (Context.TryGetSharedState<Func<string, ValueTask>>("GMCP_Callback", out var callback) && callback != null)
            {
                await callback(message);
            }
        }
        finally
        {
            _gmcpBytes.Clear();
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _gmcpBytes.Clear();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// MSDP (MUD Server Data Protocol) plugin implementation.
/// This demonstrates the plugin architecture pattern.
/// </summary>
public class MSDPProtocol : TelnetProtocolPluginBase
{
    private const int MaxMessageSize = 8192; // 8KB DOS protection
    private readonly List<byte> _msdpBytes = new();

    private Func<Interpreters.TelnetInterpreter, string, ValueTask>? _onMSDPReceived;

    /// <summary>
    /// Sets the callback that is invoked when an MSDP message is received.
    /// </summary>
    /// <param name="callback">The callback to handle MSDP messages</param>
    /// <returns>This instance for fluent chaining</returns>
    public MSDPProtocol OnMSDPMessage(Func<Interpreters.TelnetInterpreter, string, ValueTask>? callback)
    {
        _onMSDPReceived = callback;
        return this;
    }

    /// <summary>
    /// Gets or sets the MSDP message callback.
    /// Can be set directly or using the fluent OnMSDPMessage method.
    /// </summary>
    public Func<Interpreters.TelnetInterpreter, string, ValueTask>? OnMSDPReceived
    {
        get => _onMSDPReceived;
        set => _onMSDPReceived = value;
    }

    /// <inheritdoc />
    public override Type ProtocolType => typeof(MSDPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MSDP (MUD Server Data Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring MSDP state machine");
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MSDP Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("MSDP Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MSDP Protocol disabled");
        _msdpBytes.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called by the interpreter when an MSDP message is received.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnMSDPMessageAsync(Interpreters.TelnetInterpreter interpreter, string message)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Received MSDP message");
        
        if (_onMSDPReceived != null)
            await _onMSDPReceived(interpreter, message).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles an MSDP message byte.
    /// </summary>
    public void AddMSDPByte(byte value)
    {
        if (!IsEnabled)
            return;

        // DOS protection: enforce max message size
        if (_msdpBytes.Count >= MaxMessageSize)
        {
            Context.Logger.LogWarning("MSDP message exceeded max size of {MaxSize} bytes, truncating", MaxMessageSize);
            return;
        }

        _msdpBytes.Add(value);
    }

    /// <summary>
    /// Processes a complete MSDP message.
    /// </summary>
    public async ValueTask ProcessMSDPMessageAsync()
    {
        if (!IsEnabled || _msdpBytes.Count == 0)
            return;

        try
        {
            Context.Logger.LogDebug("Received MSDP message with {ByteCount} bytes", _msdpBytes.Count);

            // Trigger callback if registered
            if (Context.TryGetSharedState<Func<byte[], ValueTask>>("MSDP_Callback", out var callback) && callback != null)
            {
                await callback(_msdpBytes.ToArray());
            }
        }
        finally
        {
            _msdpBytes.Clear();
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _msdpBytes.Clear();
        return ValueTask.CompletedTask;
    }
}
