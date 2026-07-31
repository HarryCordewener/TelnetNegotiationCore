using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Generated;
using OneOf;
using Microsoft.Extensions.Logging;
using LocalMoreLinq;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// TODO: Telnet Interpreter should take in a simple Interface object that can Read & Write from / to a Stream!
/// Read Byte, Write Byte, and a Buffer Size. That way we can test it.
/// </summary>
public partial class TelnetInterpreter
{
    private readonly Dictionary<byte, Trigger> _isDefinedDictionary = new();

    /// <summary>
    /// A list of functions to call at the start.
    /// </summary>
    private readonly List<Func<ValueTask>> _initialCall;

    /// <summary>
    /// The plugin manager for protocol plugins (null if not using plugin-based API).
    /// </summary>
    public Plugins.ProtocolPluginManager? PluginManager { get; internal set; }

    /// <summary>
    /// The current Encoding used for interpreting incoming non-negotiation text, and what we should send on outbound.
    /// </summary>
    public Encoding CurrentEncoding { get; internal set; } = Encoding.UTF8;

    /// <summary>
    /// Telnet state machine
    /// </summary>
    public StateMachine<State, Trigger> TelnetStateMachine { get; }

    /// <summary>
    /// A cache of parameterized triggers.
    /// </summary>
    private readonly ParameterizedTriggers _parameterizedTriggers;

    /// <summary>
    /// Maximum buffer size for telnet messages (default 5MB).
    /// </summary>
    public int MaxBufferSize { get; init; } = 5242880;

    /// <summary>
    /// Local buffer for accumulating line data.
    /// </summary>
    private readonly byte[] _buffer;

    /// <summary>
    /// Buffer position where we are writing.
    /// </summary>
    private int _bufferPosition;

    /// <summary>
    /// Channel for byte processing pipeline with backpressure.
    /// </summary>
    private readonly Channel<byte> _byteChannel;

    /// <summary>
    /// Unbounded channel for protocol negotiation messages (typically low volume).
    /// </summary>
    private readonly Channel<byte[]> _negotiationChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    /// <summary>
    /// SemaphoreSlim used to serialize all writes to the output stream,
    /// preventing concurrent write conflicts on the dual-channel telnet pipe.
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Cancellation token source for graceful shutdown.
    /// </summary>
    private readonly CancellationTokenSource _processingCts = new();

    /// <summary>
    /// Background processing task.
    /// </summary>
    private Task? _processingTask;

    /// <summary>
    /// Optional decoder sitting between the network and the state machine. Written from the
    /// byte-processing loop (a plugin installs it from a state machine entry/exit handler) and
    /// read from that same loop, so no locking is needed beyond publishing the reference.
    /// </summary>
    private IInboundByteTransform? _inboundTransform;

    /// <summary>
    /// Optional encoder sitting between the library and the network. Read from every writer
    /// thread, hence the volatile access.
    /// </summary>
    private IOutboundByteTransform? _outboundTransform;

    /// <summary>
    /// Helper function for Byte parameterized triggers.
    /// </summary>
    /// <param name="t">The Trigger</param>
    /// <returns>A Parameterized trigger</returns>
    internal StateMachine<State, Trigger>.TriggerWithParameters<OneOf<byte, Trigger>> ParameterizedTrigger(Trigger t)
        => _parameterizedTriggers.ParameterizedTrigger(TelnetStateMachine, t);

    /// <summary>
    /// The Logger
    /// </summary>
    private readonly ILogger _logger;

    public enum TelnetMode
    {
        Error = 0,
        Client = 1,
        Server = 2
    };

    public TelnetMode Mode { get; }

    /// <summary>
    /// Callback to run on a submission (linefeed)
    /// </summary>
    public required Func<byte[], Encoding, TelnetInterpreter, ValueTask>? CallbackOnSubmitAsync { get; init; }

    /// <summary>
    /// Callback to the output stream directly for negotiation.
    /// Internal use - negotiation messages are queued through _negotiationChannel.
    /// </summary>
    public required Func<ReadOnlyMemory<byte>, ValueTask> CallbackNegotiationAsync { get; init; }

    /// <summary>
    /// Callback per byte.
    /// </summary>
    public Func<byte, Encoding, ValueTask>? CallbackOnByteAsync { get; init; }

