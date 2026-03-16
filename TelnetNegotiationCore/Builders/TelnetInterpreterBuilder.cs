using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Builders;

/// <summary>
/// Fluent builder for creating TelnetInterpreter instances with plugin architecture.
/// </summary>
public class TelnetInterpreterBuilder
{
    private TelnetInterpreter.TelnetMode _mode = TelnetInterpreter.TelnetMode.Error;
    private ILogger? _logger;
    private Func<byte[], System.Text.Encoding, TelnetInterpreter, ValueTask>? _onSubmit;
    private Func<ReadOnlyMemory<byte>, ValueTask>? _onNegotiation;
    private int? _maxBufferSize;
    private readonly List<ITelnetProtocolPlugin> _plugins = new();
    private ProtocolPluginManager? _pluginManager;

    public TelnetInterpreterBuilder()
    {
    }

    /// <summary>
    /// Sets the telnet mode (Server or Client).
    /// </summary>
    /// <param name="mode">The telnet mode</param>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder UseMode(TelnetInterpreter.TelnetMode mode)
    {
        _mode = mode;
        return this;
    }

    /// <summary>
    /// Sets the logger instance.
    /// </summary>
    /// <param name="logger">The logger to use</param>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder UseLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Sets the callback for submitted lines.
    /// </summary>
    /// <param name="callback">The callback to invoke on line submission</param>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder OnSubmit(Func<byte[], System.Text.Encoding, TelnetInterpreter, ValueTask> callback)
    {
        _onSubmit = callback;
        return this;
    }

    /// <summary>
    /// Sets the callback for negotiation messages.
    /// </summary>
    /// <param name="callback">The callback to invoke for negotiation</param>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder OnNegotiation(Func<ReadOnlyMemory<byte>, ValueTask> callback)
    {
        _onNegotiation = callback;
        return this;
    }

