using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// EOR (End of Record) protocol plugin
/// Used for prompting without requiring Go-Ahead
/// </summary>
public class EORProtocol : TelnetProtocolPluginBase
{
    private bool? _doEOR = null;

    private Func<ValueTask>? _onPromptReceived;

    /// <summary>
    /// Sets the callback that is invoked when a prompt is received (EOR marker).
    /// </summary>
    /// <param name="callback">The callback to handle prompts</param>
    /// <returns>This instance for fluent chaining</returns>
    public EORProtocol OnPrompt(Func<ValueTask>? callback)
    {
        _onPromptReceived = callback;
        return this;
    }

    /// <summary>
    /// Gets or sets the prompt received callback.
    /// Can be set directly or using the fluent OnPrompt method.
    /// </summary>
    public Func<ValueTask>? OnPromptReceived
    {
        get => _onPromptReceived;
        set => _onPromptReceived = value;
    }

    /// <summary>
    /// Indicates whether EOR is enabled
    /// </summary>
    public bool IsEOREnabled => _doEOR == true;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(EORProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "EOR (End of Record)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();
    // Note: EOR and SuppressGA often work together as fallbacks
    // This could be expressed as a soft dependency if needed

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring EOR state machine");
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("EOR Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("EOR Protocol enabled");
        _doEOR = true;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("EOR Protocol disabled");
        _doEOR = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Sends an EOR marker to indicate end of prompt/record
    /// </summary>
    public async ValueTask SendEORMarkerAsync()
    {
        if (!IsEnabled || !IsEOREnabled)
            return;

        Context.Logger.LogDebug("Sending EOR marker");

        // Trigger prompting callback if registered
        if (Context.TryGetSharedState<Func<ValueTask>>("Prompting_Callback", out var callback) && callback != null)
        {
            await callback();
        }
    }

    /// <summary>
    /// Enables EOR for the connection
    /// </summary>
    public ValueTask EnableEORAsync()
    {
        if (!IsEnabled)
            return ValueTask.CompletedTask;

        _doEOR = true;
        Context.Logger.LogInformation("EOR enabled for connection");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Disables EOR for the connection
    /// </summary>
    public ValueTask DisableEORAsync()
    {
        if (!IsEnabled)
            return ValueTask.CompletedTask;

        _doEOR = false;
        Context.Logger.LogInformation("EOR disabled for connection");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _doEOR = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called by the interpreter when a prompt is signaled.
    /// Internal method that invokes the callback.
    /// </summary>
    internal async ValueTask OnPromptAsync()
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Server is prompting with EOR");
        
        if (_onPromptReceived != null)
            await _onPromptReceived().ConfigureAwait(false);
    }
}
