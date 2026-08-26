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
    /// Installs the decoder every inbound byte passes through before the telnet state machine sees
    /// it, or removes it with null. Any transform already installed is disposed.
    /// </summary>
    /// <remarks>
    /// This is the seam for protocols that change what the bytes after their negotiation *mean* —
    /// MCCP being the one in this library. Installing from a state machine handler takes effect
    /// from the next byte, because handlers run on the byte-processing loop itself.
    /// </remarks>
    /// <param name="transform">The transform to install, or null to go back to raw telnet</param>
    void SetInboundByteTransform(Interpreters.IInboundByteTransform? transform);

    /// <summary>
    /// Installs the encoder every outbound write passes through on its way to the network, or
    /// removes it with null, optionally writing one last thing in the clear first. Any transform
    /// already installed is disposed once no write is inside it.
    /// </summary>
    /// <remarks>
    /// This is serialized against outbound writes, which is what makes both halves safe: a write
    /// cannot be inside an encoder being disposed, and nothing can be written between
    /// <paramref name="sendFirst"/> and the switch-over. A protocol that announces its switch-over
    /// with a marker — MCCP's <c>IAC SB MCCPn IAC SE</c> — must pass that marker here rather than
    /// sending it separately, or another thread's write can land after the peer has already started
    /// decoding.
    /// </remarks>
    /// <param name="transform">The transform to install, or null to go back to raw telnet</param>
    /// <param name="sendFirst">A final write to make in the clear before the transform takes over</param>
    ValueTask SetOutboundByteTransformAsync(
        Interpreters.IOutboundByteTransform? transform,
        ReadOnlyMemory<byte> sendFirst = default);

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

    /// <summary>True when bytes have arrived since the last line submission or prompt boundary.</summary>
    bool HasPartialLine { get; }

    /// <summary>True once a genuine <c>IAC GA</c> or <c>IAC EOR</c> prompt has fired on this connection.</summary>
    bool HasSeenMarkedPrompt { get; }

    /// <summary>
    /// Hands the standing partial line to <see cref="Interpreters.TelnetInterpreter.LastPromptBytes"/>
    /// and clears it. Call this at a prompt boundary, before invoking the prompt callback.
    /// </summary>
    /// <param name="marked">True for a server marker, false for a boundary inferred from silence.</param>
    void TakePartialLineAsPrompt(bool marked);
}
