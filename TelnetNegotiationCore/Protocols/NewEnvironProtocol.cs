using System;
using System.Collections.Generic;
using System.Globalization;
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
/// <para>
/// This protocol supports optional configuration. Call <see cref="OnEnvironmentVariables"/> to set up
/// the callback that will handle environment variables a peer sends.
/// MNES (Mud New Environment Standard) is an extension indicated by MTTS flag 512.
/// </para>
/// <para>
/// In client mode, <b>nothing is sent that the application did not supply</b>: the variables a
/// server receives are the ones set through
/// <see cref="Builders.TelnetInterpreterBuilder.WithClientIdentity(Models.ClientIdentity)"/> and
/// <see cref="WithClientEnvironmentVariables"/>, and nothing else. Configure neither and a server's
/// SEND is answered with an empty IS.
/// </para>
/// </remarks>
[RequiredMethod("OnEnvironmentVariables", Description = "Configure the callback to handle environment variable updates (optional but recommended)")]
public class NewEnvironProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willNewEnviron = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON };
    private static readonly byte[] s_doNewEnviron = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NEWENVIRON };

    private readonly List<byte> _currentVar = [];
    private readonly List<byte> _currentValue = [];
    private readonly List<(bool IsUserVar, string? Name)> _requestedVariables = [];
    private readonly Dictionary<string, string> _environmentVariables = new();
    private readonly Dictionary<string, string> _userVariables = new();
    private IReadOnlyDictionary<string, string> _clientEnvironmentVariables = new Dictionary<string, string>();
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
    /// Sets the variables this client sends when a server asks for them (client mode only).
    /// Defaults to <b>none</b>: a server is told nothing about the machine the client runs on unless
    /// the application decided to tell it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MNES — the MUD profile of NEW-ENVIRON — defines the names a MUD client would want here, and
    /// <see cref="MnesVariables"/> spells them once: <c>CLIENT_NAME</c>, <c>CLIENT_VERSION</c>,
    /// <c>TERMINAL_TYPE</c>, <c>MTTS</c>, <c>CHARSET</c> and <c>IPADDRESS</c>. It also carries
    /// <see cref="MnesVariables.IsLegalName"/> and <see cref="MnesVariables.IsLegalValue"/>, for an
    /// application that wants to check itself against the profile before configuring anything.
    /// The first four are better set once through
    /// <see cref="Builders.TelnetInterpreterBuilder.WithClientIdentity(Models.ClientIdentity)"/>,
    /// which also feeds the TTYPE responses; a variable set here overrides the identity-derived one
    /// of the same name.
    /// </para>
    /// <para>
    /// RFC 1572's <c>USER</c> is the account to log in <i>as</i>, not the operating-system account
    /// the client runs under. This library never fills it in from the environment; an application
    /// that genuinely has a login name to send can set it here, having decided that itself.
    /// </para>
    /// </remarks>
    /// <param name="environmentVariables">The variables to send, in the order to send them</param>
    /// <returns>This instance for fluent chaining</returns>
    public NewEnvironProtocol WithClientEnvironmentVariables(IReadOnlyDictionary<string, string>? environmentVariables)
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
        _requestedVariables.Clear();
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
        FlushRequestedVariable();
        _collectingVar = true;
        _collectingValue = false;
        _isUserVar = false;
        _currentVar.Clear();
    }

    private void StartRequestedUserVar(OneOf<byte, Trigger> _)
    {
        FlushRequestedVariable();
        _collectingVar = true;
        _collectingValue = false;
        _isUserVar = true;
        _currentVar.Clear();
    }

    /// <summary>
    /// Records the variable a server has just finished naming in its SEND. A type marker carrying no
    /// name at all is RFC 1572's request for every variable of that type, and is recorded as such
    /// rather than as a variable called "".
    /// </summary>
    private void FlushRequestedVariable()
    {
        if (!_collectingVar)
        {
            return;
        }

        _requestedVariables.Add((
            _isUserVar,
            _currentVar.Count > 0 ? Encoding.ASCII.GetString(_currentVar.ToArray()) : null));

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

    private async ValueTask SendEnvironmentVariablesAsync(StateMachine<State, Trigger>.Transition _, IProtocolContext context)
    {
        // Client received SEND request from server
        context.Logger.LogDebug("Server requested environment variables, sending response...");

        var response = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.NEWENVIRON,
            (byte)Trigger.IS
        };

        FlushRequestedVariable();

        foreach (var (isUserVar, name, value) in ResolveRequestedVariables(context))
        {
            response.Add(isUserVar ? (byte)Trigger.NEWENVIRON_USERVAR : (byte)Trigger.NEWENVIRON_VAR);
            AppendEscaped(response, name);

            // A name with no VALUE at all is RFC 1572's "I do not have that one". A name with an
            // empty VALUE is a variable that is defined and empty, which is a different answer.
            if (value != null)
            {
                response.Add((byte)Trigger.NEWENVIRON_VALUE);
                AppendEscaped(response, value);
            }
        }

        _requestedVariables.Clear();

        response.Add((byte)Trigger.IAC);
        response.Add((byte)Trigger.SE);

        await context.SendNegotiationAsync(response.ToArray());
    }

    /// <summary>
    /// Answers the SEND the server actually sent. RFC 1572: <i>"If a list of variables is specified,
    /// then only those variables should be sent"</i>, and the reply owes <i>"a response for each
    /// 'type ...' explicitly requested"</i> in the order it was requested — a variable this client
    /// does not have among them, answered by its name carrying no value.
    /// </summary>
    /// <remarks>
    /// Being asked for a variable is not consent to go and find one: a requested name that the
    /// application did not configure is undefined, including <c>USER</c>.
    /// </remarks>
    private List<(bool IsUserVar, string Name, string? Value)> ResolveRequestedVariables(IProtocolContext context)
    {
        var available = ResolveClientVariables(context);
        var response = new List<(bool IsUserVar, string Name, string? Value)>();

        if (_requestedVariables.Count == 0)
        {
            foreach (var (name, value) in available)
            {
                response.Add((false, name, value));
            }

            return response;
        }

        foreach (var (isUserVar, requested) in _requestedVariables)
        {
            if (requested == null)
            {
                // Every variable of that type. Everything this library sends is a well-known VAR —
                // MNES's own names included — so "every USERVAR" is an empty answer.
                if (!isUserVar)
                {
                    foreach (var (name, value) in available)
                    {
                        response.Add((false, name, value));
                    }
                }

                continue;
            }

            var match = isUserVar
                ? default
                : available.FirstOrDefault(x => string.Equals(x.Key, requested, StringComparison.Ordinal));

            response.Add((isUserVar, requested, match.Key != null ? match.Value : null));
        }

        return response;
    }

    /// <summary>
    /// Everything this client tells a server about itself, in MNES's own order: the identity the
    /// application set, then whatever else it configured, with a configured variable overriding the
    /// identity-derived one of the same name.
    /// </summary>
    /// <remarks>
    /// An application that configured neither sends nothing, which leaves the server in the same
    /// position as one talking to a client that never negotiated NEW-ENVIRON at all. Nothing here
    /// is read from the environment of the process: RFC 1572's <c>USER</c> is the account to log in
    /// as, which has nothing to do with the operating-system account the client happens to run
    /// under, and a server has no business learning the latter.
    /// </remarks>
    private List<KeyValuePair<string, string>> ResolveClientVariables(IProtocolContext context)
    {
        var configured = _clientEnvironmentVariables;
        var variables = new List<KeyValuePair<string, string>>();

        if (context.TryGetSharedState<ClientIdentity>(ClientIdentity.SharedStateKey, out var identity) && identity != null)
        {
            foreach (var (name, value) in IdentityVariables(identity, context))
            {
                variables.Add(new KeyValuePair<string, string>(
                    name, configured.TryGetValue(name, out var overridden) ? overridden : value));
            }
        }

        foreach (var (name, value) in configured)
        {
            if (!variables.Any(x => x.Key == name))
            {
                variables.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return variables;
    }

    /// <summary>
    /// The MNES variables an identity supplies, in the order the specification lists them, named
    /// from <see cref="MnesVariables"/> rather than by literal. The MTTS bitvector is the one the
    /// Terminal Type plugin reports, so that the two channels a client introduces itself through
    /// cannot disagree.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> IdentityVariables(ClientIdentity identity, IProtocolContext context)
    {
        yield return new KeyValuePair<string, string>(MnesVariables.ClientName, identity.Name);

        if (!string.IsNullOrWhiteSpace(identity.Version))
        {
            yield return new KeyValuePair<string, string>(MnesVariables.ClientVersion, identity.Version!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(identity.TerminalType))
        {
            yield return new KeyValuePair<string, string>(MnesVariables.TerminalType, identity.TerminalType!.Trim());
        }

        var capabilities = context.GetPlugin<TerminalTypeProtocol>()?.ClientCapabilities
            ?? (identity.Mtts ?? MttsCapabilities.None) | TerminalTypeProtocol.ObservedCapabilities(context);

        if (capabilities != MttsCapabilities.None)
        {
            yield return new KeyValuePair<string, string>(
                MnesVariables.Mtts, ((int)capabilities).ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Writes a name or value with RFC 1572's escapes. MNES forbids the VAR, VALUE, ESC, USERVAR and
    /// IAC bytes inside a value, but an application supplies these strings, so one that carries a
    /// control byte anyway must not be able to end the subnegotiation early.
    /// </summary>
    internal static void AppendEscaped(List<byte> target, string text)
    {
        foreach (var b in Encoding.ASCII.GetBytes(text))
        {
            switch (b)
            {
                case (byte)Trigger.NEWENVIRON_VAR:
                case (byte)Trigger.NEWENVIRON_VALUE:
                case (byte)Trigger.NEWENVIRON_ESC:
                case (byte)Trigger.NEWENVIRON_USERVAR:
                    target.Add((byte)Trigger.NEWENVIRON_ESC);
                    break;
                case (byte)Trigger.IAC:
                    target.Add((byte)Trigger.IAC);
                    break;
            }

            target.Add(b);
        }
    }

    #endregion
}
