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
/// <para>
/// This implementation provides a flexible framework for authentication negotiation.
/// By default, it rejects all authentication types by responding with NULL, allowing
/// sessions to proceed without authentication. This maintains backward compatibility
/// while providing the protocol negotiation framework.
/// </para>
/// <para>
/// Consumers can inject custom authentication behavior via callbacks to implement
/// specific authentication mechanisms such as Kerberos, SRP, RSA, or SSL.
/// </para>
/// <para><strong>Server-Side Usage:</strong></para>
/// <code>
/// var telnet = await new TelnetInterpreterBuilder()
///     .UseMode(TelnetInterpreter.TelnetMode.Server)
///     .AddPlugin&lt;AuthenticationProtocol&gt;()
///         .WithAuthenticationTypes(async () => new List&lt;(byte, byte)&gt; { (5, 0) })
///         .OnAuthenticationResponse(async (authData) => ValidateCredentials(authData))
///     .BuildAsync();
/// </code>
/// <para><strong>Client-Side Usage:</strong></para>
/// <code>
/// var telnet = await new TelnetInterpreterBuilder()
///     .UseMode(TelnetInterpreter.TelnetMode.Client)
///     .AddPlugin&lt;AuthenticationProtocol&gt;()
///         .OnAuthenticationRequest(async (authTypes) => ProvideCredentials(authTypes))
///     .BuildAsync();
/// </code>
/// <para><strong>Authentication Types:</strong></para>
/// <list type="bullet">
/// <item><description>0 - NULL (no authentication)</description></item>
/// <item><description>1 - KERBEROS_V4</description></item>
/// <item><description>2 - KERBEROS_V5</description></item>
/// <item><description>5 - SRP (Secure Remote Password)</description></item>
/// <item><description>6 - RSA</description></item>
/// <item><description>7 - SSL</description></item>
/// </list>
/// <para><strong>Modifiers:</strong></para>
/// <list type="bullet">
/// <item><description>0x01 - AUTH_WHO_MASK (0=CLIENT_TO_SERVER, 1=SERVER_TO_CLIENT)</description></item>
/// <item><description>0x02 - AUTH_HOW_MASK (0=ONE_WAY, 2=MUTUAL)</description></item>
/// <item><description>0x04 - ENCRYPT_USING_TELOPT</description></item>
/// <item><description>0x08 - INI_CRED_FWD_ON</description></item>
/// <item><description>0x10 - ENCRYPT_AFTER_EXCHANGE</description></item>
/// </list>
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
    /// </summary>
    /// <param name="callback">
    /// Callback to handle authentication requests. The callback receives a byte array containing
    /// authentication type pairs (each pair is 2 bytes: auth type and modifiers) offered by the server.
    /// Should return the authentication response data (auth type, modifiers, and credentials as bytes),
    /// or null to reject all authentication types with NULL response.
    /// </param>
    /// <returns>This instance for fluent chaining</returns>
    /// <remarks>
    /// <para><strong>Client-side only.</strong> This callback is invoked when the server sends a SEND subnegotiation
    /// with a list of supported authentication types.</para>
    /// <example>
    /// <code>
    /// .OnAuthenticationRequest(async (authTypePairs) =>
    /// {
    ///     // authTypePairs format: [type1, mod1, type2, mod2, ...]
    ///     var authType = authTypePairs[0];
    ///     var modifiers = authTypePairs[1];
    ///     var credentials = await GetCredentials(authType);
    ///     return new byte[] { authType, modifiers }.Concat(credentials).ToArray();
    /// })
    /// </code>
    /// </example>
    /// </remarks>
    public AuthenticationProtocol OnAuthenticationRequest(Func<byte[], ValueTask<byte[]?>>? callback)
    {
        _onAuthenticationRequest = callback;
        return this;
    }

    /// <summary>
    /// Sets the callback invoked when the server receives an authentication response (IS) from the client.
    /// </summary>
    /// <param name="callback">
    /// Callback to handle authentication responses. The callback receives a byte array containing
    /// the authentication data from the client (auth type, modifiers, and credential data).
    /// Use this to validate credentials and optionally send a REPLY message.
    /// </param>
    /// <returns>This instance for fluent chaining</returns>
    /// <remarks>
    /// <para><strong>Server-side only.</strong> This callback is invoked when the client sends an IS subnegotiation
    /// with authentication credentials.</para>
    /// <example>
    /// <code>
    /// .OnAuthenticationResponse(async (authData) =>
    /// {
    ///     var authType = authData[0];
    ///     var modifiers = authData[1];
    ///     var credentials = authData.Skip(2).ToArray();
    ///     
    ///     var isValid = await ValidateCredentials(authType, credentials);
    ///     if (!isValid)
    ///     {
    ///         var authPlugin = telnet.PluginManager.GetPlugin&lt;AuthenticationProtocol&gt;();
    ///         await authPlugin.SendAuthenticationReplyAsync(new byte[] { authType, modifiers, 0xFF });
    ///     }
    /// })
    /// </code>
    /// </example>
    /// </remarks>
    public AuthenticationProtocol OnAuthenticationResponse(Func<byte[], ValueTask>? callback)
    {
        _onAuthenticationResponse = callback;
        return this;
    }

    /// <summary>
    /// Sets the provider function that returns the list of supported authentication types.
    /// </summary>
    /// <param name="provider">
    /// Function that provides a list of authentication type pairs. Each tuple contains
    /// (AuthType, Modifiers) where AuthType identifies the mechanism (e.g., 5=SRP, 6=RSA)
    /// and Modifiers specifies options (e.g., 0x02=MUTUAL, 0x04=ENCRYPT_USING_TELOPT).
    /// If not set, the server will send an empty list (rejecting authentication).
    /// </param>
    /// <returns>This instance for fluent chaining</returns>
    /// <remarks>
    /// <para><strong>Server-side only.</strong> Used by the server to offer authentication options to the client.</para>
    /// <para><strong>Common Authentication Types:</strong></para>
    /// <list type="bullet">
    /// <item><description>0 - NULL (no authentication)</description></item>
    /// <item><description>1 - KERBEROS_V4</description></item>
    /// <item><description>2 - KERBEROS_V5</description></item>
    /// <item><description>5 - SRP (Secure Remote Password)</description></item>
    /// <item><description>6 - RSA</description></item>
    /// <item><description>7 - SSL</description></item>
    /// </list>
    /// <para><strong>Common Modifiers (combine with bitwise OR):</strong></para>
    /// <list type="bullet">
    /// <item><description>0x00 - CLIENT_TO_SERVER + ONE_WAY (default)</description></item>
    /// <item><description>0x01 - SERVER_TO_CLIENT</description></item>
    /// <item><description>0x02 - MUTUAL (two-way authentication)</description></item>
    /// <item><description>0x04 - ENCRYPT_USING_TELOPT (negotiate encryption via TELOPT ENCRYPT)</description></item>
    /// <item><description>0x08 - INI_CRED_FWD_ON (credentials will be forwarded)</description></item>
    /// <item><description>0x10 - ENCRYPT_AFTER_EXCHANGE (encrypt immediately after auth)</description></item>
    /// </list>
    /// <example>
    /// <code>
    /// .WithAuthenticationTypes(async () => new List&lt;(byte, byte)&gt;
    /// {
    ///     (5, 0),     // SRP, one-way, no encryption
    ///     (6, 0x02),  // RSA, mutual authentication
    ///     (2, 0x06)   // Kerberos V5, mutual + encrypt using telopt
    /// })
    /// </code>
    /// </example>
    /// </remarks>
    public AuthenticationProtocol WithAuthenticationTypes(Func<ValueTask<List<(byte AuthType, byte Modifiers)>>>? provider)
    {
        _authenticationTypesProvider = provider;
        return this;
    }

    /// <summary>
    /// Sends an authentication request to the remote party.
    /// </summary>
    /// <param name="authenticationTypes">
    /// List of supported authentication type pairs. Each tuple contains (AuthType, Modifiers).
    /// The list is sent in order of preference (most preferred first).
    /// </param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// <para><strong>Server-side.</strong> Used by servers to initiate authentication or send additional
    /// authentication type options during the negotiation.</para>
    /// <para>This sends an IAC SB AUTHENTICATION SEND [type pairs] IAC SE message.</para>
    /// <example>
    /// <code>
    /// await authPlugin.SendAuthenticationRequestAsync(new List&lt;(byte, byte)&gt;
    /// {
    ///     (5, 0),     // SRP
    ///     (6, 0x02)   // RSA with mutual auth
    /// });
    /// </code>
    /// </example>
    /// </remarks>
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
    /// </summary>
    /// <param name="authData">
    /// The authentication data including auth type, modifiers, and credentials.
    /// Format: [authType, modifiers, ...credential bytes]
    /// </param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// <para><strong>Client-side.</strong> Used by clients to respond to authentication requests.</para>
    /// <para>This sends an IAC SB AUTHENTICATION IS [auth data] IAC SE message.</para>
    /// <example>
    /// <code>
    /// // Send SRP authentication with credentials
    /// await authPlugin.SendAuthenticationResponseAsync(new byte[]
    /// {
    ///     5, 0,                   // SRP, no modifiers
    ///     0x01, 0x02, 0x03, 0x04  // Credential data
    /// });
    /// </code>
    /// </example>
    /// </remarks>
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
    /// </summary>
    /// <param name="replyData">
    /// The reply data including auth type, modifiers, and status/challenge data.
    /// Format: [authType, modifiers, ...status or challenge bytes]
    /// </param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// <para><strong>Server-side.</strong> Used to send authentication status, acceptance, rejection, or challenges.</para>
    /// <para>This sends an IAC SB AUTHENTICATION REPLY [reply data] IAC SE message.</para>
    /// <para>Common status values:</para>
    /// <list type="bullet">
    /// <item><description>0x00 - Success/Accept</description></item>
    /// <item><description>0xFF - Reject/Failure</description></item>
    /// <item><description>Other values are mechanism-specific (e.g., challenge data)</description></item>
    /// </list>
    /// <example>
    /// <code>
    /// // Accept authentication
    /// await authPlugin.SendAuthenticationReplyAsync(new byte[] { 5, 0, 0x00 });
    /// 
    /// // Reject authentication
    /// await authPlugin.SendAuthenticationReplyAsync(new byte[] { 5, 0, 0xFF });
    /// 
    /// // Send challenge for multi-round auth
    /// await authPlugin.SendAuthenticationReplyAsync(new byte[]
    /// {
    ///     5, 0,                   // SRP
    ///     0x01, 0x02, 0x03, 0x04  // Challenge data
    /// });
    /// </code>
    /// </example>
    /// </remarks>
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
