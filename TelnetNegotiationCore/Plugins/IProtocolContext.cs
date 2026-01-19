using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Plugins;

/// <summary>
/// Context interface providing access to telnet interpreter functionality for plugins.
/// This is the bridge between plugins and the core telnet interpreter.
/// </summary>
public interface IProtocolContext
{
    /// <summary>
    /// Gets the logger instance for this context.
    /// </summary>
    ILogger Logger { get; }

    /// <summary>
    /// Gets the current encoding used for text interpretation.
    /// </summary>
    Encoding CurrentEncoding { get; }

    /// <summary>
    /// Sets the current encoding used for text interpretation.
    /// </summary>
    /// <param name="encoding">The new encoding to use</param>
    void SetEncoding(Encoding encoding);

    /// <summary>
    /// Gets the telnet mode (Server or Client).
    /// </summary>
    Interpreters.TelnetInterpreter.TelnetMode Mode { get; }

    /// <summary>
    /// Gets the state machine instance.
    /// </summary>
    StateMachine<State, Trigger> StateMachine { get; }

    /// <summary>
    /// Sends negotiation bytes to the remote endpoint.
    /// </summary>
    /// <param name="bytes">The bytes to send</param>
    ValueTask SendNegotiationAsync(ReadOnlyMemory<byte> bytes);

    /// <summary>
    /// Writes data to the output buffer.
    /// </summary>
    /// <param name="data">The data to write</param>
    ValueTask WriteToBufferAsync(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Gets a plugin by its type.
    /// </summary>
    /// <typeparam name="T">The plugin type to retrieve</typeparam>
    /// <returns>The plugin instance or null if not registered</returns>
    T? GetPlugin<T>() where T : class, ITelnetProtocolPlugin;

    /// <summary>
    /// Gets a plugin by its runtime type.
    /// </summary>
    /// <param name="pluginType">The plugin type to retrieve</param>
    /// <returns>The plugin instance or null if not registered</returns>
    ITelnetProtocolPlugin? GetPlugin(Type pluginType);

    /// <summary>
    /// Checks if a plugin of the specified type is registered and enabled.
    /// </summary>
    /// <typeparam name="T">The plugin type to check</typeparam>
    /// <returns>True if the plugin is registered and enabled</returns>
    bool IsPluginEnabled<T>() where T : class, ITelnetProtocolPlugin;

    /// <summary>
    /// Checks if a plugin of the specified type is registered and enabled.
    /// </summary>
    /// <param name="pluginType">The plugin type to check</param>
    /// <returns>True if the plugin is registered and enabled</returns>
    bool IsPluginEnabled(Type pluginType);

    /// <summary>
    /// Gets all registered plugins.
    /// </summary>
    IReadOnlyCollection<ITelnetProtocolPlugin> GetAllPlugins();

    /// <summary>
    /// Registers a shared state value that plugins can use to communicate.
    /// </summary>
    /// <param name="key">The key for the shared state</param>
    /// <param name="value">The value to store</param>
    void SetSharedState(string key, object? value);

    /// <summary>
    /// Gets a shared state value.
    /// </summary>
    /// <param name="key">The key for the shared state</param>
    /// <returns>The value or null if not found</returns>
    object? GetSharedState(string key);

    /// <summary>
    /// Tries to get a shared state value with a specific type.
    /// </summary>
    /// <typeparam name="T">The expected type of the value</typeparam>
    /// <param name="key">The key for the shared state</param>
    /// <param name="value">The value if found and of correct type</param>
    /// <returns>True if the value was found and is of the correct type</returns>
    bool TryGetSharedState<T>(string key, out T? value);

    /// <summary>
    /// Registers a function to be called during initial negotiation (after BuildAsync).
    /// This is used by protocols to announce their willingness to negotiate.
    /// </summary>
    /// <param name="negotiationFunc">The async function to call during initialization</param>
    void RegisterInitialNegotiation(Func<ValueTask> negotiationFunc);

    /// <summary>
    /// Gets the underlying telnet interpreter instance for advanced protocol scenarios.
    /// </summary>
    Interpreters.TelnetInterpreter Interpreter { get; }
}
