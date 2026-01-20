using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Authentication protocol plugin (RFC 2941)
/// https://datatracker.ietf.org/doc/html/rfc2941
/// </summary>
/// <remarks>
/// This implementation provides basic support for the AUTHENTICATION option.
/// Currently, it rejects all authentication types by responding with NULL.
/// This allows the telnet session to proceed without authentication while
/// properly handling the negotiation protocol.
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

    /// <inheritdoc />
    public override Type ProtocolType => typeof(AuthenticationProtocol);

    /// <inheritdoc />
    public override string ProtocolName => "Authentication (RFC 2941)";

    /// <inheritdoc />
    public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

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
            .OnEntry(() => context.Logger.LogDebug("Starting authentication subnegotiation"));

        // Handle the SEND command - we'll consume all bytes until IAC
        stateMachine.Configure(State.NegotiatingAuthenticationSend)
            .Permit(Trigger.IAC, State.CompletingAuthenticationNegotiation);
        
        // Allow all other triggers to be ignored (these are auth type pairs we don't support)
        TriggerHelper.ForAllTriggersButIAC(t => 
            stateMachine.Configure(State.NegotiatingAuthenticationSend).PermitReentry(t));

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
        context.Logger.LogDebug("Client willing to authenticate - but we reject all auth types");
        
        // Send SEND subnegotiation with no authentication types (immediate reject)
        // This tells the client we don't support any authentication methods
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            AUTH_SEND,
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
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
        context.Logger.LogDebug("Sending IS NULL response - rejecting all authentication types");
        
        // Respond with IS NULL to indicate we don't support any authentication type
        await context.SendNegotiationAsync(new byte[]
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.AUTHENTICATION,
            AUTH_IS,
            AUTH_NULL,  // NULL authentication type
            AUTH_NO_MODIFIERS,  // No modifiers
            (byte)Trigger.IAC,
            (byte)Trigger.SE
        });
    }

    #endregion
}
