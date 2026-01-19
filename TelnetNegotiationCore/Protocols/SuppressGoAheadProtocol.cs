using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Suppress Go-Ahead protocol plugin
/// Allows half-duplex operation without requiring GA after each transmission
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnPrompt"/> to set up
/// the callback that will handle prompts if you need to be notified when prompts are received.
/// </remarks>
[RequiredMethod("OnPrompt", Description = "Configure the callback to handle prompt events (optional but recommended)")]
public class SuppressGoAheadProtocol : TelnetProtocolPluginBase
{
    private bool? _doGA = true;

    private Func<ValueTask>? _onPromptReceived;

    /// <summary>
    /// Sets the callback that is invoked when a prompt is received (Suppress Go-Ahead marker).
    /// </summary>
    /// <param name="callback">The callback to handle prompts</param>
    /// <returns>This instance for fluent chaining</returns>
    public SuppressGoAheadProtocol OnPrompt(Func<ValueTask>? callback)
    {
        _onPromptReceived = callback;
        return this;
    }



    /// <summary>
    /// Indicates whether Go-Ahead is suppressed (true = suppressed, false = enabled)
    /// </summary>
    public bool IsGoAheadSuppressed => _doGA == false;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(SuppressGoAheadProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Suppress Go-Ahead";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();
    // Note: SuppressGA and EOR often work together as fallbacks
    // This could be expressed as a soft dependency if needed

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Suppress Go-Ahead state machine");
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Suppress Go-Ahead Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Suppress Go-Ahead Protocol enabled");
        _doGA = false; // GA suppressed
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Suppress Go-Ahead Protocol disabled");
        _doGA = true; // GA active
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Enables Go-Ahead suppression for the connection
    /// </summary>
    public ValueTask SuppressGoAheadAsync()
    {
        if (!IsEnabled)
            return ValueTask.CompletedTask;

        _doGA = false;
        Context.Logger.LogInformation("Go-Ahead suppression enabled");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Disables Go-Ahead suppression (re-enables GA)
    /// </summary>
    public ValueTask EnableGoAheadAsync()
    {
        if (!IsEnabled)
            return ValueTask.CompletedTask;

        _doGA = true;
        Context.Logger.LogInformation("Go-Ahead suppression disabled (GA active)");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Checks if prompting should use EOR as fallback
    /// </summary>
    public bool ShouldUseEORFallback()
    {
        if (!IsEnabled || !IsGoAheadSuppressed)
            return false;

        // Check if EOR plugin is available and enabled
        var eorPlugin = Context.GetPlugin<EORProtocol>();
        return eorPlugin != null && eorPlugin.IsEnabled;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _doGA = null;
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

        Context.Logger.LogDebug("Server is prompting with Suppress Go-Ahead");
        
        if (_onPromptReceived != null)
            await _onPromptReceived().ConfigureAwait(false);
    }
}
