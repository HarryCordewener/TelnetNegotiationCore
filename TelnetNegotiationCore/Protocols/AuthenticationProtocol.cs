using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Authentication protocol plugin (RFC 2941)
/// https://datatracker.ietf.org/doc/html/rfc2941
/// </summary>
/// <remarks>
/// This implementation provides a flexible framework for authentication.
/// By default, it rejects all authentication types by responding with NULL.
/// Consumers can inject custom authentication behavior via callbacks to
/// handle specific authentication mechanisms.
/// </remarks>
public class AuthenticationProtocol : TelnetProtocolPluginBase
{
    /// <summary>
    /// Authentication type NULL - used to indicate no authentication types are supported
    /// </summary>
    private const byte AUTH_NULL = 0;

    /// <summary>
    /// Authentication command IS - sent by client
    /// </summary>
    private const byte AUTH_IS = 0;

    /// <summary>
    /// Authentication command SEND - sent by server
    /// </summary>
    private const byte AUTH_SEND = 1;

    /// <summary>
    /// Authentication command REPLY - sent by server
    /// </summary>
    private const byte AUTH_REPLY = 2;

    /// <summary>
    /// Authentication command NAME - optional command to specify account name
    /// </summary>
    private const byte AUTH_NAME = 3;

    /// <summary>
    /// No authentication modifiers - used when rejecting with NULL type
    /// </summary>
    private const byte AUTH_NO_MODIFIERS = 0;

    // Callbacks for authentication behavior injection
    private Func<byte[], ValueTask<byte[]?>>? _onAuthenticationRequest;
    private Func<byte[], ValueTask>? _onAuthenticationResponse;
    private Func<ValueTask<List<(byte AuthType, byte Modifiers)>>>? _authenticationTypesProvider;
    
    // State for capturing authentication data during subnegotiation
    private List<byte> _authRequestData = new();

    /// <inheritdoc />
    public override Type ProtocolType => typeof(AuthenticationProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Authentication (RFC 2941)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

    /// <summary>
    /// Sets the callback invoked when the client receives an authentication request (SEND) from the server.
    /// The callback receives the list of authentication type pairs offered by the server and should return
    /// the authentication response bytes (IS command with auth type and data), or null to reject with NULL.
    /// </summary>
    /// <param name="callback">Callback to handle authentication requests</param>
    /// <returns>This instance for fluent chaining</returns>
    public AuthenticationProtocol OnAuthenticationRequest(Func<byte[], ValueTask<byte[]?>>? callback)
    {
        _onAuthenticationRequest = callback;
        return this;
    }

    /// <summary>
    /// Sets the callback invoked when the server receives an authentication response (IS) from the client.
    /// The callback receives the authentication data from the client for processing/validation.
    /// </summary>
    /// <param name="callback">Callback to handle authentication responses</param>
    /// <returns>This instance for fluent chaining</returns>
    public AuthenticationProtocol OnAuthenticationResponse(Func<byte[], ValueTask>? callback)
    {
        _onAuthenticationResponse = callback;
        return this;
    }

    /// <summary>
    /// Sets the provider function that returns the list of supported authentication types.
    /// Used by the server to offer authentication options to the client.
    /// If not set, the server will send an empty list (rejecting authentication).
    /// </summary>
    /// <param name="provider">Function that provides list of (AuthType, Modifiers) tuples</param>
    /// <returns>This instance for fluent chaining</returns>
    public AuthenticationProtocol WithAuthenticationTypes(Func<ValueTask<List<(byte AuthType, byte Modifiers)>>>? provider)
    {
        _authenticationTypesProvider = provider;
        return this;
    }

    /// <summary>
    /// Sends an authentication request to the remote party.
    /// Used by servers to initiate authentication after negotiation.
    /// </summary>
    /// <param name="authenticationTypes">List of supported authentication type pairs</param>
    public async ValueTask SendAuthenticationRequestAsync(List<(byte AuthType, byte Modifiers)> authenticationTypes)
    {
        if (!IsEnabled)
            return;

        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            AUTH_SEND
        };

        foreach (var (authType, modifiers) in authenticationTypes)
        {
            bytes.Add(authType);
            bytes.Add(modifiers);
        }

        bytes.Add((byte)Trigger.IAC);
        bytes.Add((byte)Trigger.SE);

        await Context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <summary>
    /// Sends an authentication response to the server.
    /// Used by clients to respond to authentication requests.
    /// </summary>
    /// <param name="authData">The authentication data including auth type, modifiers, and credentials</param>
    public async ValueTask SendAuthenticationResponseAsync(byte[] authData)
    {
        if (!IsEnabled)
            return;

        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            AUTH_IS
        };

        bytes.AddRange(authData);
        bytes.Add((byte)Trigger.IAC);
        bytes.Add((byte)Trigger.SE);

        await Context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <summary>
    /// Sends an authentication reply from server to client.
    /// Used to send authentication status or challenges.
    /// </summary>
    /// <param name="replyData">The reply data including status or challenge</param>
    public async ValueTask SendAuthenticationReplyAsync(byte[] replyData)
    {
        if (!IsEnabled)
            return;

        var bytes = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            AUTH_REPLY
        };

        bytes.AddRange(replyData);
        bytes.Add((byte)Trigger.IAC);
        bytes.Add((byte)Trigger.SE);

        await Context.SendNegotiationAsync(bytes.ToArray());
    }