    /// <summary>
    /// Constructor, sets up for standard Telnet protocol with NAWS and Character Set support.
    /// </summary>
    /// <remarks>
    /// After calling this constructor, one should subscribe to the Triggers, register a Stream, and then run Process()
    /// </remarks>
    /// <param name="mode">Server or Client mode</param>
    /// <param name="logger">A Serilog Logger. If null, we will use the default one with a Context of the Telnet Interpreter.</param>
    public TelnetInterpreter(TelnetMode mode, ILogger logger)
    {
        Mode = mode;
        _logger = logger;
        logger.BeginScope(new Dictionary<string, object> { { "TelnetMode", mode } });

        _initialCall = [];
        TelnetStateMachine = new StateMachine<State, Trigger>(State.Accepting);
        _parameterizedTriggers = new ParameterizedTriggers();

        // Initialize buffer with configurable size
        _buffer = new byte[MaxBufferSize];

        // Create bounded channel with backpressure (max 10,000 bytes buffered)
        _byteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.Wait,  // Backpressure: block producer if full
            SingleReader = true,   // Optimization: only one consumer
            SingleWriter = false   // Multiple threads may write
        });

        SupportedCharacterSets = new Lazy<byte[]>(CharacterSets, true);

        new List<Func<StateMachine<State, Trigger>, StateMachine<State, Trigger>>>
        {
            // NOTE: SetupSafeNegotiation must run AFTER protocol ConfigureStateMachine calls
            // so it only adds safety catches for truly unhandled triggers.
            // It's now called explicitly by TelnetInterpreterBuilder after ConfigureStateMachines.
            
            SetupStandardProtocol
        }.AggregateRight(TelnetStateMachine, (func, stateMachine) => func(stateMachine));

        if (logger.IsEnabled(LogLevel.Trace))
        {
            TelnetStateMachine.OnTransitioned(transition => _logger.LogTrace(
                "Telnet StateMachine: {Source} --[{Trigger}({TriggerByte})]--> {Destination}",
                transition.Source, transition.Trigger, transition.Parameters[0], transition.Destination));
        }
    }

    /// <summary>
    /// Validates the configuration, then sets up the initial calls for negotiation.
    /// </summary>
    /// <returns>The Telnet Interpreter</returns>
    public async ValueTask<TelnetInterpreter> BuildAsync()
    {
        var validatedInterpreter = Validate();

        // Start background processing task
        _processingTask = Task.Run(() => ProcessBytesAsync(_processingCts.Token));

        // Start the idle keep-alive loop, if one was configured.
        StartKeepAlive();

        foreach (var t in _initialCall)
        {
            await t();
        }

        return validatedInterpreter;
    }

    /// <summary>
    /// Setup standard processes.
    /// </summary>
    /// <param name="tsm">The state machine.</param>
    /// <returns>Itself</returns>
    private StateMachine<State, Trigger> SetupStandardProtocol(StateMachine<State, Trigger> tsm)
    {
        // If we are in Accepting mode, these should be interpreted as regular characters.
        // EXCEPTION: NEWLINE should trigger submission (Act), not start a new character sequence
        TriggerHelper.ForAllTriggersButIAC(t =>
        {
            if (t == Trigger.NEWLINE)
            {
                tsm.Configure(State.Accepting).Permit(t, State.Act);
            }
            else
            {
                tsm.Configure(State.Accepting).Permit(t, State.ReadingCharacters);
            }
        });

        // Standard triggers, which are fine in the Awaiting state and should just be interpreted as a character in this state.
        tsm.Configure(State.ReadingCharacters)
            .SubstateOf(State.Accepting)
            .Permit(Trigger.NEWLINE, State.Act);

        // Configure OnEntryFrom for all triggers to write bytes to buffer
        // EXCEPT IAC which has special handling below
        TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.ReadingCharacters)
            .OnEntryFromAsync(ParameterizedTrigger(t), async x => await WriteToBufferAndAdvanceAsync(x)));

        // Allow re-entry for continued character reading (critical fix for multi-byte data)
        // Exclude NEWLINE since it transitions to Act
        TriggerHelper.ForAllTriggersButIAC(t =>
        {
            if (t != Trigger.NEWLINE)
            {
                tsm.Configure(State.ReadingCharacters).PermitReentry(t);
            }
        });

        // We've gotten a newline. We interpret this as time to act and send a signal back.
        tsm.Configure(State.Act)
            .SubstateOf(State.Accepting)
            .OnEntryAsync(async () => await WriteToOutput());

        // SubNegotiation
        tsm.Configure(State.Accepting)
            .Permit(Trigger.IAC, State.StartNegotiation);

        // Escaped IAC, interpret as actual IAC
        tsm.Configure(State.StartNegotiation)
            .Permit(Trigger.IAC, State.ReadingCharacters)
            .Permit(Trigger.WILL, State.Willing)
            .Permit(Trigger.WONT, State.Refusing)
            .Permit(Trigger.DO, State.Do)
            .Permit(Trigger.DONT, State.Dont)
            .Permit(Trigger.SB, State.SubNegotiation)
            .OnEntry(_ => _logger.LogTrace("Connection: {ConnectionState}", "Starting Negotiation"));

        tsm.Configure(State.StartNegotiation)
            .Permit(Trigger.NOP, State.DoNothing);

        tsm.Configure(State.DoNothing)
            .SubstateOf(State.Accepting)
            .OnEntry(() => _logger.LogTrace("Connection: {ConnectionState}", "NOP call. Do nothing."));

        // As a general documentation, negotiation means a Do followed by a Will, or a Will followed by a Do.
        // Do is followed by Refusing or Will followed by Don't indicate negative negotiation.
        tsm.Configure(State.Willing);
        tsm.Configure(State.Refusing);
        tsm.Configure(State.Do);
        tsm.Configure(State.Dont);

        tsm.Configure(State.ReadingCharacters)
            .OnEntryFromAsync(Trigger.IAC, async _ =>
            {
                _logger.LogDebug("Connection: {ConnectionState}", "Escaped IAC - writing byte 255 to buffer");
                // Escaped IAC (255,255) - write the actual IAC byte to buffer
                await WriteToBufferAndAdvanceAsync(OneOf<byte, Trigger>.FromT0((byte)255));
            });

        tsm.Configure(State.SubNegotiation)
            .OnEntryFrom(Trigger.IAC, _ => _logger.LogDebug("Connection: {ConnectionState}", "SubNegotiation request"));

        tsm.Configure(State.EndSubNegotiation)
            .Permit(Trigger.SE, State.Accepting);

        return tsm;
    }

    /// <summary>
    /// Write the character into a buffer.
    /// </summary>
    /// <param name="b">A useful byte for the Client/Server</param>
    private async ValueTask WriteToBufferAndAdvanceAsync(OneOf<byte, Trigger> b)
    {
        if (b.AsT0 == (byte)Trigger.CARRIAGERETURN) return;
        _logger.LogTrace("Debug: Writing into buffer: {Byte}", b.AsT0);
        _buffer[_bufferPosition] = b.AsT0;
        _bufferPosition++;
        await (CallbackOnByteAsync?.Invoke(b.AsT0, CurrentEncoding) ?? default(ValueTask));
    }

    /// <summary>
    /// Write it to output - this should become an Event.
    /// </summary>
    private async ValueTask WriteToOutput()
    {
        if (_bufferPosition == 0)
        {
            return;
        }

        // Create array for callback - always allocate exact size needed
        var cp = _buffer.AsSpan()[.._bufferPosition].ToArray();
        _bufferPosition = 0;

        if (CallbackOnSubmitAsync is not null)
        {
            await CallbackOnSubmitAsync(cp, CurrentEncoding, this);
        }
    }

    /// <summary>
    /// Validates the object is ready to process.
    /// </summary>
    private TelnetInterpreter Validate()
    {
        if (CallbackOnSubmitAsync == null && CallbackOnByteAsync == null)
        {
            throw new ApplicationException(
                $"Writeback Functions ({CallbackOnSubmitAsync}, {CallbackOnByteAsync}) are null or have not been registered.");
        }

        if (CallbackNegotiationAsync == null)
        {
            throw new ApplicationException($"{CallbackNegotiationAsync} is null and has not been registered.");
        }

        // Also checked by TelnetInterpreterBuilder.WithKeepAlive; repeated here because
        // KeepAliveInterval is a public init property that can be assigned without the builder.
        if (KeepAliveInterval is { } keepAliveInterval)
        {
            ValidateKeepAliveInterval(keepAliveInterval, nameof(KeepAliveInterval));
        }

        return this;
    }

    internal void RegisterInitialWilling(Func<ValueTask> fun)
    {
        _initialCall.Add(fun);
    }

    /// <summary>
    /// Installs (or, with null, removes) the decoder every inbound byte passes through before the
    /// telnet state machine sees it. Any transform already installed is disposed.
    /// </summary>
    /// <remarks>
    /// It takes effect from the next byte read off the channel. A plugin installing one from a
    /// state machine handler is running on the byte-processing loop itself, so "the next byte" is
    /// the next byte after the one being processed — which is what a protocol that starts encoding
    /// immediately after a marker needs.
    /// </remarks>
    /// <param name="transform">The transform to install, or null to go back to raw telnet.</param>
    internal void SetInboundByteTransform(IInboundByteTransform? transform)
        => Interlocked.Exchange(ref _inboundTransform, transform)?.Dispose();

    /// <summary>
    /// Installs (or, with null, removes) the encoder every outbound write passes through on its way
    /// to the network. Any transform already installed is disposed.
    /// </summary>
    /// <param name="transform">The transform to install, or null to go back to raw telnet.</param>
    internal void SetOutboundByteTransform(IOutboundByteTransform? transform)
        => Interlocked.Exchange(ref _outboundTransform, transform)?.Dispose();

    /// <summary>
    /// Writes data to the output stream in a thread-safe manner using an internal write lock.
    /// All outgoing telnet data (negotiation, user data, prompts) should go through this method
    /// to prevent concurrent write conflicts on the dual-channel telnet pipe.
    /// </summary>
    /// <param name="data">The bytes to send.</param>
    /// <param name="cancellationToken">Token to cancel the wait for the write lock.</param>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    public async ValueTask WriteToNetworkAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (CallbackNegotiationAsync is null) return;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Encoding happens under the write lock so a stateful encoder (a zlib deflater, say)
            // sees writes in the same order the network will.
            var transform = Volatile.Read(ref _outboundTransform);
            await CallbackNegotiationAsync(transform is null ? data : transform.Encode(data));
        }
        finally
        {
            _writeLock.Release();
            // Restart the keep-alive idle window. This is an interlocked store of a timestamp,
            // not a call into the keep-alive loop, so it cannot re-enter or deadlock the lock we
            // just released. See TelnetKeepAliveInterpreter.
            MarkNetworkWrite();
        }
    }

    /// <summary>
    /// Interprets the next byte in an asynchronous way.
    /// Non-blocking - submits byte to processing channel and returns immediately.
    /// </summary>
    /// <param name="bt">An integer representation of a byte.</param>
    /// <returns>ValueTask</returns>
    public async ValueTask InterpretAsync(byte bt)
    {
        await _byteChannel.Writer.WriteAsync(bt);
    }

    /// <summary>
    /// Interprets the next byte in an asynchronous way.
    /// Non-blocking - submits bytes to processing channel and returns immediately.
    /// </summary>
    /// <param name="byteArray">An integer representation of a byte.</param>
    /// <returns>ValueTask</returns>
    public async ValueTask InterpretByteArrayAsync(ReadOnlyMemory<byte> byteArray)
    {
        // Index into ReadOnlyMemory<byte> directly to avoid a .ToArray() allocation.
        // ReadOnlyMemory<byte> (unlike ReadOnlySpan<byte>) is not a ref struct,
        // so it is safe to hold across await boundaries.
        for (int i = 0; i < byteArray.Length; i++)
        {
            await _byteChannel.Writer.WriteAsync(byteArray.Span[i]);
        }
    }

    /// <summary>
    /// Waits for all pending bytes in the channel to be processed.
    /// Useful for tests and ensuring all data is processed before continuing.
    /// </summary>
    /// <param name="maxWaitMs">Maximum time to wait for channel to drain (default: 1000ms)</param>
    /// <param name="additionalDelayMs">Additional delay after channel drains to allow callbacks to complete (default: 100ms)</param>
    public async ValueTask WaitForProcessingAsync(int maxWaitMs = 1000, int additionalDelayMs = 100)
    {
        var startTime = DateTime.UtcNow;
        while (_byteChannel.Reader.Count > 0 && (DateTime.UtcNow - startTime).TotalMilliseconds < maxWaitMs)
        {
            await Task.Delay(10);
        }
        
        // Give additional time for state machine transitions and callbacks to complete
        if (additionalDelayMs > 0)
        {
            await Task.Delay(additionalDelayMs);
        }
    }

    /// <summary>
    /// Background task that processes bytes from the channel.
    /// </summary>
    private async Task ProcessBytesAsync(CancellationToken cancellationToken)
    {
        try
        {
            int byteCount = 0;
            await foreach (var raw in _byteChannel.Reader.ReadAllAsync(cancellationToken))
            {
                var transform = _inboundTransform;
                if (transform is null)
                {
                    await FireByteAsync(raw, ++byteCount);
                    continue;
                }

                // A transform is installed (MCCP, in practice), so this wire byte is not a telnet
                // byte. Only what comes back out of it is, and one wire byte can decode to none or
                // to many. A decode failure is terminal for the stream and has its own path inside
                // the transform, so it deliberately is not caught the way a bad telnet byte is.
                var decoded = transform.Decode(raw);
                for (var i = 0; i < decoded.Length; i++)
                {
                    await FireByteAsync(decoded.Span[i], ++byteCount);
                }
            }
            _logger.LogDebug("Byte processing completed. Total bytes processed: {ByteCount}", byteCount);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
            _logger.LogDebug("Byte processing cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in byte processing pipeline at byte position");
        }
    }

    /// <summary>
    /// Fires a single telnet byte into the state machine.
    /// </summary>
    /// <param name="bt">The telnet byte, after any inbound transform has decoded it.</param>
    /// <param name="byteCount">Its position in the decoded stream, for tracing.</param>
    private async ValueTask FireByteAsync(byte bt, int byteCount)
    {
        if (!_isDefinedDictionary.TryGetValue(bt, out var triggerOrByte))
        {
            // Use generated IsDefined method instead of reflection
            triggerOrByte = TriggerExtensions.IsDefined((short)bt)
                ? (Trigger)bt
                : Trigger.ReadNextCharacter;
            _isDefinedDictionary.Add(bt, triggerOrByte);
        }

        _logger.LogTrace("Processing byte #{ByteNum}: {Byte:X2} (trigger: {Trigger}), current state: {State}",
            byteCount, bt, triggerOrByte, TelnetStateMachine.State);
        try
        {
            await TelnetStateMachine.FireAsync(ParameterizedTrigger(triggerOrByte), bt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One malformed byte must not end the connection. Before this, any throw out of
            // the state machine escaped the whole loop and every subsequent byte on the
            // socket was silently discarded for the life of the connection.
            _logger.LogError(ex,
                "Dropping byte #{ByteNum} ({Byte:X2}, trigger {Trigger}) that could not be processed in state {State}. Connection continues.",
                byteCount, bt, triggerOrByte, TelnetStateMachine.State);
        }
        _logger.LogTrace("After byte #{ByteNum}, new state: {State}, buffer position: {BufferPos}",
            byteCount, TelnetStateMachine.State, _bufferPosition);
    }

    /// <summary>
    /// Graceful shutdown of the interpreter.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _byteChannel.Writer.Complete();  // Signal no more data
        
#if NET6_0_OR_GREATER
        await _processingCts.CancelAsync();  // Cancel processing
#else
        _processingCts.Cancel();
#endif

        // Let the keep-alive loop observe the cancellation and finish any write it already started,
        // BEFORE the write lock and the token source it uses are disposed.
        await StopKeepAliveAsync();

        if (_processingTask != null)
        {
            try
            {
                await _processingTask;  // Wait for processing to finish
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
        
        // Plugins own unmanaged-ish state (MCCP's zlib streams, for one) and were never disposed.
        if (PluginManager is not null)
        {
            await PluginManager.DisposeAllAsync();
        }

        SetInboundByteTransform(null);
        SetOutboundByteTransform(null);

        _processingCts.Dispose();
        _writeLock.Dispose();
    }
}