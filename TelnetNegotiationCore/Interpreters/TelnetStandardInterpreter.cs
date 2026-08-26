using System;
using System.Buffers;
using System.Collections.Concurrent;
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
/// <remarks>
/// The <see cref="IAsyncDisposable"/> here is load-bearing, however redundant it looks next to a
/// <see cref="DisposeAsync"/> that was already written and already correct. <c>await using</c> finds
/// that method by pattern, so the disposal ran for anyone who wrote the <c>await using</c> by hand
/// and for nobody else — a DI container, a collection of disposables, or any <c>is IAsyncDisposable</c>
/// test looked at the type, saw nothing, and let the interpreter and every plugin it owns leak.
/// </remarks>
public partial class TelnetInterpreter : IAsyncDisposable
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
    /// The longest line of ordinary (non-negotiation) input this connection will assemble, in bytes.
    /// Defaults to 5 MiB.
    /// </summary>
    /// <remarks>
    /// A peer decides when to send a newline, so this is the one accumulator an untrusted peer can
    /// grow simply by never terminating a line. A line that passes this is <em>dropped</em>, never
    /// truncated — a line cut at an arbitrary point is a different line, not a shorter one — and the
    /// connection carries on with the next.
    /// </remarks>
    public int MaxBufferSize { get; init; } = DefaultMaxBufferSize;

    /// <summary>
    /// The default value of <see cref="MaxBufferSize"/>: 5 MiB.
    /// </summary>
    public const int DefaultMaxBufferSize = 5242880;

    /// <summary>
    /// Local buffer for accumulating line data. Allocated on the first byte of input rather than in
    /// the constructor, so that <see cref="MaxBufferSize"/> (an init property, assigned after the
    /// constructor has run) is honoured and a negotiation-only connection allocates nothing.
    /// </summary>
    /// <remarks>
    /// It starts at <see cref="InitialBufferCapacity"/> and doubles towards <see cref="MaxBufferSize"/>
    /// as a line needs it, rather than allocating the ceiling up front: the ceiling is a bound on what
    /// a hostile peer may do, not a size any real line reaches, and a server holding thousands of idle
    /// connections should not pay 5 MiB apiece to hold "look".
    /// </remarks>
    private byte[]? _buffer;

    /// <summary>
    /// Capacity of <see cref="_buffer"/>, held separately so the per-byte path is one integer
    /// comparison that covers "not allocated yet" and "full" together.
    /// </summary>
    private int _bufferCapacity;

    /// <summary>
    /// Starting capacity of the line buffer. Comfortably larger than any line a MUD sends.
    /// </summary>
    private const int InitialBufferCapacity = 1024;

    /// <summary>
    /// Above this, the line buffer is released rather than retained once its line is delivered, so
    /// that one unusually large line does not hold its allocation for the life of the connection.
    /// Matches <see cref="Helpers.SubnegotiationBuffer"/>'s policy.
    /// </summary>
    private const int RetainedBufferCapacity = 64 * 1024;

    /// <summary>
    /// Buffer position where we are writing.
    /// </summary>
    private int _bufferPosition;

    /// <summary>
    /// Whether the line currently being assembled has already passed <see cref="MaxBufferSize"/>,
    /// and so is going to be discarded whole when its newline finally arrives.
    /// </summary>
    private bool _bufferOverflowed;

    /// <summary>
    /// <see cref="CurrentEncoding"/> as it stood when the line currently being assembled began, or
    /// null when no line is open. This, not the encoding at newline time, is what the line is
    /// delivered with.
    /// </summary>
    /// <remarks>
    /// CHARSET can complete part-way through a line, because RFC 2066 page 9 only asks a peer to
    /// hold data back while a subnegotiation runs — "While a CHARSET subnegotiation is in progress,
    /// data SHOULD be queued" — and a SHOULD is not a MUST. When a peer sends anyway, the bytes
    /// already buffered were written in the encoding that was in force when they were sent, and no
    /// later negotiation can change what the peer already put on the wire.
    ///
    /// A line that straddles the switch is genuinely ambiguous: bytes the peer sent after it moved
    /// really are in the new encoding, and one label cannot describe both halves. That ambiguity is
    /// the sender's to avoid by queueing, and is why the RFC asks. Binding at line start is the half
    /// that is right for the case that actually happens — a banner written whole in the old encoding
    /// whose newline arrives after negotiation settles — and it never invents an encoding the peer
    /// had not yet agreed to.
    /// </remarks>
    private Encoding? _lineEncoding;

    /// <summary>
    /// Observers that see each assembled line before <see cref="CallbackOnSubmitAsync"/> does, and
    /// may consume it. See <see cref="RegisterInputLineObserver"/>.
    /// </summary>
    private readonly List<Func<byte[], Encoding, ValueTask<bool>>> _inputLineObservers = [];

    /// <summary>
    /// Written to <see cref="_byteChannel"/> in place of a wire byte, to move
    /// <see cref="Protocols.PacketPatchProtocol"/>'s hold-time decision off its timer thread and onto
    /// the byte-processing loop -- the one thread that already owns the line buffer, so it can act on
    /// the sentinel directly instead of a second thread reaching into that state. Negative and out of
    /// the 0-255 range a genuine wire byte occupies, so it can never collide with one.
    /// </summary>
    private const short InferredPromptSentinel = -1;

    /// <summary>
    /// Channel for byte processing pipeline with backpressure. Element type is <c>short</c>, not
    /// <c>byte</c>: every wire byte still fits, and the spare negative range is what carries
    /// <see cref="InferredPromptSentinel"/>.
    /// </summary>
    private readonly Channel<short> _byteChannel;

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
    /// Non-zero once a caller has claimed the shutdown in <see cref="DisposeAsync"/>.
    /// </summary>
    /// <remarks>
    /// An <see cref="Interlocked"/>-guarded <see cref="int"/> rather than a plain <c>bool</c>,
    /// because the two callers most likely to race here run on different threads: the owner of the
    /// connection, and the read loop that has just noticed the connection went away and is tearing
    /// down what it was reading into. A check-then-set on a <c>bool</c> would let both of them
    /// through and both would then try to complete the same channel.
    /// </remarks>
    private int _disposed;

    /// <summary>
    /// Cancelled when the interpreter is disposed. Protocols that run their own background timers
    /// link to this so that nothing outlives the connection it belongs to.
    /// </summary>
    /// <remarks>
    /// Reads as already-cancelled once disposal has run to completion, rather than throwing: a
    /// disposed interpreter <em>is</em> a cancelled connection, and a caller asking "is this
    /// connection still alive" should get that answer rather than an exception.
    /// </remarks>
    internal CancellationToken ProcessingToken
    {
        get
        {
            try
            {
                return _processingCts.Token;
            }
            catch (ObjectDisposedException)
            {
                return new CancellationToken(canceled: true);
            }
        }
    }

    /// <summary>
    /// Background processing task.
    /// </summary>
    private Task? _processingTask;

    /// <summary>
    /// Optional decoder sitting between the network and the state machine. Only the byte-processing
    /// loop ever calls it, so the reference just needs publishing, not locking.
    /// </summary>
    private IInboundByteTransform? _inboundTransform;

    /// <summary>
    /// Decoders that have been swapped out and are waiting to be disposed by the byte-processing
    /// loop.
    /// </summary>
    /// <remarks>
    /// Whoever swaps one out cannot dispose it: the loop may be inside
    /// <see cref="IInboundByteTransform.DecodeAsync"/>, and a decoder is typically a zlib inflater,
    /// where disposing under a thread that is reading from it is a native use-after-free rather
    /// than a tidy <see cref="ObjectDisposedException"/>. The loop disposes them itself, between
    /// bytes, which is the one place it is provably not inside one.
    /// </remarks>
    private readonly ConcurrentQueue<IInboundByteTransform> _retiredInboundTransforms = new();

    /// <summary>
    /// Set when something is waiting in <see cref="_retiredInboundTransforms"/>. Read once per
    /// received byte, so it is a plain volatile bool rather than a queue probe: retirement happens
    /// at most a couple of times in a connection's life, and the per-byte path should not pay for
    /// it.
    /// </summary>
    private volatile bool _hasRetiredInboundTransforms;

    /// <summary>
    /// Optional encoder sitting between the library and the network. Both its use and its
    /// replacement happen under <see cref="_writeLock"/>, which is what keeps a write from being
    /// inside an encoder that is being disposed.
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
    /// Internal use - writes go straight out, serialized by <see cref="_writeLock"/> and never queued.
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

        // Create bounded channel with backpressure (max 10,000 bytes buffered)
        _byteChannel = Channel.CreateBounded<short>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.Wait,  // Backpressure: block producer if full
            SingleReader = true,   // Optimization: only one consumer
            SingleWriter = false   // Multiple threads may write
        });

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

        // IAC GA (Go-Ahead) is the original 1983 prompt marker (RFC 854) and predates both EOR and
        // SUPPRESS-GO-AHEAD: a server is free to send it regardless of which options either side
        // negotiated, and several real ones do on every prompt. Before this, GA had no permitted
        // transition from StartNegotiation at all, so it always reached OnUnhandledTriggerAsync: a
        // logged Critical and a recovery through Trigger.Error on every single occurrence. Servers
        // that pair a trailing GA with another IAC sequence right behind it — achaea.com does, at
        // the exact moment it starts MCCP2 — could lose that sequence to the recovery instead of
        // parsing it, which is how a still-compressed byte stream ends up read as plain telnet.
        //
        // The state itself does nothing. Whether this particular GA is a prompt boundary or a
        // leftover to drop is the negotiated state, which is the plugins' knowledge and not the
        // interpreter's: SuppressGoAheadProtocol adds its own entry action here, and drops the GA
        // only where RFC 858 says to — once SUPPRESS-GO-AHEAD is in effect. EOR is not part of that
        // condition; RFC 885 is a different marker and says nothing about Go-Ahead.
        tsm.Configure(State.StartNegotiation)
            .Permit(Trigger.GA, State.GoAhead);

        tsm.Configure(State.GoAhead)
            .SubstateOf(State.Accepting)
            .OnEntry(() => _logger.LogTrace("Connection: {ConnectionState}", "GA (Go-Ahead) received."));

        // As a general documentation, negotiation means a Do followed by a Will, or a Will followed by a Do.
        // Do is followed by Refusing or Will followed by Don't indicate negative negotiation.
        //
        // Each of these four states expects exactly one more byte: the option number the WILL,
        // WONT, DO or DONT was about. A peer that sends a fresh IAC there instead — achaea.com does,
        // immediately after a bare IAC WONT with no option byte at all, right at the point it starts
        // MCCP2 — is not completing that negotiation. Without a permitted transition, that IAC used
        // to be a byte with no meaning the state machine could assign it: an unhandled trigger,
        // recovered through the generic Trigger.Error path into State.Accepting, which does not
        // know a subnegotiation was about to start. The IAC SB COMPRESS2 IAC SE right behind it then
        // read as four bytes of plain text and the zlib that followed read as more of the same,
        // never inflated. Permitting IAC here abandons the incomplete negotiation and starts parsing
        // the new command from where it actually begins, the same way IAC IAC is already handled as
        // an escaped literal from StartNegotiation rather than through the same generic recovery.
        tsm.Configure(State.Willing)
            .Permit(Trigger.IAC, State.StartNegotiation);
        tsm.Configure(State.Refusing)
            .Permit(Trigger.IAC, State.StartNegotiation);
        tsm.Configure(State.Do)
            .Permit(Trigger.IAC, State.StartNegotiation);
        tsm.Configure(State.Dont)
            .Permit(Trigger.IAC, State.StartNegotiation);

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
    /// Registers an observer that sees each assembled line of ordinary input before
    /// <see cref="CallbackOnSubmitAsync"/> does, and returns true to consume it.
    /// </summary>
    /// <remarks>
    /// This exists for protocols that are carried as text rather than as negotiation — the plaintext
    /// MSSP exchange is the only one today — where the protocol's own lines must not be handed to the
    /// host application as if a user had typed them. Observers run in registration order and the
    /// first one to return true stops the line there.
    /// </remarks>
    /// <param name="observer">Receives the line's bytes and the connection's encoding; returns true
    /// if the line belongs to it and must not be delivered.</param>
    internal void RegisterInputLineObserver(Func<byte[], Encoding, ValueTask<bool>> observer)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));

        _inputLineObservers.Add(observer);
    }

    /// <summary>
    /// Write the character into a buffer.
    /// </summary>
    /// <param name="b">A useful byte for the Client/Server</param>
    private async ValueTask WriteToBufferAndAdvanceAsync(OneOf<byte, Trigger> b)
    {
        if (b.AsT0 == (byte)Trigger.CARRIAGERETURN) return;
        _logger.LogTrace("Debug: Writing into buffer: {Byte}", b.AsT0);

        // The first byte of a line pins the encoding the whole line is delivered with. See _lineEncoding.
        _lineEncoding ??= CurrentEncoding;

        if (_bufferPosition >= MaxBufferSize)
        {
            // Past the ceiling: stop storing, but keep consuming the line so that the connection
            // stays in sync and the next newline still ends it. Writing anyway indexed past the end
            // of the array, which threw out of the state machine and killed byte processing for the
            // rest of the connection.
            _bufferOverflowed = true;
        }
        else
        {
            // One comparison on the hot path: capacity starts at zero, so this covers the first byte
            // of the connection and a full buffer with the same test, and the growth itself is out of
            // line because it happens at most log2(MaxBufferSize / 1 KiB) times per line.
            if (_bufferPosition == _bufferCapacity)
            {
                GrowLineBuffer();
            }

            _buffer![_bufferPosition] = b.AsT0;
            _bufferPosition++;
        }

        await (CallbackOnByteAsync?.Invoke(b.AsT0, CurrentEncoding) ?? default(ValueTask));
    }

    /// <summary>
    /// Doubles the line buffer, never past <see cref="MaxBufferSize"/>.
    /// </summary>
    /// <remarks>
    /// Only ever called when the buffer is exactly full and the ceiling has not been reached, so the
    /// new capacity is always strictly larger than the old one and the ceiling still decides where
    /// storing stops. Nothing here changes what an over-long line does: that is decided before this
    /// is reached.
    /// </remarks>
    private void GrowLineBuffer()
    {
        var capacity = _bufferCapacity == 0
            ? Math.Min(InitialBufferCapacity, MaxBufferSize)
            : (int)Math.Min((long)_bufferCapacity * 2, MaxBufferSize);

        Array.Resize(ref _buffer, capacity);
        _bufferCapacity = capacity;
    }

    /// <summary>
    /// Releases the line buffer if one large line grew it past <see cref="RetainedBufferCapacity"/>.
    /// </summary>
    private void ReleaseLineBufferIfLarge()
    {
        if (_bufferCapacity <= RetainedBufferCapacity) return;

        _buffer = null;
        _bufferCapacity = 0;
    }

    /// <summary>
    /// Write it to output - this should become an Event.
    /// </summary>
    private async ValueTask WriteToOutput()
    {
        if (_bufferOverflowed)
        {
            _logger.LogError(
                "A line of input exceeded the maximum buffer size of {MaxBufferSize} bytes and was dropped. Raise TelnetInterpreter.MaxBufferSize if this is legitimate traffic.",
                MaxBufferSize);

            _bufferOverflowed = false;
            _bufferPosition = 0;
            _lineEncoding = null;
            ReleaseLineBufferIfLarge();
            return;
        }

        // A NEWLINE reaches here only from State.Act, which only a NEWLINE trigger enters — so every
        // call is a genuine line submission, including one with nothing buffered since the last: the
        // second CRLF of a "paragraph break" double newline is exactly that; MUD/MUSH output leans on
        // it for blank lines. Skipping the callback here silently drops those lines before any caller
        // ever sees them. _buffer stays null until something is written to it, so the empty case can't
        // reuse it.
        var cp = _bufferPosition == 0 ? [] : _buffer!.AsSpan()[.._bufferPosition].ToArray();
        _bufferPosition = 0;

        // An empty line holds no bytes to have been written in any particular encoding, so it takes
        // the current one; anything else is delivered in the encoding it arrived in.
        var lineEncoding = _lineEncoding ?? CurrentEncoding;
        _lineEncoding = null;
        ReleaseLineBufferIfLarge();

        foreach (var observer in _inputLineObservers)
        {
            if (await observer(cp, lineEncoding))
            {
                return;
            }
        }

        if (CallbackOnSubmitAsync is not null)
        {
            await CallbackOnSubmitAsync(cp, lineEncoding, this);
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

        // Also checked by TelnetInterpreterBuilder.WithMaxBufferSize; repeated here because
        // MaxBufferSize is a public init property that can be assigned without the builder.
        if (MaxBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBufferSize), MaxBufferSize,
                "Maximum buffer size must be positive.");
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
    /// telnet state machine sees it. Any transform already installed is retired, and disposed by
    /// the byte-processing loop once it is out of it.
    /// </summary>
    /// <remarks>
    /// It takes effect from the next byte read off the channel. A plugin installing one from a
    /// state machine handler is running on the byte-processing loop itself, so "the next byte" is
    /// the next byte after the one being processed — which is what a protocol that starts decoding
    /// immediately after a marker needs.
    /// <para>
    /// Safe to call from any thread, which matters because
    /// <see cref="Plugins.ProtocolPluginManager.DisablePluginAsync{T}"/> is public and reaches here
    /// from wherever a consumer calls it. The swap itself is atomic and disposal is deferred, so a
    /// decoder is never disposed while the loop is inside it. What is <i>not</i> supported is
    /// installing the same instance twice with a retirement in between — a stateful decoder cannot
    /// be reused that way regardless.
    /// </para>
    /// </remarks>
    /// <param name="transform">The transform to install, or null to go back to raw telnet.</param>
    internal void SetInboundByteTransform(IInboundByteTransform? transform)
    {
        var previous = Interlocked.Exchange(ref _inboundTransform, transform);
        if (previous is null)
        {
            return;
        }

        _retiredInboundTransforms.Enqueue(previous);
        _hasRetiredInboundTransforms = true;
    }

    /// <summary>
    /// Disposes decoders that have been swapped out. Only ever called where the caller is provably
    /// not inside one: between bytes on the processing loop, or after it has stopped.
    /// </summary>
    private void DisposeRetiredInboundTransforms()
    {
        // Cleared before draining, so a retirement that races this drain leaves the flag set and is
        // picked up next time round rather than being stranded.
        _hasRetiredInboundTransforms = false;

        while (_retiredInboundTransforms.TryDequeue(out var retired))
        {
            retired.Dispose();
        }
    }

    /// <summary>
    /// Installs (or, with null, removes) the encoder every outbound write passes through on its way
    /// to the network, optionally writing one last thing in the clear first. Any transform already
    /// installed is disposed once no write is inside it.
    /// </summary>
    /// <remarks>
    /// Both halves matter and both need <see cref="_writeLock"/>:
    /// <list type="bullet">
    /// <item>A write captures the encoder and uses it under that lock, so swapping without it can
    /// dispose an encoder a write is currently inside.</item>
    /// <item><paramref name="sendFirst"/> exists because a protocol that announces its switch-over
    /// with a marker needs the marker and the switch to be one step. Sending the marker and then
    /// installing as two operations lets another thread's write land in between, going out in the
    /// clear after the peer has already started decoding it as compressed.</item>
    /// </list>
    /// Callers are state machine handlers on the byte-processing loop, which never holds the write
    /// lock across a call back into the state machine, so this cannot deadlock against them.
    /// </remarks>
    /// <param name="transform">The transform to install, or null to go back to raw telnet.</param>
    /// <param name="sendFirst">A final write to make in the clear, before the transform takes over.</param>
    /// <param name="cancellationToken">Token to cancel the wait for the write lock.</param>
    internal async ValueTask SetOutboundByteTransformAsync(
        IOutboundByteTransform? transform,
        ReadOnlyMemory<byte> sendFirst = default,
        CancellationToken cancellationToken = default)
    {
        IOutboundByteTransform? previous;
        var wrote = false;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!sendFirst.IsEmpty && CallbackNegotiationAsync is not null)
            {
                var current = _outboundTransform;
                await CallbackNegotiationAsync(current is null ? sendFirst : current.Encode(sendFirst));
                wrote = true;
            }

            previous = _outboundTransform;
            _outboundTransform = transform;
        }
        finally
        {
            _writeLock.Release();

            if (wrote)
            {
                MarkNetworkWrite();
            }
        }

        // Safe now: no write can reach it again, and any write that was inside it has left.
        previous?.Dispose();
    }

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
            // sees writes in the same order the network will, and so it cannot be swapped out and
            // disposed while this write is inside it.
            var transform = _outboundTransform;
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
                if (raw == InferredPromptSentinel)
                {
                    // Not a wire byte. Protocols.PacketPatchProtocol's timer enqueues this instead of
                    // touching the line buffer itself, so the actual check-and-take runs here, on the
                    // one thread that already owns that buffer -- see TakePartialLineAsPrompt and
                    // TryEnqueueInferredPrompt.
                    //
                    // The handler is read first, and gates the rest: it is null whenever the plugin
                    // would not want this sentinel honoured at all -- disabled at runtime, disposed,
                    // or already latched off by a marked prompt (OnByteStreamIdleAsync unregisters it
                    // in that last case) -- and checking it before touching anything else means a
                    // stale sentinel from before any of those leaves the line buffer alone rather than
                    // draining it for a report nobody will receive. HasSeenMarkedPrompt is read here
                    // too, not left to TakePartialLineAsPrompt, because it must gate the call and not
                    // just its result: a sentinel that is stale because a genuine IAC GA/EOR won the
                    // race and already drained the buffer must not touch the buffer at all, only find
                    // nothing there.
                    var onInferredPrompt = _onInferredPrompt;
                    if (onInferredPrompt is not null && !HasSeenMarkedPrompt && TakePartialLineAsPrompt(marked: false))
                    {
                        try
                        {
                            await onInferredPrompt();
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Mirrors FireByteAsync's own catch: a consumer's callback throwing
                            // must not take down byte processing for the rest of the connection.
                            _logger.LogError(ex,
                                "The inferred-prompt callback threw. Connection continues.");
                        }
                    }

                    continue;
                }

                var bt = (byte)raw;

                // Between bytes: the one point where this loop is provably not inside a decoder.
                if (_hasRetiredInboundTransforms)
                {
                    DisposeRetiredInboundTransforms();
                }

                var transform = _inboundTransform;
                if (transform is null)
                {
                    await FireByteAsync(bt, ++byteCount);
                    await NotifyIfIdleAsync();
                    continue;
                }

                // A transform is installed (MCCP, in practice), so this wire byte is not a telnet
                // byte. Only what comes back out of it is, and one wire byte can decode to none or
                // to many. A decode failure is terminal for the stream and has its own path inside
                // the transform, so it deliberately is not caught the way a bad telnet byte is.
                //
                // Feeding ONE byte per call is load-bearing, not incidental: it is what bounds a
                // decoder's output buffer. DEFLATE expands at most 1032:1 from a single input byte,
                // so an inflater's buffer plateaus at 2 KiB no matter what the peer sends — a 16 MiB
                // zip bomb arrives as 1032 bytes per call, 16,315 times. Batch the feed and that
                // ceiling becomes 1032 x batch size, which a hostile peer chooses. See
                // MCCPCompressedStreamTests.AHighRatioPayloadDoesNotGrowTheDecodersBuffer.
                var decoded = await transform.DecodeAsync(bt);
                for (var i = 0; i < decoded.Length; i++)
                {
                    await FireByteAsync(decoded.Span[i], ++byteCount);
                }

                // After the whole decoded batch, not inside the loop above: one wire byte can decode
                // to many, and notifying between them would arm the idle timer while bytes from the
                // very same read are still waiting to be fired.
                await NotifyIfIdleAsync();
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
    /// Tells a registered idle handler that the byte stream has gone quiet, when it has.
    /// </summary>
    /// <remarks>
    /// "Quiet" is the channel being empty at the instant this runs, which is a looser thing than "a
    /// read just ended": <see cref="InterpretByteArrayAsync"/> writes one byte per <c>await</c>, so a
    /// consumer draining faster than the writes arrive can observe <c>Count == 0</c> between two bytes
    /// of what the caller thinks of as a single read, not only after the last one. That produces false
    /// positives, and they are harmless: the only registered handler
    /// (<see cref="Protocols.PacketPatchProtocol.OnByteStreamIdleAsync"/>) just restarts its hold-time
    /// clock from whatever byte was last seen, and a hold time is measured from the last byte either
    /// way -- re-arming a little early changes nothing once the rest of the read does arrive.
    /// </remarks>
    private async ValueTask NotifyIfIdleAsync()
    {
        var handler = _onByteStreamIdle;
        if (handler is not null && _byteChannel.Reader.Count == 0)
        {
            await handler();
        }
    }

    /// <summary>
    /// Enqueues <see cref="InferredPromptSentinel"/> for the byte-processing loop to act on. The only
    /// caller is <see cref="Protocols.PacketPatchProtocol"/>'s timer callback, which must not touch the
    /// line buffer itself -- see <see cref="TakePartialLineAsPrompt"/>'s remarks.
    /// </summary>
    /// <remarks>
    /// A non-blocking <c>TryWrite</c> rather than <c>WriteAsync</c>, deliberately: the timer callback
    /// is synchronous and must return quickly regardless, the channel has ample headroom (10,000 deep)
    /// for one extra signal per held fragment, and if it is ever genuinely full or already completed
    /// the fragment simply goes unreported as a prompt -- no worse an outcome than the connection
    /// having already gone away, which a completed channel means it has.
    /// </remarks>
    /// <returns>False if the sentinel could not be enqueued (the channel is full or closed).</returns>
    internal bool TryEnqueueInferredPrompt() => _byteChannel.Writer.TryWrite(InferredPromptSentinel);

    /// <summary>
    /// Graceful shutdown of the interpreter.
    /// </summary>
    /// <remarks>
    /// Calling this more than once is harmless, as <see cref="IAsyncDisposable"/> requires, and it
    /// does happen: a container that owns the interpreter disposes it whether or not the consumer
    /// already did, and a consumer inside an <c>await using</c> may reasonably close the connection
    /// early by hand. Without the guard the second call did not merely repeat the work, it threw —
    /// <see cref="System.Threading.Channels.ChannelWriter{T}.Complete"/> on an already-completed
    /// channel, and then cancellation on an already-disposed
    /// <see cref="CancellationTokenSource"/>. The first caller performs the whole shutdown; anyone
    /// arriving after it returns having done nothing.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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

        // The processing loop has stopped, so nothing can be inside a decoder any more, and
        // whatever it never got round to retiring is disposed here instead.
        SetInboundByteTransform(null);
        DisposeRetiredInboundTransforms();

        await SetOutboundByteTransformAsync(null);

        _processingCts.Dispose();
        _writeLock.Dispose();
    }
}