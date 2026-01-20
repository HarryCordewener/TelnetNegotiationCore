using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// ENVIRON protocol plugin - RFC 1408
/// http://www.faqs.org/rfcs/rfc1408.html
/// </summary>
/// <remarks>
/// This protocol supports optional configuration. Call <see cref="OnEnvironmentVariables"/> to set up
/// the callback that will handle environment variable requests.
/// NOTE: This is the older RFC 1408 ENVIRON protocol. For the newer RFC 1572 NEW-ENVIRON protocol,
/// use <see cref="NewEnvironProtocol"/> instead.
/// </remarks>
[RequiredMethod("OnEnvironmentVariables", Description = "Configure the callback to handle environment variable updates (optional but recommended)")]
public class EnvironProtocol : TelnetProtocolPluginBase
{
    private readonly List<byte> _currentVar = [];
    private readonly List<byte> _currentValue = [];
    private readonly Dictionary<string, string> _environmentVariables = new();
    private Dictionary<string, string>? _clientEnvironmentVariables = null;
    private bool _collectingVar = false;
    private bool _collectingValue = false;
    private byte _commandType = 0; // IS or SEND

    private Func<Dictionary<string, string>, ValueTask>? _onEnvironmentVariables;

    /// <summary>
    /// Sets the callback that is invoked when environment variables are received.
    /// </summary>
    /// <param name="callback">The callback to handle environment variables</param>
    /// <returns>This instance for fluent chaining</returns>
    public EnvironProtocol OnEnvironmentVariables(Func<Dictionary<string, string>, ValueTask>? callback)
    {
        _onEnvironmentVariables = callback;
        return this;
    }

    /// <summary>
    /// Sets the environment variables to send to the server when requested (client mode only).
    /// If not set, the client will send default environment variables (USER, LANG).
    /// </summary>
    /// <param name="environmentVariables">The environment variables to send</param>
    /// <returns>This instance for fluent chaining</returns>
    public EnvironProtocol WithClientEnvironmentVariables(Dictionary<string, string> environmentVariables)
    {
        _clientEnvironmentVariables = environmentVariables;
        return this;
    }

    /// <summary>
    /// The environment variables received from the remote party
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables => _environmentVariables;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(EnvironProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "ENVIRON (RFC 1408)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring ENVIRON state machine");
        
        // Register ENVIRON protocol handlers with the context
        context.SetSharedState("Environ_Protocol", this);

