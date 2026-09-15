using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Plugins;
using TelnetNegotiationCore.Models;

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

    private CarriageReturnMode? _carriageReturnMode;
    private TimeSpan? _keepAliveInterval;
    private Func<TelnetInterpreter, CancellationToken, ValueTask>? _keepAliveAsync;
    private readonly List<ITelnetProtocolPlugin> _plugins = new();
    private ProtocolPluginManager? _pluginManager;
    private Models.ClientIdentity? _clientIdentity;

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
    /// Says who this application is, for every protocol that has to name the client (client mode).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is one fact reported down two channels, because they are the same fact: TTYPE answers
    /// with the client name first (MTTS defines the first response that way), and MNES sends
    /// <c>CLIENT_NAME</c>. Set it once here rather than configuring each protocol separately.
    /// </para>
    /// <para>
    /// Without it the library names nobody: TTYPE answers <c>UNKNOWN</c> and NEW-ENVIRON sends no
    /// variables. It will not introduce an application under this library's name.
    /// </para>
    /// </remarks>
    /// <param name="identity">The identity to report</param>
    /// <returns>This builder for chaining</returns>
    /// <exception cref="ArgumentNullException">The identity is null.</exception>
    public TelnetInterpreterBuilder WithClientIdentity(Models.ClientIdentity identity)
    {
        _clientIdentity = identity ?? throw new ArgumentNullException(nameof(identity));
        return this;
    }

    /// <summary>
    /// Says who this application is, by name and optionally version.
    /// See <see cref="WithClientIdentity(Models.ClientIdentity)"/>.
    /// </summary>
    /// <param name="name">The name of the application — not of the library it is built on</param>
    /// <param name="version">The version of the application, if it wants to report one</param>
    /// <returns>This builder for chaining</returns>
    /// <exception cref="ArgumentException">The name is null, empty or whitespace.</exception>
    public TelnetInterpreterBuilder WithClientIdentity(string name, string? version = null)
        => WithClientIdentity(new Models.ClientIdentity(name) { Version = version });

    /// <summary>
    /// Sets the longest line of ordinary input the interpreter will assemble, in bytes.
    /// See <see cref="TelnetInterpreter.MaxBufferSize"/>. Defaults to 5 MiB.
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
    /// Chooses what happens to a carriage return that is not part of a <c>CR LF</c> pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see cref="CarriageReturnMode.Drop"/>, which is what this library has always done.
    /// The named shorthands <see cref="DropCarriageReturns"/>,
    /// <see cref="TreatCarriageReturnNullAsLineEnd"/> and <see cref="PreserveCarriageReturns"/> say
    /// the same thing more legibly at a call site.
    /// </para>
    /// <para>
    /// <c>CR LF</c> ends a line whatever this is set to, and so does a bare <c>LF</c>.
    /// </para>
    /// </remarks>
    /// <param name="mode">What a carriage return means on this connection.</param>
    /// <returns>This builder for chaining</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined mode.</exception>
    public TelnetInterpreterBuilder WithCarriageReturnMode(CarriageReturnMode mode)
    {
        if (mode is not (CarriageReturnMode.Drop or CarriageReturnMode.EndOfLine or CarriageReturnMode.Preserve))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a defined carriage-return mode");

        _carriageReturnMode = mode;
        return this;
    }

    /// <summary>
    /// Discards a carriage return, and consumes a <c>NUL</c> that follows it. The default.
    /// </summary>
    /// <remarks>
    /// Right for a line-oriented consumer with no use for carriage returns, which is most of them.
    /// Equivalent to <c>WithCarriageReturnMode(CarriageReturnMode.Drop)</c>.
    /// </remarks>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder DropCarriageReturns() =>
        WithCarriageReturnMode(CarriageReturnMode.Drop);

    /// <summary>
    /// Treats <c>CR NUL</c> as the end of a line, exactly as <c>CR LF</c> is treated.
    /// </summary>
    /// <remarks>
    /// RFC 1123 §3.3.1 requires this of an ASCII server host reading user input: "CR LF and CR NUL
    /// MUST have the same effect". Right for a server whose clients may send <c>CR NUL</c> for the
    /// end-of-line key. Equivalent to <c>WithCarriageReturnMode(CarriageReturnMode.EndOfLine)</c>.
    /// </remarks>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder TreatCarriageReturnNullAsLineEnd() =>
        WithCarriageReturnMode(CarriageReturnMode.EndOfLine);

    /// <summary>
    /// Delivers a literal carriage return for one that does not begin <c>CR LF</c>.
    /// </summary>
    /// <remarks>
    /// RFC 854's reading of <c>CR NUL</c>, and what libtelnet implements. Right when the peer's
    /// carriage returns carry meaning — a MUD overprinting an ASCII spinner sends bare ones, and the
    /// other modes discard exactly that. Equivalent to
    /// <c>WithCarriageReturnMode(CarriageReturnMode.Preserve)</c>.
    /// </remarks>
    /// <returns>This builder for chaining</returns>
    public TelnetInterpreterBuilder PreserveCarriageReturns() =>
        WithCarriageReturnMode(CarriageReturnMode.Preserve);

    /// <summary>
    /// Enables an idle keep-alive on the connection. Disabled unless this is called.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interval is an <b>idle</b> window, not a fixed heartbeat: it restarts on every outbound
    /// write through <see cref="TelnetInterpreter.WriteToNetworkAsync"/>, so a connection that is
    /// already sending data never sends anything extra. A keep-alive only goes out after a genuine
    /// period of silence.
    /// </para>
    /// <para>
    /// This works in both <see cref="TelnetInterpreter.TelnetMode.Server"/> and
    /// <see cref="TelnetInterpreter.TelnetMode.Client"/> mode: NOP is a plain telnet command that
    /// either end may send at any time (RFC 854), and both ends have the same reason to want it.
    /// </para>
    /// <para>
    /// <b>A keep-alive is not peer-liveness detection.</b> The default payload is <c>IAC NOP</c>,
    /// which the peer is not required to answer, so a successful send only proves the local write
    /// succeeded — not that anyone is still listening. Use it to stop NAT tables, load balancers and
    /// idle timers from dropping a quiet connection. Verifying that the peer is actually alive needs
    /// a round trip the peer must answer, such as TIMING-MARK (RFC 860, option 6), which this
    /// library does not implement.
    /// </para>
    /// <para>
    /// If the send throws — typically because the peer is gone — the exception is logged as a
    /// warning and the keep-alive stops for that connection. It is never rethrown onto the host
    /// application, and it does not disturb the byte-processing loop.
    /// </para>
    /// </remarks>
    /// <param name="interval">
    /// How long the connection may stay silent before a keep-alive is sent, for example
    /// <c>TimeSpan.FromSeconds(30)</c>. Defaults to
    /// <see cref="TelnetInterpreter.DefaultKeepAliveInterval"/> (30 seconds). Must be between
    /// <see cref="TelnetInterpreter.MinimumKeepAliveInterval"/> (1 second) and
    /// <see cref="TelnetInterpreter.MaximumKeepAliveInterval"/> (24 hours).
    /// </param>
    /// <param name="sendAsync">
    /// Optional replacement for the default <c>IAC NOP</c> send. Receives the interpreter and a
    /// token that is cancelled when the interpreter is disposed.
    /// </param>
    /// <returns>This builder for chaining</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The interval is outside
    /// [<see cref="TelnetInterpreter.MinimumKeepAliveInterval"/>,
    /// <see cref="TelnetInterpreter.MaximumKeepAliveInterval"/>]. Out-of-range values are rejected
    /// rather than clamped, so a mistyped interval is never silently turned into a different one.
    /// </exception>
    public TelnetInterpreterBuilder WithKeepAlive(
        TimeSpan? interval = null,
        Func<TelnetInterpreter, CancellationToken, ValueTask>? sendAsync = null)
    {
        var resolved = interval ?? TelnetInterpreter.DefaultKeepAliveInterval;
        TelnetInterpreter.ValidateKeepAliveInterval(resolved, nameof(interval));

        _keepAliveInterval = resolved;
        _keepAliveAsync = sendAsync;
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
    /// automatically wiring up the negotiation write callback to the stream. After calling <see cref="BuildAsync"/>, use
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
        _onNegotiation = data => WriteToStreamAsync(stream, data);
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
    /// <see cref="Stream"/>. A <see cref="PipeReader"/> is created for input, and the negotiation
    /// write callback is wired directly to the stream. The adapter is released when reading ends,
    /// while the caller-owned stream remains open. You do not need to call <see cref="UseStream"/> or
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

        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        _onNegotiation = data => WriteToStreamAsync(stream, data);
        try
        {
            var interpreter = await BuildAsync();
            var readTask = ReadFromOwnedPipeAsync(interpreter, reader, cancellationToken);
            return (interpreter, readTask);
        }
        catch
        {
            await reader.CompleteAsync();
            throw;
        }
    }

    private static async ValueTask WriteToStreamAsync(Stream stream, ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out var segment) && segment.Array is not null)
        {
            await stream.WriteAsync(segment.Array, segment.Offset, segment.Count);
            return;
        }

        var copy = data.ToArray();
        await stream.WriteAsync(copy, 0, copy.Length);
    }

    private static async Task ReadFromOwnedPipeAsync(
        TelnetInterpreter interpreter,
        PipeReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReadFromPipeAsync(interpreter, reader, cancellationToken);
        }
        finally
        {
            await reader.CompleteAsync();
        }
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

    internal T? GetConfiguredPlugin<T>() where T : class, ITelnetProtocolPlugin =>
        _plugins.OfType<T>().LastOrDefault();

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

        // Create the interpreter instance. MaxBufferSize is an init property, so it is assigned here
        // rather than passed to the constructor; the line buffer is allocated from it lazily.
        var interpreter = new TelnetInterpreter(_mode, _logger)
        {
            CallbackOnSubmitAsync = _onSubmit,
            CallbackNegotiationAsync = _onNegotiation,
            CallbackOnByteAsync = byteCallback,
            PluginManager = _pluginManager,
            KeepAliveInterval = _keepAliveInterval,
            KeepAliveAsync = _keepAliveAsync,
            MaxBufferSize = _maxBufferSize ?? TelnetInterpreter.DefaultMaxBufferSize,
            CarriageReturnMode = _carriageReturnMode ?? TelnetInterpreter.DefaultCarriageReturnMode
        };

        // Create protocol context. The generated machine reuses this exact instance (see
        // TelnetInterpreter.SharedProtocolContext) rather than creating its own -- ProtocolContext's
        // shared state is per instance, and this is the one WithClientIdentity below, and every
        // plugin's ConfigureStateMachine, populate.
        var context = new ProtocolContext(interpreter, _pluginManager, _logger);
        interpreter.SharedProtocolContext = context;

        // Publish the client identity before any plugin configures itself, so that the protocols
        // that report it — TTYPE and NEW-ENVIRON — read the same one.
        if (_clientIdentity != null)
        {
            context.SetSharedState(Models.ClientIdentity.SharedStateKey, _clientIdentity);
        }

        try
        {
            // Run each plugin's ConfigureStateMachine hook BEFORE initialization, so any cross-cutting
            // setup it registers (e.g. a server's initial negotiation offer) is in place first.
            _pluginManager.ConfigureStateMachines(context);

            // Initialize plugins in dependency order
            await _pluginManager.InitializePluginsAsync(context);

            await interpreter.StartGeneratedMachineAsync();

            // Build the interpreter (call existing BuildAsync if needed)
            await interpreter.BuildAsync();

            return interpreter;
        }
        catch (Exception buildFailure)
        {
            try
            {
                await interpreter.DisposeAsync();
            }
            catch (Exception disposalFailure)
            {
                throw new AggregateException(
                    "Building the interpreter failed, and cleanup also reported a failure.",
                    buildFailure, disposalFailure);
            }

            ExceptionDispatchInfo.Capture(buildFailure).Throw();
            throw;
        }
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