    /// <inheritdoc />
    public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        context.Logger.LogInformation("Configuring Authentication state machine");
        
        // Register Authentication protocol handlers with the context
        context.SetSharedState("Authentication_Protocol", this);
        
        // Configure state machine transitions for Authentication protocol
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
        // Server side: Receives WILL AUTHENTICATION from client
        stateMachine.Configure(State.Willing)
            .Permit(Trigger.AUTHENTICATION, State.WillAuthentication);

        stateMachine.Configure(State.Refusing)
            .Permit(Trigger.AUTHENTICATION, State.WontAuthentication);

        stateMachine.Configure(State.WillAuthentication)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await OnClientWillAuthenticateAsync(context));

        stateMachine.Configure(State.WontAuthentication)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Client won't authenticate"));

        // Server handles IS subnegotiation (authentication response from client)
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.AUTHENTICATION, State.AlmostNegotiatingAuthentication);

        stateMachine.Configure(State.AlmostNegotiatingAuthentication)
            .Permit(Trigger.IS, State.NegotiatingAuthenticationSend)
            .OnEntry(() =>
            {
                context.Logger.LogDebug("Receiving authentication response from client");
                _authRequestData = new List<byte>();
            });

        // Handle the IS command - capture all auth data until IAC
        stateMachine.Configure(State.NegotiatingAuthenticationSend)
            .Permit(Trigger.IAC, State.CompletingAuthenticationNegotiation);
        
        // Capture all other triggers as authentication data
        TriggerHelper.ForAllTriggersButIAC(t => 
            stateMachine.Configure(State.NegotiatingAuthenticationSend)
                .OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), (OneOf<byte, Trigger> b) => _authRequestData.Add(b.AsT0))
                .PermitReentry(t));

        stateMachine.Configure(State.CompletingAuthenticationNegotiation)
            .Permit(Trigger.SE, State.SendingAuthenticationResponse)
            .OnEntry(() => context.Logger.LogDebug("Received end of IS subnegotiation"));

        stateMachine.Configure(State.SendingAuthenticationResponse)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await ProcessAuthenticationResponseAsync(context));

        // Server initiates authentication negotiation
        context.RegisterInitialNegotiation(async () => await SendDoAuthenticationAsync(context));
    }

    private void ConfigureAsClient(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
    {
        // Client side: Receives DO AUTHENTICATION from server
        stateMachine.Configure(State.Do)
            .Permit(Trigger.AUTHENTICATION, State.DoAuthentication);

        stateMachine.Configure(State.Dont)
            .Permit(Trigger.AUTHENTICATION, State.DontAuthentication);

        stateMachine.Configure(State.DoAuthentication)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await OnServerRequestsAuthenticationAsync(context));

        stateMachine.Configure(State.DontAuthentication)
            .SubstateOf(State.Accepting)
            .OnEntry(() => context.Logger.LogDebug("Server doesn't want authentication"));

        // Client handles SEND subnegotiation
        stateMachine.Configure(State.SubNegotiation)
            .Permit(Trigger.AUTHENTICATION, State.AlmostNegotiatingAuthentication);

        stateMachine.Configure(State.AlmostNegotiatingAuthentication)
            .Permit(Trigger.SEND, State.NegotiatingAuthenticationSend)
            .OnEntry(() =>
            {
                context.Logger.LogDebug("Starting authentication subnegotiation");
                _authRequestData = new List<byte>();
            });

        // Handle the SEND command - capture all auth type pairs until IAC
        stateMachine.Configure(State.NegotiatingAuthenticationSend)
            .Permit(Trigger.IAC, State.CompletingAuthenticationNegotiation);
        
        // Capture all other triggers as auth type/modifier pairs
        TriggerHelper.ForAllTriggersButIAC(t => 
            stateMachine.Configure(State.NegotiatingAuthenticationSend)
                .OnEntryFrom(context.Interpreter.ParameterizedTrigger(t), (OneOf<byte, Trigger> b) => _authRequestData.Add(b.AsT0))
                .PermitReentry(t));

        stateMachine.Configure(State.CompletingAuthenticationNegotiation)
            .Permit(Trigger.SE, State.SendingAuthenticationResponse)
            .OnEntry(() => context.Logger.LogDebug("Received end of SEND subnegotiation"));

        stateMachine.Configure(State.SendingAuthenticationResponse)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await SendAuthenticationNullResponseAsync(context));
    }

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync()
    {
        Context.Logger.LogInformation("Authentication Protocol initialized");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolEnabledAsync()
    {
        Context.Logger.LogInformation("Authentication Protocol enabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnProtocolDisabledAsync()
    {
        Context.Logger.LogInformation("Authentication Protocol disabled");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    #region State Machine Handlers

    private async ValueTask SendDoAuthenticationAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server requesting client to authenticate (DO AUTHENTICATION)");
        await context.SendNegotiationAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.DO, 
            (byte)Trigger.AUTHENTICATION 
        });
    }

    private async ValueTask OnClientWillAuthenticateAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Client willing to authenticate - sending authentication types");
        
        // Get the list of authentication types from the provider (or empty list to reject)
        var authTypes = new List<(byte AuthType, byte Modifiers)>();
        if (_authenticationTypesProvider != null)
        {
            authTypes = await _authenticationTypesProvider();
        }

        // Send SEND subnegotiation with authentication types
        await SendAuthenticationRequestAsync(authTypes);
    }

    private async ValueTask OnServerRequestsAuthenticationAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Server requests authentication (DO AUTHENTICATION) - responding with WILL");
        await context.SendNegotiationAsync(new byte[] 
        { 
            (byte)Trigger.IAC, 
            (byte)Trigger.WILL, 
            (byte)Trigger.AUTHENTICATION 
        });
    }

    private async ValueTask SendAuthenticationNullResponseAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Processing authentication request");
        
        byte[]? responseData = null;
        
        // If callback is provided, let it handle the authentication
        if (_onAuthenticationRequest != null)
        {
            responseData = await _onAuthenticationRequest(_authRequestData.ToArray());
        }
        
        // If no response data or callback not set, send NULL rejection
        if (responseData == null)
        {
            context.Logger.LogDebug("Sending IS NULL response - rejecting all authentication types");
            responseData = new byte[] { AUTH_NULL, AUTH_NO_MODIFIERS };
        }
        
        await SendAuthenticationResponseAsync(responseData);
    }

    private async ValueTask ProcessAuthenticationResponseAsync(IProtocolContext context)
    {
        context.Logger.LogDebug("Processing authentication response from client");
        
        // Invoke callback if provided
        if (_onAuthenticationResponse != null)
        {
            await _onAuthenticationResponse(_authRequestData.ToArray());
        }
        else
        {
            context.Logger.LogDebug("No authentication response handler configured - authentication data ignored");
        }
    }

    #endregion
}