    /// <summary>
    /// Sets the maximum buffer size.
    /// </summary>
    /// <param name="size">The maximum buffer size in bytes</param>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder WithMaxBufferSize(int size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Buffer size must be positive");
        _maxBufferSize = size;
        return this;
    }

    /// <summary>
    /// Configures the interpreter to use an <see cref="IDuplexPipe"/> for network I/O,
    /// automatically wiring up the negotiation write callback to the pipe's output.
    /// After calling <see cref="BuildAsync"/>, use <see cref="ReadFromPipeAsync"/> to start
    /// reading from the pipe's input, or use <see cref="BuildAndStartAsync(IDuplexPipe, CancellationToken)"/>
    /// which does both in one step.
    /// </summary>
    /// <param name="pipe">The duplex pipe to use for network I/O</param>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder UsePipe(IDuplexPipe pipe)
    {
        if (pipe == null)
            throw new ArgumentNullException(nameof(pipe));
        _onNegotiation = async data => await pipe.Output.WriteAsync(data);
        return this;
    }

    /// <summary>
    /// Configures the interpreter to use a <see cref="Stream"/> for network I/O,
    /// automatically wiring up the negotiation write callback to a <see cref="PipeWriter"/>
    /// created from the stream. After calling <see cref="BuildAsync"/>, use
    /// <see cref="ReadFromPipeAsync"/> with a <see cref="PipeReader"/> created from the
    /// same stream, or use <see cref="BuildAndStartAsync(Stream, CancellationToken)"/>
    /// which does both in one step.
    /// </summary>
    /// <param name="stream">The stream to use for network I/O (e.g. <see cref="NetworkStream"/>)</param>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder UseStream(Stream stream)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));
        var writer = PipeWriter.Create(stream);
        _onNegotiation = async data => await writer.WriteAsync(data);
        return this;
    }

    /// <summary>
    /// Builds the interpreter and starts the network read loop from the given
    /// <see cref="IDuplexPipe"/>. The negotiation write callback is automatically
    /// wired to the pipe's output; you do not need to call <see cref="UsePipe"/> or
    /// <see cref="OnNegotiation"/> separately.
    /// </summary>
    /// <param name="pipe">The duplex pipe to use for network I/O</param>
    /// <param name="cancellationToken">Token to cancel the read loop</param>
    /// <returns>
    /// The configured <see cref="TelnetInterpreter"/> and a <see cref="Task"/> that
    /// completes when the remote end closes the connection or the token is cancelled.
    /// </returns>
    public async Task<(TelnetInterpreter Interpreter, Task ReadTask)> BuildAndStartAsync(
        IDuplexPipe pipe,
        CancellationToken cancellationToken = default)
    {
        if (pipe == null)
            throw new ArgumentNullException(nameof(pipe));

        UsePipe(pipe);
        var interpreter = await BuildAsync();
        var readTask = ReadFromPipeAsync(interpreter, pipe.Input, cancellationToken);
        return (interpreter, readTask);
    }

    /// <summary>
    /// Builds the interpreter and starts the network read loop from the given
    /// <see cref="Stream"/>. <see cref="PipeReader"/> and <see cref="PipeWriter"/> are
    /// created directly from the stream, and the negotiation write callback is automatically
    /// wired to the writer. You do not need to call <see cref="UseStream"/> or
    /// <see cref="OnNegotiation"/> separately.
    /// </summary>
    /// <param name="stream">The stream to use for network I/O (e.g. <see cref="NetworkStream"/>)</param>
    /// <param name="cancellationToken">Token to cancel the read loop</param>
    /// <returns>
    /// The configured <see cref="TelnetInterpreter"/> and a <see cref="Task"/> that
    /// completes when the remote end closes the connection or the token is cancelled.
    /// </returns>
    public async Task<(TelnetInterpreter Interpreter, Task ReadTask)> BuildAndStartAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        var reader = PipeReader.Create(stream);
        var writer = PipeWriter.Create(stream);
        _onNegotiation = async data => await writer.WriteAsync(data);
        var interpreter = await BuildAsync();
        var readTask = ReadFromPipeAsync(interpreter, reader, cancellationToken);
        return (interpreter, readTask);
    }

    /// <summary>
    /// Reads incoming data from <paramref name="reader"/> and feeds it to the
    /// <paramref name="interpreter"/> until the pipe completes or the token is cancelled.
    /// </summary>
    /// <param name="interpreter">The interpreter to feed bytes to</param>
    /// <param name="reader">The pipe reader to read from</param>
    /// <param name="cancellationToken">Token to cancel reading</param>
    public static async Task ReadFromPipeAsync(
        TelnetInterpreter interpreter,
        PipeReader reader,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await reader.ReadAtLeastAsync(1, cancellationToken);

            foreach (var segment in result.Buffer)
            {
                await interpreter.InterpretByteArrayAsync(segment);
            }

            reader.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted)
                break;
        }
    }

    /// <summary>
    /// Builds the interpreter and starts the network read loop from the given
    /// <see cref="TcpClient"/>. The client's underlying <see cref="NetworkStream"/> is used
    /// directly via <see cref="PipeReader"/> and <see cref="PipeWriter"/>; the negotiation
    /// write callback is automatically wired to the stream's writer. You do not need to call
    /// <see cref="UseStream"/> or <see cref="OnNegotiation"/> separately.
    /// </summary>
    /// <param name="client">The connected TCP client</param>
    /// <param name="cancellationToken">Token to cancel the read loop</param>
    /// <returns>
    /// The configured <see cref="TelnetInterpreter"/> and a <see cref="Task"/> that
    /// completes when the remote end closes the connection or the token is cancelled.
    /// </returns>
    public Task<(TelnetInterpreter Interpreter, Task ReadTask)> BuildAndStartAsync(
        TcpClient client,
        CancellationToken cancellationToken = default)
    {
        if (client == null)
            throw new ArgumentNullException(nameof(client));

        return BuildAndStartAsync(client.GetStream(), cancellationToken);
    }

    /// <summary>
    /// Adds a protocol plugin to the interpreter.
    /// Returns a configuration context that allows fluent chaining of plugin-specific configuration
    /// and builder methods. Implicitly converts back to TelnetInterpreterBuilder for continued chaining.
    /// </summary>
    /// <typeparam name="T">The plugin type</typeparam>
    /// <returns>A configuration context that allows chaining plugin configuration and builder methods</returns>
    public PluginConfigurationContext<T> AddPlugin<T>() where T : ITelnetProtocolPlugin, new()
    {
        var plugin = new T();
        _plugins.Add(plugin);
        return new PluginConfigurationContext<T>(this, plugin);
    }

    /// <summary>
    /// Adds a protocol plugin instance to the interpreter.
    /// </summary>
    /// <param name="plugin">The plugin instance</param>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder AddPlugin(ITelnetProtocolPlugin plugin)
    {
        if (plugin == null)
            throw new ArgumentNullException(nameof(plugin));
        
        _plugins.Add(plugin);
        return this;
    }

    /// <summary>
    /// Builds the TelnetInterpreter instance with all configured plugins.
    /// </summary>
    /// <returns>A configured TelnetInterpreter instance</returns>
    public async Task<TelnetInterpreter> BuildAsync()
    {
        // Validate required parameters
        if (_mode == TelnetInterpreter.TelnetMode.Error)
            throw new InvalidOperationException("Telnet mode must be set using UseMode()");
        
        if (_logger == null)
            throw new InvalidOperationException("Logger must be set using UseLogger()");
        
        if (_onSubmit == null)
            throw new InvalidOperationException("Submit callback must be set using OnSubmit()");
        
        if (_onNegotiation == null)
            throw new InvalidOperationException("Negotiation callback must be set using OnNegotiation()");

        // Create plugin manager with logger
        _pluginManager = new ProtocolPluginManager(_logger);

        // Register all plugins
        foreach (var plugin in _plugins)
        {
            _pluginManager.RegisterPlugin(plugin);
        }

        // Check if Echo protocol is configured with a handler
        Func<byte, System.Text.Encoding, ValueTask>? byteCallback = null;
        var echoPlugin = _plugins.OfType<Protocols.EchoProtocol>().FirstOrDefault();
        if (echoPlugin != null)
        {
            var echoHandler = echoPlugin.GetEchoHandler();
            if (echoHandler != null)
            {
                byteCallback = echoHandler;
                _logger.LogInformation("Echo protocol configured with default echo handler");
            }
        }

        // Create the interpreter instance
        var interpreter = new TelnetInterpreter(_mode, _logger)
        {
            CallbackOnSubmitAsync = _onSubmit,
            CallbackNegotiationAsync = _onNegotiation,
            CallbackOnByteAsync = byteCallback,
            PluginManager = _pluginManager
        };

        // Set max buffer size if specified
        if (_maxBufferSize.HasValue)
        {
            // Note: MaxBufferSize is init-only, so this needs to be set during construction
            // For now, we'll log a warning. In a real implementation, we'd need to refactor
            // the constructor to accept this parameter.
            _logger.LogWarning("MaxBufferSize cannot be set after construction. Using default.");
        }

        // Create protocol context
        var context = new ProtocolContext(interpreter, _pluginManager, _logger);

        // Configure state machines for all plugins BEFORE initialization
        // This matches the existing pattern where Setup* methods configure the state machine
        _pluginManager.ConfigureStateMachines(interpreter.TelnetStateMachine, context);

        // Apply safety configuration AFTER protocol configuration
        // This ensures safety catches only apply to truly unhandled triggers
        interpreter.ApplySafetyConfiguration();

        // Initialize plugins in dependency order
        await _pluginManager.InitializePluginsAsync(context);

        // Build the interpreter (call existing BuildAsync if needed)
        await interpreter.BuildAsync();

        return interpreter;
    }

    /// <summary>
    /// Gets the plugin manager for advanced scenarios.
    /// Call this after BuildAsync() to access the plugin manager.
    /// </summary>
    public ProtocolPluginManager? GetPluginManager() => _pluginManager;
}

