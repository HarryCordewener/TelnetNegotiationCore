using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Terminal Type protocol plugin - RFC 1091 and MTTS
/// https://datatracker.ietf.org/doc/html/rfc1091
/// https://tintin.mudhalla.net/protocols/mtts/
/// </summary>
public class TerminalTypeProtocol : TelnetProtocolPluginBase
{
    private static readonly byte[] s_willTtype = new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE };
    private static readonly byte[] s_doTtype = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE };

    /// <summary>
    /// RFC 1091's own word for a terminal that will not name itself, and what this library answers
    /// when the application has not said who it is.
    /// </summary>
    public const string UnknownTerminalType = "UNKNOWN";

    private ImmutableList<string> _terminalTypes = [];
    private ImmutableList<string> _configuredTerminalTypes = [];
    private MttsCapabilities _clientCapabilities = MttsCapabilities.None;
    private int _currentTerminalType = -1;
    private byte[] _ttypeByteState = [];
    private int _ttypeIndex = 0;

    /// <summary>
    /// A list of terminal types for this connection
    /// </summary>
    public ImmutableList<string> TerminalTypes => _terminalTypes;

    /// <summary>
    /// The MTTS bitvector this client reports, in client mode: what the application claimed through
    /// <see cref="ClientIdentity.Mtts"/>, together with what the library can observe about its own
    /// negotiation stack (see <see cref="ObservedCapabilities"/>). <see cref="MttsCapabilities.None"/>
    /// in server mode, and whenever there is nothing to claim — in which case no MTTS response is
    /// sent at all.
    /// </summary>
    public MttsCapabilities ClientCapabilities => _clientCapabilities;

    /// <summary>
    /// Replaces the terminal types this client reports, in the order it reports them, instead of
    /// deriving them from <see cref="ClientIdentity"/>. MTTS reads the first entry as the client
    /// name, the second as the terminal type and the third as the <c>MTTS &lt;bitvector&gt;</c>
    /// claim; nothing is added to what is passed here, so an application taking this route states
    /// its whole list itself.
    /// </summary>
    /// <param name="terminalTypes">The terminal types to report, in order</param>
    /// <returns>This instance for fluent chaining</returns>
    /// <exception cref="ArgumentException">The list is null, empty, or contains a blank entry.</exception>
    public TerminalTypeProtocol WithTerminalTypes(params string[] terminalTypes)
    {
        if (terminalTypes == null || terminalTypes.Length == 0)
        {
            throw new ArgumentException(
                "At least one terminal type is required. Omit this call to report "
                + $"\"{UnknownTerminalType}\".", nameof(terminalTypes));
        }

        if (terminalTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A terminal type cannot be blank.", nameof(terminalTypes));
        }

        _configuredTerminalTypes = [.. terminalTypes];
        return this;
    }

    /// <summary>
    /// The MTTS capabilities the library can see for itself, rather than take an application's word
    /// for: <see cref="MttsCapabilities.Mnes"/> when <see cref="NewEnvironProtocol"/> is registered,
    /// so the claim is true exactly when this connection will answer MNES; and
    /// <see cref="MttsCapabilities.Utf8"/> when the interpreter is decoding UTF-8, which it does
    /// unless RFC 2066 CHARSET negotiates something else.
    /// </summary>
    /// <remarks>
    /// Everything else MTTS defines is a claim about a terminal — colour depth, mouse tracking, a
    /// screen reader — which this library cannot observe and will not make on an application's
    /// behalf. Those come from <see cref="ClientIdentity.Mtts"/>.
    /// </remarks>
    /// <param name="context">The protocol context to inspect</param>
    /// <returns>The capabilities this connection can honestly claim on its own</returns>
    public static MttsCapabilities ObservedCapabilities(IProtocolContext context)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        var capabilities = MttsCapabilities.None;

        if (string.Equals(context.CurrentEncoding.WebName, "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            capabilities |= MttsCapabilities.Utf8;
        }

        if (context.GetPlugin<NewEnvironProtocol>() != null)
        {
            capabilities |= MttsCapabilities.Mnes;
        }

        return capabilities;
    }

    /// <summary>
    /// The current selected Terminal Type
    /// </summary>
    public string CurrentTerminalType => _currentTerminalType == -1
        ? "unknown"
        : _terminalTypes[Math.Min(_currentTerminalType, _terminalTypes.Count - 1)];

    /// <inheritdoc />
    public override Type ProtocolType => typeof(TerminalTypeProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Terminal Type (RFC 1091 + MTTS)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Terminal Type state machine");
        
        // Register TerminalType protocol handlers with the context
        context.SetSharedState("TerminalType_Protocol", this);
        
        // Common state machine configuration
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.TTYPE, State.WillDoTType);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.TTYPE, State.WontDoTType);

        stateMachine.Configure(State.Do)
            .Permit(Trigger.TTYPE, State.DoTType);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.TTYPE, State.DontTType);
        
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            ConfigureAsServer(stateMachine, context);
        }
        else
        {
            ConfigureAsClient(stateMachine, context);
        }
    }
    
    private void ConfigureAsClient(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        _currentTerminalType = -1;
        _terminalTypes = _terminalTypes.AddRange(ResolveClientTerminalTypes(context));

        stateMachine.Configure(State.DoTType)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await WillDoTerminalTypeAsync(context));

        stateMachine.Configure(State.DontTType)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Server telling us not to Terminal Type"));

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.TTYPE, State.AlmostNegotiatingTerminalType);

        stateMachine.Configure(State.AlmostNegotiatingTerminalType)
            .Permit(Trigger.SEND, State.NegotiatingTerminalType);

        stateMachine.Configure(State.NegotiatingTerminalType)
            .Permit(Trigger.IAC, State.CompletingTerminalType)
            .OnEntry(GetTerminalType);

        stateMachine.Configure(State.CompletingTerminalType)
            .OnEntryAsync(async () => await ReportNextAvailableTerminalTypeAsync(context))
            .Permit(Trigger.SE, State.Accepting);
    }
    
    /// <summary>
    /// What this client answers TTYPE with, in order: the application's name, its terminal type,
    /// and its MTTS bitvector — none of which the library makes up. An entry is left out rather than
    /// invented, and a repeat is left out too, because a repeated response is how a client tells a
    /// server its list has ended.
    /// </summary>
    private IEnumerable<string> ResolveClientTerminalTypes(IProtocolContext context)
    {
        if (!_configuredTerminalTypes.IsEmpty)
        {
            _clientCapabilities = ParseMttsResponse(_configuredTerminalTypes, context.Logger);
            return _configuredTerminalTypes;
        }

        context.TryGetSharedState<ClientIdentity>(ClientIdentity.SharedStateKey, out var identity);
        _clientCapabilities = ObservedCapabilities(context) | (identity?.Mtts ?? MttsCapabilities.None);

        var responses = new List<string> { identity?.Name ?? UnknownTerminalType };

        if (!string.IsNullOrWhiteSpace(identity?.TerminalType))
        {
            responses.Add(identity!.TerminalType!.Trim());
        }

        if (_clientCapabilities != MttsCapabilities.None)
        {
            responses.Add($"MTTS {((int)_clientCapabilities).ToString(CultureInfo.InvariantCulture)}");
        }

        return responses.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the <c>MTTS &lt;bitvector&gt;</c> entry out of a list of terminal types, so that a
    /// bitvector a peer sent, or one an application stated itself through
    /// <see cref="WithTerminalTypes"/>, is understood rather than only forwarded.
    /// </summary>
    private static MttsCapabilities ParseMttsResponse(IEnumerable<string> terminalTypes, ILogger logger)
    {
        var mtts = terminalTypes.FirstOrDefault(x => x.StartsWith("MTTS ", StringComparison.OrdinalIgnoreCase));
        if (mtts == null)
        {
            return MttsCapabilities.None;
        }

        if (!int.TryParse(mtts.Substring(5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitvector))
        {
            logger.LogWarning("Ignoring a terminal type that looks like an MTTS bitvector but is not one: {TerminalType}", mtts);
            return MttsCapabilities.None;
        }

        return (MttsCapabilities)bitvector;
    }

    private void ConfigureAsServer(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        stateMachine.Configure(State.WillDoTType)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await RequestTerminalTypeAsync(context));

        stateMachine.Configure(State.WontDoTType)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Connection: {ConnectionState}", "Client won't do Terminal Type"));

        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.TTYPE, State.AlmostNegotiatingTerminalType);

        stateMachine.Configure(State.AlmostNegotiatingTerminalType)
            .Permit(Trigger.IS, State.NegotiatingTerminalType);

        stateMachine.Configure(State.NegotiatingTerminalType)
            .Permit(Trigger.IAC, State.EscapingTerminalTypeValue)
            .OnEntry(GetTerminalType);

        TriggerHelper.ForAllTriggersButIAC(t =>
            stateMachine.Configure(State.NegotiatingTerminalType).Permit(t, State.EvaluatingTerminalType));
        TriggerHelper.ForAllTriggers(t =>
            stateMachine.Configure(State.EvaluatingTerminalType).OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), CaptureTerminalType));
        TriggerHelper.ForAllTriggersButIAC(t => stateMachine.Configure(State.EvaluatingTerminalType).PermitReentry(t));

        stateMachine.Configure(State.EvaluatingTerminalType)
            .Permit(Trigger.IAC, State.EscapingTerminalTypeValue);

        stateMachine.Configure(State.EscapingTerminalTypeValue)
            .Permit(Trigger.IAC, State.EvaluatingTerminalType)
            .Permit(Trigger.SE, State.CompletingTerminalType);

        stateMachine.Configure(State.CompletingTerminalType)
            .OnEntryAsync(async () => await CompleteTerminalTypeAsServerAsync(context))
            .SubstateOf(State.Accepting);

        context.RegisterInitialNegotiation(async () => await SendDoTerminalTypeAsync(context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Terminal Type Protocol initialized");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Terminal Type Protocol enabled");
        return default(ValueTask);
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Terminal Type Protocol disabled");
        _terminalTypes = [];
        _currentTerminalType = -1;
        _ttypeByteState = [];
        _ttypeIndex = 0;
        return default(ValueTask);
    }

    /// <summary>
    /// Processes a terminal type byte
    /// </summary>
    public void ProcessTerminalTypeByte(byte value)
    {
        if (!IsEnabled)
            return;

        if (_ttypeIndex >= _ttypeByteState.Length)
        {
            Array.Resize(ref _ttypeByteState, _ttypeIndex + 1);
        }

        _ttypeByteState[_ttypeIndex++] = value;
    }

    /// <summary>
    /// Requests the next terminal type from the client
    /// </summary>
    public async ValueTask RequestTerminalTypeAsync(IProtocolContext context)
    {
        if (!IsEnabled)
            return;

        Context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the client, to send the next Terminal Type.");
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        _terminalTypes = [];
        _ttypeByteState = [];
        _ttypeIndex = 0;
        return default(ValueTask);
    }
    
    #region State Machine Handlers

    private void GetTerminalType(StateMachine<State, Trigger>.Transition _)
    {
        _ttypeByteState = new byte[1024];
        _ttypeIndex = 0;
    }

    private void CaptureTerminalType(OneOf<byte, Trigger> b)
    {
        if (_ttypeIndex >= _ttypeByteState.Length) return;
        _ttypeByteState[_ttypeIndex] = b.AsT0;
        _ttypeIndex++;
    }

    private async ValueTask CompleteTerminalTypeAsServerAsync(IProtocolContext context)
    {
        var TType = Encoding.ASCII.GetString(_ttypeByteState, 0, _ttypeIndex);
        if (_terminalTypes.Contains(TType))
        {
            _currentTerminalType = (_currentTerminalType + 1) % _terminalTypes.Count;
            
            var MTTS = _terminalTypes.FirstOrDefault(x => x.StartsWith("MTTS ", StringComparison.OrdinalIgnoreCase));
            if (MTTS != null)
            {
                var capabilities = ParseMttsResponse([MTTS], context.Logger);

                _terminalTypes = _terminalTypes.AddRange(MttsCapabilityNames.Expand(capabilities));
                _terminalTypes = _terminalTypes.Remove(MTTS);
            }

            context.Logger.LogDebug("Connection: {ConnectionState}: {@TerminalTypes}",
                "Completing Terminal Type negotiation. List as follows", _terminalTypes);
                
            UpdateInterpreterProperties(context);
        }
        else
        {
            context.Logger.LogTrace("Connection: {ConnectionState}: {TerminalType}",
                "Registering Terminal Type. Requesting the next", TType);
            _terminalTypes = _terminalTypes.Add(TType);
            _currentTerminalType++;
            
            UpdateInterpreterProperties(context);
            
            await RequestTerminalTypeAsync(context);
        }
    }

    private async ValueTask WillDoTerminalTypeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the other party, Willing to do Terminal Type.");
        await context.SendNegotiationAsync(s_willTtype);
    }

    private async ValueTask SendDoTerminalTypeAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Connection: {ConnectionState}", "Telling the other party, to do Terminal Type.");
        await context.SendNegotiationAsync(s_doTtype);
    }

    private async ValueTask ReportNextAvailableTerminalTypeAsync(IProtocolContext context)
    {
        _currentTerminalType = (_currentTerminalType + 1) % (_terminalTypes.Count + 1);
        context.Logger.LogDebug("Connection: {ConnectionState}", "Reporting the next Terminal Type to the server.");
        byte[] terminalType =
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS,
            .. Encoding.ASCII.GetBytes(CurrentTerminalType),
            (byte)Trigger.IAC, (byte)Trigger.SE
        ];

        await context.SendNegotiationAsync(terminalType);
        
        UpdateInterpreterProperties(context);
    }
    
    /// <summary>
    /// The interpreter carries the same list and selection for consumers reading
    /// <see cref="Interpreters.TelnetInterpreter.TerminalTypes"/> and
    /// <see cref="Interpreters.TelnetInterpreter.CurrentTerminalType"/>, the latter of which it derives
    /// from the selected index.
    /// </summary>
    private void UpdateInterpreterProperties(IProtocolContext context)
    {
        context.Interpreter.TerminalTypes = _terminalTypes;
        context.Interpreter.CurrentTerminalTypeIndex = _currentTerminalType;
    }

    #endregion
}