        // Configure state machine transitions for ENVIRON protocol
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.ENVIRON, State.WillENVIRON);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.ENVIRON, State.WontENVIRON);

        stateMachine.Configure(State.Do)
            .Permit(Trigger.ENVIRON, State.DoENVIRON);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.ENVIRON, State.DontENVIRON);

        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            ConfigureAsServer(stateMachine, context);
        }
        else
        {
            ConfigureAsClient(stateMachine, context);
        }
    }

    private void ConfigureAsServer(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        stateMachine.Configure(State.DoENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await OnDoEnvironAsync(x, context));

        stateMachine.Configure(State.DontENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Client won't do ENVIRON - do nothing"));

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.ENVIRON, State.AlmostNegotiatingENVIRON);

        stateMachine.Configure(State.AlmostNegotiatingENVIRON)
            .Permit(Trigger.IS, State.NegotiatingENVIRON)
            .OnEntry(() =>
            {
                _currentVar.Clear();
                _currentValue.Clear();
                _collectingVar = false;
                _collectingValue = false;
            });

        stateMachine.Configure(State.NegotiatingENVIRON)
            .Permit(Trigger.NEWENVIRON_VAR, State.EvaluatingENVIRONVar)
            .Permit(Trigger.IAC, State.CompletingENVIRON)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.IS), CaptureCommandType);

        stateMachine.Configure(State.EvaluatingENVIRONVar)
            .Permit(Trigger.NEWENVIRON_VAR, State.EvaluatingENVIRONVar)
            .Permit(Trigger.NEWENVIRON_VALUE, State.EvaluatingENVIRONValue)
            .Permit(Trigger.IAC, State.EscapingENVIRONVar)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.NEWENVIRON_VAR), StartNewVar);

        stateMachine.Configure(State.EscapingENVIRONVar)
            .Permit(Trigger.IAC, State.EvaluatingENVIRONVar)
            .Permit(Trigger.SE, State.CompletingENVIRON);

        stateMachine.Configure(State.EvaluatingENVIRONValue)
            .Permit(Trigger.NEWENVIRON_VAR, State.EvaluatingENVIRONVar)
            .Permit(Trigger.IAC, State.EscapingENVIRONValue)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.NEWENVIRON_VALUE), StartNewValue);

        stateMachine.Configure(State.EscapingENVIRONValue)
            .Permit(Trigger.IAC, State.EvaluatingENVIRONValue)
            .Permit(Trigger.SE, State.CompletingENVIRON);

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.NEWENVIRON_VALUE, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingENVIRONVar).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureVarByte));

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.NEWENVIRON_VALUE, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingENVIRONValue).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureValueByte));

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.NEWENVIRON_VALUE, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingENVIRONVar).PermitReentry(t));

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.NEWENVIRON_VALUE, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingENVIRONValue).PermitReentry(t));

        stateMachine.Configure(State.CompletingENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await CompleteEnvironAsync(x, context));

        context.RegisterInitialNegotiation(async () => await WillingEnvironAsync(context));
    }

    private void ConfigureAsClient(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        stateMachine.Configure(State.WillENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await OnWillEnvironAsync(x, context));

        stateMachine.Configure(State.WontENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Server won't do ENVIRON - do nothing"));

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.ENVIRON, State.AlmostNegotiatingENVIRON);

        stateMachine.Configure(State.AlmostNegotiatingENVIRON)
            .Permit(Trigger.SEND, State.NegotiatingENVIRON)
            .OnEntry(() =>
            {
                _currentVar.Clear();
                _currentValue.Clear();
                _collectingVar = false;
                _collectingValue = false;
            });

        stateMachine.Configure(State.NegotiatingENVIRON)
            .Permit(Trigger.NEWENVIRON_VAR, State.EvaluatingENVIRONVar)
            .Permit(Trigger.IAC, State.CompletingENVIRON)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.SEND), CaptureCommandType);

        stateMachine.Configure(State.EvaluatingENVIRONVar)
            .Permit(Trigger.NEWENVIRON_VAR, State.EvaluatingENVIRONVar)
            .Permit(Trigger.IAC, State.CompletingENVIRON)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.NEWENVIRON_VAR), StartRequestedVar);

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingENVIRONVar).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureVarByte));

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingENVIRONVar).PermitReentry(t));

        stateMachine.Configure(State.CompletingENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await SendEnvironmentVariablesAsync(x, context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("ENVIRON Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("ENVIRON Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("ENVIRON Protocol disabled");
        ClearState();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        ClearState();
        return ValueTask.CompletedTask;
    }

    private void ClearState()
    {
        _currentVar.Clear();
        _currentValue.Clear();
        _environmentVariables.Clear();
        _collectingVar = false;
        _collectingValue = false;
        _commandType = 0;
    }

    #region State Machine Handlers

    private void CaptureCommandType(OneOf<byte, Trigger> b)
    {
        _commandType = b.AsT0;
    }

    private void StartNewVar(OneOf<byte, Trigger> _)
    {
        SaveCurrentVariable();
        _collectingVar = true;
        _collectingValue = false;
        _currentVar.Clear();
    }

    private void StartNewValue(OneOf<byte, Trigger> _)
    {
        _collectingVar = false;
        _collectingValue = true;
        _currentValue.Clear();
    }

    private void StartRequestedVar(OneOf<byte, Trigger> _)
    {
        _collectingVar = true;
        _collectingValue = false;
        _currentVar.Clear();
    }

    private void CaptureVarByte(OneOf<byte, Trigger> b)
    {
        if (_collectingVar)
        {
            _currentVar.Add(b.AsT0);
        }
    }

    private void CaptureValueByte(OneOf<byte, Trigger> b)
    {
        if (_collectingValue)
        {
            _currentValue.Add(b.AsT0);
        }
    }

    private void SaveCurrentVariable()
    {
        if (_currentVar.Count > 0)
        {
            var varNameSpan = CollectionsMarshal.AsSpan(_currentVar);
            var varName = Encoding.ASCII.GetString(varNameSpan);
            var varValue = _currentValue.Count > 0 
                ? Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_currentValue)) 
                : string.Empty;

            _environmentVariables[varName] = varValue;

            Context.Logger.LogDebug("ENVIRON variable: {Name} = {Value}", varName, varValue);
        }
    }

    private async ValueTask WillingEnvironAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to ENVIRON!");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });
    }

    private async ValueTask OnDoEnvironAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Client will do ENVIRON. Requesting environment variables...");
        
        // Send ENVIRON SEND (request all variables)
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENVIRON,
            (byte)Trigger.SEND,
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    private async ValueTask OnWillEnvironAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Server will do ENVIRON");
        await context.SendNegotiationAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ENVIRON });
    }

    private async ValueTask CompleteEnvironAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        SaveCurrentVariable();

        context.Logger.LogInformation("Received ENVIRON variables: {Count} environment variables", 
            _environmentVariables.Count);

        if (_onEnvironmentVariables != null)
        {
            await _onEnvironmentVariables(_environmentVariables);
        }
    }

    private async ValueTask SendEnvironmentVariablesAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        // Client received SEND request from server
        context.Logger.LogDebug("Server requested environment variables, sending response...");

        var response = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENVIRON,
            (byte)Trigger.IS
        };

        // Use configured environment variables, or fall back to defaults
        Dictionary<string, string> envVars;
        if (_clientEnvironmentVariables != null && _clientEnvironmentVariables.Count > 0)
        {
            envVars = _clientEnvironmentVariables;
        }
        else
        {
            // Default environment variables
            // Use system locale with UTF-8 encoding (common for modern systems)
            // Users can override via WithClientEnvironmentVariables() if needed
            var locale = System.Globalization.CultureInfo.CurrentCulture.Name.Replace('-', '_');
            if (!locale.Contains('.'))
            {
                // Default to UTF-8 for modern systems; users should configure if different encoding needed
                locale += ".UTF-8";
            }

            envVars = new Dictionary<string, string>
            {
                { "USER", Environment.GetEnvironmentVariable("USER") ?? Environment.UserName ?? "unknown" },
                { "LANG", locale }
            };
        }

        foreach (var (name, value) in envVars)
        {
            if (!string.IsNullOrEmpty(value))
            {
                response.Add((byte)Trigger.NEWENVIRON_VAR);
                response.AddRange(Encoding.ASCII.GetBytes(name));
                response.Add((byte)Trigger.NEWENVIRON_VALUE);
                response.AddRange(Encoding.ASCII.GetBytes(value));
            }
        }

        response.Add((byte)Trigger.IAC);
        response.Add((byte)Trigger.SE);

        await context.SendNegotiationAsync(response.ToArray());
    }

    #endregion
}
