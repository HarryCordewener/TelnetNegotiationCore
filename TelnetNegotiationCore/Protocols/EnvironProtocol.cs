using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
    /// <remarks>
    /// Negotiation acceptance and the subnegotiation are wired to the generated machine (see
    /// <see cref="OnPeerNegotiatedAsync"/> and the <c>OnEnviron*</c> event methods); this hook
    /// survives only to register the server's initial offer, a cross-cutting mechanism independent
    /// of which machine drives byte processing.
    /// </remarks>
    public override void ConfigureStateMachine(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            context.RegisterInitialNegotiation(async () => await WillingEnvironAsync(context));
        }
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

    private void StartNewVar()
    {
        SaveCurrentVariable();
        _collectingVar = true;
        _collectingValue = false;
        _currentVar.Clear();
    }

    private void StartNewValue()
    {
        _collectingVar = false;
        _collectingValue = true;
        _currentValue.Clear();
    }

    private void StartRequestedVar()
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

    private ValueTask OnWontEnvironAsync(IProtocolContext context)
    {
        context.Logger.LogDebug(context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server
            ? "Client won't do ENVIRON - do nothing"
            : "Server won't do ENVIRON - do nothing");
        return OnNegotiatedAsync(false);
    }

    private ValueTask OnDontEnvironAsync(IProtocolContext context)
    {
        context.Logger.LogDebug(context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server
            ? "Client won't do ENVIRON - do nothing"
            : "Server telling client not to send ENVIRON");
        return OnNegotiatedAsync(false);
    }

    /// <summary>WILL and DO are each answered the same way regardless of which side receives them --
    /// unlike NEW-ENVIRON's WILL, ENVIRON's original Stateless configuration wired both server and
    /// client modes to the exact same handler for each.</summary>
    internal async ValueTask OnPeerNegotiatedAsync(byte verb, IProtocolContext context)
    {
        switch (verb)
        {
            case (byte)Trigger.WILL:
                await OnWillEnvironAsync(context);
                break;
            case (byte)Trigger.WONT:
                await OnWontEnvironAsync(context);
                break;
            case (byte)Trigger.DO:
                await OnDoEnvironAsync(context);
                break;
            case (byte)Trigger.DONT:
                await OnDontEnvironAsync(context);
                break;
        }
    }

    /// <summary>The subnegotiation began: reset the same fields AlmostNegotiatingENVIRON's entry did,
    /// before the command byte was even known -- see NewEnvironProtocol.OnNewEnvironStartedAsync for
    /// why that reordering changes nothing observable.</summary>
    internal ValueTask OnEnvironStartedAsync(byte command, IProtocolContext context)
    {
        _currentVar.Clear();
        _currentValue.Clear();
        _collectingVar = false;
        _collectingValue = false;
        _commandType = command;

        if (context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            _requestedVariables.Clear();
        }

        return default;
    }

    internal ValueTask OnEnvironVarMarkerAsync(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            StartNewVar();
        }
        else
        {
            StartRequestedVar();
        }

        return default;
    }

    /// <summary>A VALUE marker only ever arrives server-side in the original configuration -- a
    /// client's EvaluatingENVIRONVar never permitted it.</summary>
    internal ValueTask OnEnvironValueMarkerAsync(IProtocolContext context)
    {
        if (context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
        {
            StartNewValue();
        }

        return default;
    }

    internal ValueTask OnEnvironDataAsync(ReadOnlyMemory<byte> data, IProtocolContext context)
    {
        if (_collectingVar)
        {
            _currentVar.AddRange(data.ToArray());
        }
        else if (_collectingValue)
        {
            _currentValue.AddRange(data.ToArray());
        }

        return default;
    }

    /// <summary>
    /// A server expects IS (the client reporting its variables); a client expects SEND (the server
    /// asking for them). Routing by <see cref="IProtocolContext.Mode"/> alone, ignoring which command
    /// the peer actually sent, means a peer that sends the other side's command -- a client sending
    /// SEND to the server, say -- has its data parsed under the wrong rules: a request for variable
    /// names read as if it were a report of their values, or the reverse. Neither side of this protocol
    /// is specified to accept the other's command, so a mismatch is rejected rather than guessed at.
    /// </summary>
    internal ValueTask OnEnvironEndedAsync(IProtocolContext context)
    {
        var server = context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server;
        var expectedCommand = server ? (byte)Trigger.IS : (byte)Trigger.SEND;

        if (_commandType != expectedCommand)
        {
            context.Logger.LogWarning(
                "ENVIRON: {Mode} received command {Command} instead of the expected {Expected}; ignoring",
                context.Mode, _commandType, expectedCommand);
            return default;
        }

        return server
            ? CompleteEnvironFromServerAsync(context)
            : SendEnvironmentVariablesFromClientAsync(context);
    }

    private async ValueTask WillingEnvironAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Announcing willingness to ENVIRON!");
        await context.SendNegotiationAsync(s_willEnviron);
    }

    private async ValueTask OnDoEnvironAsync(IProtocolContext context)
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

    private async ValueTask OnWillEnvironAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server will do ENVIRON");
        await OnNegotiatedAsync(true);
        await context.SendNegotiationAsync(s_doEnviron);
    }

    internal async ValueTask CompleteEnvironFromServerAsync(IProtocolContext context)
    {
        SaveCurrentVariable();

        context.Logger.LogInformation("Received ENVIRON variables: {Count} environment variables",
            _environmentVariables.Count);

        if (_onEnvironmentVariables != null)
        {
            await _onEnvironmentVariables(_environmentVariables);
        }
    }

    internal async ValueTask SendEnvironmentVariablesFromClientAsync(IProtocolContext context)
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
