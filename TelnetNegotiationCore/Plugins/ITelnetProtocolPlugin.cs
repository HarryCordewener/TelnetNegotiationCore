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
    /// Gets whether this plugin is currently enabled -- attached to the interpreter and processing.
    /// True from the moment <see cref="InitializeAsync"/> runs, regardless of whether the peer has
    /// agreed to anything on the wire yet. For "did the peer actually negotiate this option", see
    /// <see cref="IsNegotiated"/>.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets whether the peer has genuinely negotiated this option: a WILL/DO or DO/WILL exchange
    /// has completed and the peer agreed. False before that ever happens, and false again if the
    /// peer later refuses or withdraws the option -- unlike <see cref="IsEnabled"/>, which does not
    /// move once the plugin is attached. Set only through <see cref="OnNegotiatedAsync"/>, which a
    /// protocol calls from its own <see cref="ConfigureStateMachine"/> handlers at the point real
    /// negotiation for its option resolves.
    /// </summary>
    bool IsNegotiated { get; }

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
    /// Called the moment real wire negotiation for this plugin's option resolves -- <c>true</c>
    /// when the peer agreed (a positive WILL/DO exchange completed), <c>false</c> when it refused,
    /// or when an option it had previously agreed to is later withdrawn. Sets
    /// <see cref="IsNegotiated"/> and is what a protocol's own <see cref="ConfigureStateMachine"/>
    /// handlers call; it is public so <see cref="Plugins.ProtocolPluginManager"/> and tests can also
    /// drive it directly.
    /// </summary>
    /// <param name="isNegotiated">True if the peer just agreed, false if it just refused or withdrew.</param>
    ValueTask OnNegotiatedAsync(bool isNegotiated);

    /// <summary>
    /// Disposes resources used by the protocol.
    /// </summary>
    ValueTask DisposeAsync();
}