/// <summary>
/// Provides a fluent configuration context for protocol plugins.
/// Allows chaining plugin-specific configuration methods with builder methods.
/// </summary>
/// <typeparam name="T">The plugin type</typeparam>
public class PluginConfigurationContext<T> where T : ITelnetProtocolPlugin
{
    private readonly TelnetInterpreterBuilder _builder;
    private readonly T _plugin;

    internal PluginConfigurationContext(TelnetInterpreterBuilder builder, T plugin)
    {
        _builder = builder;
        _plugin = plugin;
    }

    /// <summary>
    /// Gets the plugin instance for configuration.
    /// </summary>
    public T Plugin => _plugin;

    /// <summary>
    /// Implicitly converts back to the builder for continued chaining.
    /// </summary>
    public static implicit operator TelnetInterpreterBuilder(PluginConfigurationContext<T> context)
    {
        return context._builder;
    }

    /// <summary>
    /// Continues building with another plugin and returns its configuration context.
    /// </summary>
    public PluginConfigurationContext<TNext> AddPlugin<TNext>() where TNext : ITelnetProtocolPlugin, new()
    {
        return _builder.AddPlugin<TNext>();
    }

    /// <summary>
    /// Adds a protocol plugin instance to the interpreter.
    /// </summary>
    /// <param name="plugin">The plugin instance</param>
    /// <returns>The builder for continued chaining</returns>
    public TelnetInterpreterBuilder AddPlugin(ITelnetProtocolPlugin plugin)
    {
        return _builder.AddPlugin(plugin);
    }

