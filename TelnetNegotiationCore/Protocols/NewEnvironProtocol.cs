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
/// NEW-ENVIRON protocol plugin - RFC 1572 with MNES support
/// http://www.faqs.org/rfcs/rfc1572.html
/// https://tintin.mudhalla.net/protocols/mnes/
/// </summary>
/// <remarks>
/// This protocol supports optional configuration. Call <see cref="OnEnvironmentVariables"/> to set up
/// the callback that will handle environment variable requests.
/// MNES (Mud New Environment Standard) is an extension indicated by MTTS flag 512.
/// </remarks>
[RequiredMethod("OnEnvironmentVariables", Description = "Configure the callback to handle environment variable updates (optional but recommended)")]
public class NewEnvironProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willNewEnviron = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON };
    private static readonly byte[] s_doNewEnviron = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NEWENVIRON };

    private readonly List<byte> _currentVar = [];
    private readonly List<byte> _currentValue = [];
    private readonly Dictionary<string, string> _environmentVariables = new();
    private readonly Dictionary<string, string> _userVariables = new();

    /// <summary>What this client reports about itself when asked. Empty until an application says.</summary>
    private readonly Dictionary<string, string> _reportedVariables = new(StringComparer.Ordinal);

    /// <summary>The variable names the server's SEND asked for, in the order it asked.</summary>
    private readonly List<string> _requestedVariables = [];
    private bool _isUserVar = false;
    private bool _collectingVar = false;
    private bool _collectingValue = false;
    private byte _commandType = 0; // IS, INFO, or SEND

    private Func<Dictionary<string, string>, Dictionary<string, string>, ValueTask>? _onEnvironmentVariables;

    /// <summary>
    /// Sets the callback that is invoked when environment variables are received.
    /// </summary>
    /// <param name="callback">The callback to handle environment variables (regular, user)</param>
    /// <returns>This instance for fluent chaining</returns>
    public NewEnvironProtocol OnEnvironmentVariables(Func<Dictionary<string, string>, Dictionary<string, string>, ValueTask>? callback)
    {
        _onEnvironmentVariables = callback;
        return this;
    }

    /// <summary>
    /// Sets what this client reports about itself when a server asks (MNES).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is reported until an application calls this.</b> The library has no business
    /// inventing an answer on the application's behalf: it does not know the client's name, its
    /// version, or whether the person running it would want any of it sent to a stranger's server.
    /// A client that configures nothing answers a SEND with an empty IS, which is exactly what RFC
    /// 1572 provides for and leaves the server no worse off than talking to the many clients that
    /// never negotiate NEW-ENVIRON at all.
    /// </para>
    /// <para>
    /// Use <see cref="MnesVariables"/> for the standard names. Names and values are validated here
    /// rather than at send time, so a value that would corrupt the subnegotiation is refused by the
    /// caller's own stack trace instead of producing a stream the far end cannot parse.
    /// </para>
    /// </remarks>
    /// <param name="variables">The variables to report. Replaces anything set before.</param>
    /// <returns>This instance for fluent chaining.</returns>
    /// <exception cref="ArgumentException">A name or value is not legal MNES.</exception>
    public NewEnvironProtocol ReportVariables(IReadOnlyDictionary<string, string> variables)
    {
        if (variables is null)
        {
            // Written out rather than ArgumentNullException.ThrowIfNull, which netstandard2.0 does
            // not have and this package still targets.
            throw new ArgumentNullException(nameof(variables));
        }

        foreach (var (name, value) in variables)
        {
            Validate(name, value);
        }

        _reportedVariables.Clear();

        foreach (var (name, value) in variables)
        {
            _reportedVariables[name] = value;
        }

        return this;
    }

    /// <summary>Sets or replaces one reported variable.</summary>
    /// <exception cref="ArgumentException">The name or value is not legal MNES.</exception>
    public NewEnvironProtocol ReportVariable(string name, string value)
    {
        Validate(name, value);
        _reportedVariables[name] = value;

        return this;
    }

    /// <summary>What this client reports about itself.</summary>
    public IReadOnlyDictionary<string, string> ReportedVariables => _reportedVariables;

    /// <summary>
    /// The environment variables received from the remote party
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables => _environmentVariables;

    /// <summary>
    /// The user-defined variables received from the remote party
    /// </summary>
    public IReadOnlyDictionary<string, string> UserVariables => _userVariables;

    /// <inheritdoc />
    public override Type ProtocolType => typeof(NewEnvironProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "NEW-ENVIRON (RFC 1572 + MNES)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring NEW-ENVIRON state machine");
        
        // Register NEW-ENVIRON protocol handlers with the context
        context.SetSharedState("NewEnviron_Protocol", this);

        // Configure state machine transitions for NEW-ENVIRON protocol
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.NEWENVIRON, State.WillNEWENVIRON);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.NEWENVIRON, State.WontNEWENVIRON);

        stateMachine.Configure(State.Do)
            .Permit(Trigger.NEWENVIRON, State.DoNEWENVIRON);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.NEWENVIRON, State.DontNEWENVIRON);

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
        // Server handles DO/DONT from client (client asking server to do NEW-ENVIRON)
        stateMachine.Configure(State.DoNEWENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await OnDoNewEnvironAsync(x, context));

        stateMachine.Configure(State.DontNEWENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Client won't do NEW-ENVIRON - do nothing"));

        // Server also handles WILL/WONT from client (client announcing ability to do NEW-ENVIRON)
        stateMachine.Configure(State.WillNEWENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await ServerOnWillNewEnvironAsync(x, context));

        stateMachine.Configure(State.WontNEWENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Client won't do NEW-ENVIRON - do nothing"));

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.NEWENVIRON, State.AlmostNegotiatingNEWENVIRON);

        stateMachine.Configure(State.AlmostNegotiatingNEWENVIRON)
            .Permit(Trigger.IS, State.NegotiatingNEWENVIRON)
            .Permit(Trigger.NEWENVIRON_INFO, State.NegotiatingNEWENVIRON)
            .OnEntry(() =>
            {
                _currentVar.Clear();
                _currentValue.Clear();
                _requestedVariables.Clear();
                _collectingVar = false;
                _collectingValue = false;
                _isUserVar = false;
            });

        stateMachine.Configure(State.NegotiatingNEWENVIRON)
            .Permit(Trigger.NEWENVIRON_VAR, State.EvaluatingNEWENVIRONVar)
            .Permit(Trigger.NEWENVIRON_USERVAR, State.EvaluatingNEWENVIRONVar)
            .Permit(Trigger.IAC, State.CompletingNEWENVIRON)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.IS), CaptureCommandType)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.NEWENVIRON_INFO), CaptureCommandType);

        stateMachine.Configure(State.EvaluatingNEWENVIRONVar)
            .PermitReentry(Trigger.NEWENVIRON_VAR)
            .PermitReentry(Trigger.NEWENVIRON_USERVAR)
            .Permit(Trigger.NEWENVIRON_VALUE, State.EvaluatingNEWENVIRONValue)
            .Permit(Trigger.IAC, State.EscapingNEWENVIRONVar)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.NEWENVIRON_VAR), StartNewVar)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.NEWENVIRON_USERVAR), StartNewUserVar);

        stateMachine.Configure(State.EscapingNEWENVIRONVar)
            .Permit(Trigger.IAC, State.EvaluatingNEWENVIRONVar)
            .Permit(Trigger.SE, State.CompletingNEWENVIRON);

        stateMachine.Configure(State.EvaluatingNEWENVIRONValue)
            .Permit(Trigger.NEWENVIRON_VAR, State.EvaluatingNEWENVIRONVar)
            .Permit(Trigger.NEWENVIRON_USERVAR, State.EvaluatingNEWENVIRONVar)
            .Permit(Trigger.IAC, State.EscapingNEWENVIRONValue)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.NEWENVIRON_VALUE), StartNewValue);

        stateMachine.Configure(State.EscapingNEWENVIRONValue)
            .Permit(Trigger.IAC, State.EvaluatingNEWENVIRONValue)
            .Permit(Trigger.SE, State.CompletingNEWENVIRON);

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.NEWENVIRON_USERVAR, Trigger.NEWENVIRON_VALUE, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingNEWENVIRONVar).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureVarByte));

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.NEWENVIRON_USERVAR, Trigger.NEWENVIRON_VALUE, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingNEWENVIRONValue).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureValueByte));

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.NEWENVIRON_USERVAR, Trigger.NEWENVIRON_VALUE, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingNEWENVIRONVar).PermitReentry(t));

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.NEWENVIRON_USERVAR, Trigger.NEWENVIRON_VALUE, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingNEWENVIRONValue).PermitReentry(t));

        stateMachine.Configure(State.CompletingNEWENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await CompleteNewEnvironAsync(x, context));

        context.RegisterInitialNegotiation(async () => await WillingNewEnvironAsync(context));
    }

    private void ConfigureAsClient(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        // Client handles WILL/WONT from server (server announcing ability to do NEW-ENVIRON)
        stateMachine.Configure(State.WillNEWENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await ClientOnWillNewEnvironAsync(x, context));

        stateMachine.Configure(State.WontNEWENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Server won't do NEW-ENVIRON - do nothing"));

        // Client also handles DO/DONT from server (server asking client to do NEW-ENVIRON)
        stateMachine.Configure(State.DoNEWENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await OnDoNewEnvironAsync(x, context));

        stateMachine.Configure(State.DontNEWENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Server telling client not to send NEW-ENVIRON"));

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.NEWENVIRON, State.AlmostNegotiatingNEWENVIRON);

        stateMachine.Configure(State.AlmostNegotiatingNEWENVIRON)
            .Permit(Trigger.SEND, State.NegotiatingNEWENVIRON)
            .OnEntry(() =>
            {
                _currentVar.Clear();
                _currentValue.Clear();
                _requestedVariables.Clear();
                _collectingVar = false;
                _collectingValue = false;
                _isUserVar = false;
            });

        stateMachine.Configure(State.NegotiatingNEWENVIRON)
            .Permit(Trigger.NEWENVIRON_VAR, State.EvaluatingNEWENVIRONVar)
            .Permit(Trigger.NEWENVIRON_USERVAR, State.EvaluatingNEWENVIRONVar)
            .Permit(Trigger.IAC, State.CompletingNEWENVIRON)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.SEND), CaptureCommandType);

        stateMachine.Configure(State.EvaluatingNEWENVIRONVar)
            .PermitReentry(Trigger.NEWENVIRON_VAR)
            .PermitReentry(Trigger.NEWENVIRON_USERVAR)
            .Permit(Trigger.IAC, State.CompletingNEWENVIRON)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.NEWENVIRON_VAR), StartRequestedVar)
            .OnEntryFrom(context.Interpreter.ParameterizedTrigger(Trigger.NEWENVIRON_USERVAR), StartRequestedUserVar);

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.NEWENVIRON_USERVAR, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingNEWENVIRONVar).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureVarByte));

        TriggerHelper.ForAllTriggersExcept([Trigger.NEWENVIRON_VAR, Trigger.NEWENVIRON_USERVAR, Trigger.IAC],
            t => stateMachine.Configure(State.EvaluatingNEWENVIRONVar).PermitReentry(t));

        stateMachine.Configure(State.CompletingNEWENVIRON)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async x => await SendEnvironmentVariablesAsync(x, context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("NEW-ENVIRON Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("NEW-ENVIRON Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("NEW-ENVIRON Protocol disabled");
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
        _environmentVariables.Clear();
        _userVariables.Clear();
        _collectingVar = false;
        _collectingValue = false;
        _isUserVar = false;
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
        _isUserVar = false;
        _currentVar.Clear();
    }

    private void StartNewUserVar(OneOf<byte, Trigger> _)
    {
        SaveCurrentVariable();
        _collectingVar = true;
        _collectingValue = false;
        _isUserVar = true;
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
        // Each VAR closes the previous name. Without this the state machine's PermitReentry meant a
        // SEND naming three variables left only the third in the buffer, and every earlier one was
        // silently discarded.
        FinishRequestedVar();
        _collectingVar = true;
        _collectingValue = false;
        _isUserVar = false;
        _currentVar.Clear();
    }

    private void StartRequestedUserVar(OneOf<byte, Trigger> _)
    {
        FinishRequestedVar();
        _collectingVar = true;
        _collectingValue = false;
        _isUserVar = true;
        _currentVar.Clear();
    }

    /// <summary>Closes off the name being collected, if any, and records it as requested.</summary>
    private void FinishRequestedVar()
    {
        if (_currentVar.Count == 0)
        {
            return;
        }

        _requestedVariables.Add(Encoding.ASCII.GetString(_currentVar.ToArray()));
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

            if (_isUserVar)
            {
                _userVariables[varName] = varValue;
            }
            else
            {
                _environmentVariables[varName] = varValue;
            }

            Context.Logger.LogDebug("NEW-ENVIRON {Type} variable: {Name} = {Value}", 
                _isUserVar ? "USER" : "ENV", varName, varValue);
        }
    }

    private async ValueTask WillingNewEnvironAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to NEW-ENVIRON!");
        await context.SendNegotiationAsync(s_willNewEnviron);
    }

    private async ValueTask OnDoNewEnvironAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Client will do NEW-ENVIRON. Requesting environment variables...");
        
        // Send NEWENVIRON SEND (request all variables)
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.NEWENVIRON,
            (byte)Trigger.SEND,
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    private async ValueTask ServerOnWillNewEnvironAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Client will do NEW-ENVIRON - accepting and requesting variables");
        
        // Send DO to accept the capability
        await context.SendNegotiationAsync(s_doNewEnviron);
        
        // Immediately send SEND to request all variables
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.NEWENVIRON,
            (byte)Trigger.SEND,
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    private async ValueTask ClientOnWillNewEnvironAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        context.Logger.LogDebug("Server will do NEW-ENVIRON");
        await context.SendNegotiationAsync(s_doNewEnviron);
    }

    private async ValueTask CompleteNewEnvironAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        SaveCurrentVariable();

        context.Logger.LogInformation("Received NEW-ENVIRON variables: {Count} environment, {UserCount} user", 
            _environmentVariables.Count, _userVariables.Count);

        if (_onEnvironmentVariables != null)
        {
            await _onEnvironmentVariables(_environmentVariables, _userVariables);
        }
    }

    private async ValueTask SendEnvironmentVariablesAsync(
        StateMachine<State, Trigger>.Transition _,
        IProtocolContext context)
    {
        FinishRequestedVar();

        // RFC 1572 and MNES both: a SEND naming no variables asks for everything we have. A SEND
        // naming some asks for those, and the answer is allowed to omit any we do not hold.
        var wanted = _requestedVariables.Count == 0
            ? _reportedVariables.Keys.ToList()
            : _requestedVariables.Where(_reportedVariables.ContainsKey).ToList();

        context.Logger.LogDebug(
            "Server requested {Requested} environment variable(s); reporting {Reported}",
            _requestedVariables.Count, wanted.Count);

        var response = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.NEWENVIRON,
            (byte)Trigger.IS
        };

        foreach (var name in wanted)
        {
            response.Add((byte)Trigger.NEWENVIRON_VAR);
            response.AddRange(Encoding.ASCII.GetBytes(name));
            response.Add((byte)Trigger.NEWENVIRON_VALUE);
            response.AddRange(Encoding.ASCII.GetBytes(_reportedVariables[name]));
        }

        response.Add((byte)Trigger.IAC);
        response.Add((byte)Trigger.SE);

        _requestedVariables.Clear();

        // An empty IS is sent rather than silence. It is a well-formed answer meaning "none of those",
        // and a server left waiting on a reply it will never get is worse than one told nothing.
        await context.SendNegotiationAsync(response.ToArray());
    }

    private static void Validate(string name, string value)
    {
        if (!MnesVariables.IsLegalName(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a legal variable name: MNES allows upper-case letters and "
                + "underscores only.", nameof(name));
        }

        if (!MnesVariables.IsLegalValue(value))
        {
            throw new ArgumentException(
                $"The value for '{name}' contains a byte MNES reserves for framing (VAR, VAL, ESC, "
                + "USERVAR or IAC), which would corrupt the subnegotiation.", nameof(value));
        }
    }

    #endregion
}
