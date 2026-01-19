using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Plugins;

/// <summary>
/// Interface for telnet protocol plugins.
/// Each plugin represents a complete protocol implementation identified by its class type.
/// </summary>
public interface ITelnetProtocolPlugin
{
    /// <summary>
    /// Gets the unique type identifier for this protocol plugin.
    /// This is used for plugin registration and discovery instead of option codes.
    /// </summary>
    Type ProtocolType { get; }

    /// <summary>
    /// Gets the human-readable name of this protocol.
    /// </summary>
    string ProtocolName { get; }

    /// <summary>
    /// Gets the list of protocol types that this plugin depends on.
    /// Dependencies will be initialized before this plugin.
    /// </summary>
    IReadOnlyCollection<Type> Dependencies { get; }

    /// <summary>
    /// Gets whether this plugin is currently enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Initializes the protocol plugin.
    /// Called once during telnet interpreter setup.
    /// </summary>
    /// <param name="context">The protocol context for interacting with the telnet system</param>
    /// <returns>A task representing the initialization</returns>
    ValueTask InitializeAsync(IProtocolContext context);

    /// <summary>
    /// Configures the state machine for this protocol.
    /// Called during telnet interpreter construction.
    /// </summary>
    /// <param name="stateMachine">The state machine to configure</param>
    /// <param name="context">The protocol context</param>
    void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context);

    /// <summary>
    /// Called when the protocol is enabled at runtime.
    /// </summary>
    ValueTask OnEnabledAsync();

    /// <summary>
    /// Called when the protocol is disabled at runtime.
    /// </summary>
    ValueTask OnDisabledAsync();

    /// <summary>
    /// Disposes resources used by the protocol.
    /// </summary>
    ValueTask DisposeAsync();
}
