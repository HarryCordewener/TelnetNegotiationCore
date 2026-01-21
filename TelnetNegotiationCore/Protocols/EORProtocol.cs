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
/// EOR (End of Record) protocol plugin
/// Used for prompting without requiring Go-Ahead
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnPrompt"/> to set up
/// the callback that will handle EOR prompts if you need to be notified when prompts are received.
/// </remarks>
[RequiredMethod("OnPrompt", Description = "Configure the callback to handle prompt events (optional but recommended)")]
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
        
        // Register EOR protocol handlers with the context
        context.SetSharedState("EOR_Protocol", this);
        
        // Configure state machine transitions for EOR protocol
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            stateMachine.Configure(State.Do)
                .Permit(Trigger.TELOPT_EOR, State.DoEOR);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.TELOPT_EOR, State.DontEOR);

            stateMachine.Configure(State.DoEOR)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await OnDoEORAsync(x, context));

            stateMachine.Configure(State.DontEOR)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDontEORAsync(context));

            context.RegisterInitialNegotiation(async () => await WillingEORAsync(context));
        }
        else
        {
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.TELOPT_EOR, State.WillEOR);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.TELOPT_EOR, State.WontEOR);

            stateMachine.Configure(State.WontEOR)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await WontEORAsync(context));

            stateMachine.Configure(State.WillEOR)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async x => await OnWillEORAsync(x, context));
        }

        stateMachine.Configure(State.StartNegotiation)
            .Permit(Trigger.EOR, State.Prompting);

        stateMachine.Configure(State.Prompting)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await OnEORPromptAsync());
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("EOR Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("EOR Protocol enabled");
        _doEOR = true;
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("EOR Protocol disabled");
        _doEOR = false;
        return default(ValueTask);
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
            return default(ValueTask);

        _doEOR = true;
        Context.Logger.LogInformation("EOR enabled for connection");
        return default(ValueTask);
    }

    /// <summary>
    /// Disables EOR for the connection
    /// </summary>
    public ValueTask DisableEORAsync()
    {
        if (!IsEnabled)
            return default(ValueTask);

        _doEOR = false;
        Context.Logger.LogInformation("EOR disabled for connection");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _doEOR = null;
        return default(ValueTask);
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

    #region State Machine Handlers

    private async ValueTask OnEORPromptAsync()
    {
        Context.Logger.LogDebug("Server is prompting EOR");
        await OnPromptAsync();
    }

    private ValueTask OnDontEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client won't do EOR - do nothing");
        _doEOR = false;
        return default(ValueTask);
    }

    private ValueTask WontEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server won't do EOR - do nothing");
        _doEOR = false;
        return default(ValueTask);
    }

    private async ValueTask WillingEORAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to EOR!");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
    }

    private ValueTask OnDoEORAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Client supports End of Record.");
        _doEOR = true;
        return default(ValueTask);
    }

    private async ValueTask OnWillEORAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Server supports End of Record.");
        _doEOR = true;
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
    }

    #endregion
}
