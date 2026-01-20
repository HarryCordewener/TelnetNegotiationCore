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
    public Encoding CurrentEncoding { get; internal set; } = Encoding.ASCII;

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
    /// Cancellation token source for graceful shutdown.
    /// </summary>
    private readonly CancellationTokenSource _processingCts = new();

    /// <summary>
    /// Background processing task.
    /// </summary>
    private Task? _processingTask;

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
    public required Func<byte[], ValueTask> CallbackNegotiationAsync { get; init; }

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
        TriggerHelper.ForAllTriggersButIAC(t => tsm.Configure(State.Accepting).Permit(t, State.ReadingCharacters));

        // Standard triggers, which are fine in the Awaiting state and should just be interpreted as a character in this state.
        tsm.Configure(State.ReadingCharacters)
            .SubstateOf(State.Accepting)
            .Permit(Trigger.NEWLINE, State.Act);

        TriggerHelper.ForAllTriggers(t => tsm.Configure(State.ReadingCharacters)
            .OnEntryFromAsync(ParameterizedTrigger(t), async x => await WriteToBufferAndAdvanceAsync(x)));

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
            .OnEntryFrom(Trigger.IAC, _ => _logger.LogDebug("Connection: {ConnectionState}", "Canceling negotiation"));

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
        await (CallbackOnByteAsync?.Invoke(b.AsT0, CurrentEncoding) ?? ValueTask.CompletedTask);
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

        byte[]? rentedBuffer = null;
        try
        {
            // Use stackalloc for very small buffers (<=512 bytes), ArrayPool for larger
            // This reduces heap allocations for common case of small messages
            byte[] cp;
            if (_bufferPosition <= 512)
            {
                // For small buffers, allocate directly - no intermediate stackalloc needed
                // since we must create a byte[] for the callback anyway
                cp = _buffer.AsSpan()[.._bufferPosition].ToArray();
            }
            else
            {
                // Rent from pool for larger buffers to reduce allocations
                rentedBuffer = ArrayPool<byte>.Shared.Rent(_bufferPosition);
                _buffer.AsSpan()[.._bufferPosition].CopyTo(rentedBuffer);
                cp = rentedBuffer[.._bufferPosition];
            }

            _bufferPosition = 0;

            if (CallbackOnSubmitAsync is not null)
            {
                await CallbackOnSubmitAsync(cp, CurrentEncoding, this);
            }
        }
        finally
        {
            // Return rented buffer to pool
            if (rentedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
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

        return this;
    }

    internal void RegisterInitialWilling(Func<ValueTask> fun)
    {
        _initialCall.Add(fun);
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
        // Convert to array first to avoid Span across await boundary
        var bytes = byteArray.ToArray();
        foreach (var b in bytes)
        {
            await _byteChannel.Writer.WriteAsync(b);
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
            await foreach (var bt in _byteChannel.Reader.ReadAllAsync(cancellationToken))
            {
                if (!_isDefinedDictionary.TryGetValue(bt, out var triggerOrByte))
                {
                    // Use generated IsDefined method instead of reflection
                    triggerOrByte = TriggerExtensions.IsDefined((short)bt)
                        ? (Trigger)bt
                        : Trigger.ReadNextCharacter;
                    _isDefinedDictionary.Add(bt, triggerOrByte);
                }

                await TelnetStateMachine.FireAsync(ParameterizedTrigger(triggerOrByte), bt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
            _logger.LogDebug("Byte processing cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in byte processing pipeline");
        }
    }

    /// <summary>
    /// Graceful shutdown of the interpreter.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _byteChannel.Writer.Complete();  // Signal no more data
        
        await _processingCts.CancelAsync();  // Cancel processing
        
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
        
        _processingCts.Dispose();
    }
}