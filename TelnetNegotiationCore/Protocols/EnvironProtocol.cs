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
    private static readonly byte[] s_willEnviron = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON };
    private static readonly byte[] s_doEnviron = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ENVIRON };

    private readonly List<byte> _currentVar = [];
    private readonly List<byte> _currentValue = [];
    private readonly List<string?> _requestedVariables = [];
    private readonly Dictionary<string, string> _environmentVariables = new();
    private IReadOnlyDictionary<string, string> _clientEnvironmentVariables = new Dictionary<string, string>();
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
    /// Defaults to <b>none</b>: nothing is sent that the application did not supply, and in
    /// particular <c>USER</c> is never filled in from the environment of the process.
    /// </summary>
    /// <param name="environmentVariables">The environment variables to send</param>
    /// <returns>This instance for fluent chaining</returns>
    public EnvironProtocol WithClientEnvironmentVariables(IReadOnlyDictionary<string, string>? environmentVariables)
    {
        _clientEnvironmentVariables = environmentVariables ?? new Dictionary<string, string>();
        return this;
    }

    /// <summary>
    /// The variables this client sends when a server asks for them, as set by
    /// <see cref="WithClientEnvironmentVariables"/>. Empty unless the application set some.
    /// </summary>
    public IReadOnlyDictionary<string, string> ClientEnvironmentVariables => _clientEnvironmentVariables;

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
        // Server handles DO/DONT from client (client asking server to do ENVIRON)
        stateMachine.Configure(State.DoENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await OnDoEnvironAsync(x, context));

        stateMachine.Configure(State.DontENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () =>
            {
                context.Logger.LogDebug("Client won't do ENVIRON - do nothing");
                await OnNegotiatedAsync(false);
            });

        // Server also handles WILL/WONT from client (client announcing ability to do ENVIRON)
        stateMachine.Configure(State.WillENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await OnWillEnvironAsync(x, context));

        stateMachine.Configure(State.WontENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () =>
            {
                context.Logger.LogDebug("Client won't do ENVIRON - do nothing");
                await OnNegotiatedAsync(false);
            });

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
            .PermitReentry(Trigger.NEWENVIRON_VAR)
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
        // Client handles WILL/WONT from server (server announcing ability to do ENVIRON)
        stateMachine.Configure(State.WillENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await OnWillEnvironAsync(x, context));

        stateMachine.Configure(State.WontENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () =>
            {
                context.Logger.LogDebug("Server won't do ENVIRON - do nothing");
                await OnNegotiatedAsync(false);
            });

        // Client also handles DO/DONT from server (server asking client to do ENVIRON)
        stateMachine.Configure(State.DoENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await OnDoEnvironAsync(x, context));

        stateMachine.Configure(State.DontENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () =>
            {
                context.Logger.LogDebug("Server telling client not to send ENVIRON");
                await OnNegotiatedAsync(false);
            });

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.ENVIRON, State.AlmostNegotiatingENVIRON);

        stateMachine.Configure(State.AlmostNegotiatingENVIRON)
            .Permit(Trigger.SEND, State.NegotiatingENVIRON)
            .OnEntry(() =>
            {
                _currentVar.Clear();
                _currentValue.Clear();
                _requestedVariables.Clear();
                _collectingVar = false;
                _collectingValue = false;
            });

        stateMachine.Configure(State.NegotiatingENVIRON)
            .Permit(Trigger.NEWENVIRON_VAR, State.EvaluatingENVIRONVar)
            .Permit(Trigger.IAC, State.CompletingENVIRON)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.SEND), CaptureCommandType);

        stateMachine.Configure(State.EvaluatingENVIRONVar)
            .PermitReentry(Trigger.NEWENVIRON_VAR)
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
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("ENVIRON Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("ENVIRON Protocol disabled");
        ClearState();
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        ClearState();
        return default(ValueTask);
    }

    private void ClearState()
    {
        _currentVar.Clear();
        _currentValue.Clear();
        _requestedVariables.Clear();
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
        FlushRequestedVariable();
        _collectingVar = true;
        _collectingValue = false;
        _currentVar.Clear();
    }

    /// <summary>
    /// Records the variable a server has just finished naming in its SEND. A VAR carrying no name at
    /// all requests every variable, and is recorded as such rather than as a variable called "".
    /// </summary>
    private void FlushRequestedVariable()
    {
        if (!_collectingVar)
        {
            return;
        }

        _requestedVariables.Add(_currentVar.Count > 0 ? Encoding.ASCII.GetString(_currentVar.ToArray()) : null);
        _currentVar.Clear();
        _collectingVar = false;
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
#if NET5_0_OR_GREATER
            var varNameSpan = CollectionsMarshal.AsSpan(_currentVar);
            var varName = Encoding.ASCII.GetString(varNameSpan);
            var varValue = _currentValue.Count > 0 
                ? Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_currentValue)) 
                : string.Empty;
#else
            var varName = Encoding.ASCII.GetString(_currentVar.ToArray());
            var varValue = _currentValue.Count > 0 
                ? Encoding.ASCII.GetString(_currentValue.ToArray()) 
                : string.Empty;
#endif

            _environmentVariables[varName] = varValue;

            Context.Logger.LogDebug("ENVIRON variable: {Name} = {Value}", varName, varValue);
        }
    }

    private async ValueTask WillingEnvironAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to ENVIRON!");
        await context.SendNegotiationAsync(s_willEnviron);
    }

    private async ValueTask OnDoEnvironAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Client will do ENVIRON. Requesting environment variables...");
        await OnNegotiatedAsync(true);

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
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_doEnviron);
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

        FlushRequestedVariable();

        // Only what the application supplied reaches the wire. See WithClientEnvironmentVariables.
        foreach (var (name, value) in ResolveRequestedVariables())
        {
            response.Add((byte)Trigger.NEWENVIRON_VAR);
            NewEnvironProtocol.AppendEscaped(response, name);

            // A name with no VALUE at all says this client does not have that variable; a name with
            // an empty VALUE says it has one and it is empty.
            if (value != null)
            {
                response.Add((byte)Trigger.NEWENVIRON_VALUE);
                NewEnvironProtocol.AppendEscaped(response, value);
            }
        }

        _requestedVariables.Clear();

        response.Add((byte)Trigger.IAC);
        response.Add((byte)Trigger.SE);

        await context.SendNegotiationAsync(response.ToArray());
    }

    /// <summary>
    /// Answers the SEND the server actually sent: only the variables it named, in the order it named
    /// them, with a name this client does not have answered by that name carrying no value. A SEND
    /// with no list, or a VAR with no name, asks for everything.
    /// </summary>
    /// <remarks>
    /// Being asked for a variable is not consent to go and find one: a requested name the
    /// application did not configure is undefined, including <c>USER</c>.
    /// </remarks>
    private List<KeyValuePair<string, string?>> ResolveRequestedVariables()
    {
        var response = new List<KeyValuePair<string, string?>>();

        if (_requestedVariables.Count == 0)
        {
            foreach (var (name, value) in _clientEnvironmentVariables)
            {
                response.Add(new KeyValuePair<string, string?>(name, value));
            }

            return response;
        }

        foreach (var requested in _requestedVariables)
        {
            if (requested == null)
            {
                foreach (var (name, value) in _clientEnvironmentVariables)
                {
                    response.Add(new KeyValuePair<string, string?>(name, value));
                }

                continue;
            }

            response.Add(new KeyValuePair<string, string?>(
                requested,
                _clientEnvironmentVariables.TryGetValue(requested, out var configured) ? configured : null));
        }

        return response;
    }

    #endregion
}
