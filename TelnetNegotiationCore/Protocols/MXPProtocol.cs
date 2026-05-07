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
/// MXP (MUD eXtension Protocol) plugin implementation.
/// MXP uses telnet option 91 (0x5B) and enables rich content tags in MUD output.
/// </summary>
/// <remarks>
/// This protocol optionally accepts configuration. Call <see cref="OnMXPEnabled"/> to set up
/// the callback that will be invoked when the remote endpoint agrees to MXP.
/// </remarks>
[RequiredMethod("OnMXPEnabled", Description = "Configure the callback to handle MXP activation (optional but recommended)")]
public class MXPProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willMxp = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP];
    private static readonly byte[] s_doMxp = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP];
    private static readonly byte[] s_dontMxp = [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MXP];
    private static readonly byte[] s_wontMxp = [(byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MXP];

    private bool? _mxpEnabled = null;
    private Func<ValueTask>? _onMXPEnabled;

    /// <summary>
    /// Sets the callback that is invoked when MXP is successfully negotiated.
    /// </summary>
    /// <param name="callback">The callback to handle MXP activation</param>
    /// <returns>This instance for fluent chaining</returns>
    public MXPProtocol OnMXPEnabled(Func<ValueTask>? callback)
    {
        _onMXPEnabled = callback;
        return this;
    }

    /// <summary>
    /// Indicates whether MXP has been negotiated and is active.
    /// </summary>
    public bool IsMXPActive => _mxpEnabled == true;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(MXPProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "MXP (MUD eXtension Protocol)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring MXP state machine");

        context.SetSharedState("MXP_Protocol", this);

        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            stateMachine.Configure(State.Do)
                .Permit(Trigger.MXP, State.DoMXP);

            stateMachine.Configure(State.Dont)
                .Permit(Trigger.MXP, State.DontMXP);

            stateMachine.Configure(State.DoMXP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async _ => await OnDoMXPAsync(context));

            stateMachine.Configure(State.DontMXP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await OnDontMXPAsync(context));

            context.RegisterInitialNegotiation(async () => await WillingMXPAsync(context));
        }
        else
        {
            stateMachine.Configure(State.Willing)
                .Permit(Trigger.MXP, State.WillMXP);

            stateMachine.Configure(State.Refusing)
                .Permit(Trigger.MXP, State.WontMXP);

            stateMachine.Configure(State.WontMXP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async () => await WontMXPAsync(context));

            stateMachine.Configure(State.WillMXP)
                .SubstateOf(State.Accepting)
                .OnEntryAsync(async _ => await OnWillMXPAsync(context));
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("MXP Protocol initialized");
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("MXP Protocol enabled");
        _mxpEnabled = true;
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("MXP Protocol disabled");
        _mxpEnabled = false;
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _mxpEnabled = null;
        return default;
    }

    #region State Machine Handlers

    private async ValueTask WillingMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to MXP!");
        await context.SendNegotiationAsync(s_willMxp);
    }

    private async ValueTask OnDoMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client supports MXP.");
        _mxpEnabled = true;

        if (_onMXPEnabled != null)
            await _onMXPEnabled().ConfigureAwait(false);
    }

    private ValueTask OnDontMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client won't do MXP - do nothing");
        _mxpEnabled = false;
        return default;
    }

    private ValueTask WontMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server won't do MXP - do nothing");
        _mxpEnabled = false;
        return default;
    }

    private async ValueTask OnWillMXPAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server supports MXP.");
        _mxpEnabled = true;
        await context.SendNegotiationAsync(s_doMxp);

        if (_onMXPEnabled != null)
            await _onMXPEnabled().ConfigureAwait(false);
    }

    #endregion
}