    /// <summary>
    /// Builds the TelnetInterpreter instance.
    /// </summary>
    public Task<Interpreters.TelnetInterpreter> BuildAsync()
    {
        return _builder.BuildAsync();
    }

    /// <summary>
    /// Builds the interpreter and starts the network read loop from the given
    /// <see cref="IDuplexPipe"/>. The negotiation write callback is automatically
    /// wired to the pipe's output.
    /// </summary>
    /// <param name="pipe">The duplex pipe to use for network I/O</param>
    /// <param name="cancellationToken">Token to cancel the read loop</param>
    /// <returns>
    /// The configured <see cref="Interpreters.TelnetInterpreter"/> and a <see cref="Task"/> that
    /// completes when the remote end closes the connection or the token is cancelled.
    /// </returns>
    public Task<(Interpreters.TelnetInterpreter Interpreter, Task ReadTask)> BuildAndStartAsync(
        IDuplexPipe pipe,
        CancellationToken cancellationToken = default)
    {
        return _builder.BuildAndStartAsync(pipe, cancellationToken);
    }

    /// <summary>
    /// Builds the interpreter and starts the network read loop from the given
    /// <see cref="Stream"/> (e.g. <see cref="NetworkStream"/>).
    /// </summary>
    /// <param name="stream">The stream to use for network I/O</param>
    /// <param name="cancellationToken">Token to cancel the read loop</param>
    /// <returns>
    /// The configured <see cref="Interpreters.TelnetInterpreter"/> and a <see cref="Task"/> that
    /// completes when the remote end closes the connection or the token is cancelled.
    /// </returns>
    public Task<(Interpreters.TelnetInterpreter Interpreter, Task ReadTask)> BuildAndStartAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        return _builder.BuildAndStartAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Builds the interpreter and starts the network read loop from the given
    /// <see cref="TcpClient"/>.
    /// </summary>
    /// <param name="client">The connected TCP client</param>
    /// <param name="cancellationToken">Token to cancel the read loop</param>
    /// <returns>
    /// The configured <see cref="Interpreters.TelnetInterpreter"/> and a <see cref="Task"/> that
    /// completes when the remote end closes the connection or the token is cancelled.
    /// </returns>
    public Task<(Interpreters.TelnetInterpreter Interpreter, Task ReadTask)> BuildAndStartAsync(
        TcpClient client,
        CancellationToken cancellationToken = default)
    {
        return _builder.BuildAndStartAsync(client, cancellationToken);
    }
}
