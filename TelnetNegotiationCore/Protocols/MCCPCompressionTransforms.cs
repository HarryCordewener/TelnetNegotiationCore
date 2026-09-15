using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Interpreters;
#if NETSTANDARD2_0
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
#endif

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Inflates an MCCP stream: <b>one</b> zlib stream for the life of the connection, fed one wire
/// byte at a time.
/// </summary>
/// <remarks>
/// <para>
/// MCCP is not a sequence of independently compressed messages — the deflate back-reference window
/// and the Huffman state carry across everything the peer sends after the marker, so a fresh
/// inflater per network read decodes the first read and then fails on the second. Nor do reads line
/// up with anything: a read can end in the middle of a symbol, and one byte can complete a match
/// that expands to hundreds. Hence the incremental interface.
/// </para>
/// <para>
/// One zlib stream, but not necessarily for the whole connection: "the server may terminate
/// compression at any point by sending an orderly stream end (Z_FINISH). Following this, the
/// connection continues as a normal telnet connection." So this watches for the end of the stream
/// and hands the connection back, rather than going on feeding plain telnet to a finished inflater
/// — which produces nothing, for ever, in silence.
/// </para>
/// </remarks>
internal sealed class MCCPInflateTransform : IInboundByteTransform
{
	/// <summary>
	/// The cumulative output-to-input ratio a peer's stream may reach before it is treated as an
	/// attack rather than a well-compressed MUD.
	/// </summary>
	/// <remarks>
	/// MCCP's own claim is a 75-90% reduction, which is 4:1 to 10:1, and English text through
	/// deflate lands near that; zlib's window is 32 KiB, so sustained ratios far above it need input
	/// built to produce them rather than content that happens to repeat. 200:1 leaves an order of
	/// magnitude of headroom over anything a real server sends and still refuses the shape that
	/// matters: 4 KiB of deflate holding 4 MiB of zeros, which is ~1,025:1.
	/// </remarks>
	internal const int DefaultMaxExpansionRatio = 200;

	/// <summary>
	/// Output allowed before the ratio is judged at all, so a short stream is never condemned by a
	/// ratio computed from a handful of bytes — a zlib header alone is 2 bytes in and 0 out.
	/// </summary>
	internal const long ExpansionRatioFloorBytes = 1024 * 1024;

	private readonly ILogger _logger;
	private readonly Func<ValueTask> _onFailureAsync;
	private readonly Func<ValueTask> _onStreamEndAsync;
	private readonly int _maxExpansionRatio;
	private byte[] _output = new byte[1024];
	private long _compressedIn;
	private long _inflatedOut;
	private bool _failed;
	private bool _ended;

#if NETSTANDARD2_0
	private readonly Inflater _inflater = new Inflater();
	private readonly byte[] _input = new byte[1];
#else
	private readonly PendingInput _input = new();
	private readonly ZLibStream _inflater;
#endif

	/// <param name="logger">Where a corrupt stream is reported.</param>
	/// <param name="onFailureAsync">
	/// Called once, if the peer's stream turns out not to be valid zlib. There is no recovering a
	/// deflate stream that has gone wrong, so after this the transform decodes nothing.
	/// </param>
	/// <param name="onStreamEndAsync">
	/// Called once, when the peer ends its stream in an orderly way. The connection is plain telnet
	/// again from that point, so the owner is expected to uninstall this transform; until it does,
	/// bytes pass through untouched.
	/// </param>
	/// <param name="maxExpansionRatio">
	/// The cumulative expansion a peer may reach past <see cref="ExpansionRatioFloorBytes"/>. See
	/// <see cref="DefaultMaxExpansionRatio"/>.
	/// </param>
	public MCCPInflateTransform(
		ILogger logger,
		Func<ValueTask> onFailureAsync,
		Func<ValueTask> onStreamEndAsync,
		int maxExpansionRatio = DefaultMaxExpansionRatio)
	{
		_logger = logger;
		_onFailureAsync = onFailureAsync;
		_onStreamEndAsync = onStreamEndAsync;
		_maxExpansionRatio = maxExpansionRatio;
#if !NETSTANDARD2_0
		_inflater = new ZLibStream(_input, CompressionMode.Decompress);
#endif
	}

	/// <inheritdoc />
	public ValueTask<ReadOnlyMemory<byte>> DecodeAsync(byte raw)
	{
		if (_failed)
		{
			return new ValueTask<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);
		}

		if (_ended)
		{
			// The peer stopped compressing and the owner has not swapped this out yet. These are
			// telnet bytes, not deflate bytes; passing them through is the only thing that is not
			// data loss.
			_output[0] = raw;
			return new ValueTask<ReadOnlyMemory<byte>>(_output.AsMemory(0, 1));
		}

		// `written` resets every call, and the interpreter feeds exactly one byte per call, so the
		// most this can produce is DEFLATE's maximum expansion from one input byte: 1032. _output
		// therefore plateaus at 2 KiB for the life of the connection however hostile the peer is.
		var written = 0;
		_compressedIn++;
		try
		{
#if NETSTANDARD2_0
			_input[0] = raw;
			_inflater.SetInput(_input, 0, 1);
#else
			_input.Push(raw);
#endif

			// Drain until the inflater stops producing, which is how both implementations say
			// "I need more input" — one byte in can be nothing out, or a match that expands to
			// hundreds.
			while (InflateInto(ref written) > 0)
			{
			}
		}
#if !NETSTANDARD2_0
		catch (InvalidDataException) when (_input.RanDryDuringLastRead)
		{
			// A host that switches on System.IO.Compression.UseStrictValidation makes ZLibStream
			// report a base stream that has run out of bytes as truncated data instead of returning
			// 0, which without this would end every MCCP connection in that process with "the
			// peer's stream is not valid zlib" on the first partial read. For MCCP a dry base
			// stream is never truncation — the peer has simply not sent the rest of the symbol yet
			// — so it means here exactly what a 0-byte read means: stop, resume when more arrives.
			// The `when` clause is what keeps this from swallowing genuine corruption, which is
			// always detected on bytes the inflater already has, never on a read that came back
			// empty.
			//
			// Not covered by a unit test: the switch is captured the first time the decompression
			// path is used in a process, so a test can only observe it if it is the first thing to
			// decompress anything, which is not something a shared test host can guarantee.
			// Verified out of process instead, on net8.0 and net10.0.
		}
#endif
		catch (Exception ex)
		{
			_failed = true;
			_logger.LogError(ex, "MCCP: the peer's compressed stream is not valid zlib. Decompression stopped");
			return FailAsync();
		}

		// Counted before the stream-end check, not after: a peer that blows the budget on the very
		// byte that ends its stream still blew it, and leaving that call out of the accounting would
		// make the running total quietly wrong for anyone who reads it later. The exposure either
		// way is one call -- at most DEFLATE's 1,032 bytes from one input byte -- since an ended
		// stream inflates nothing further; the reason to count it is that the invariant should be
		// true, not that the byte matters.
		_inflatedOut += written;

		if (ExpansionIsImplausible())
		{
			_failed = true;
			_logger.LogError(
				"MCCP: the peer's stream claims to expand {InflatedBytes} bytes out of {CompressedBytes} "
				+ "({Ratio}:1, ceiling {Ceiling}:1). Decompression stopped",
				_inflatedOut, _compressedIn, _inflatedOut / Math.Max(_compressedIn, 1), _maxExpansionRatio);
			return FailAsync();
		}

		if (StreamHasEnded())
		{
			_ended = true;
			_logger.LogInformation(
				"MCCP: the peer ended its compressed stream; the connection continues in the clear");

			// Whatever the inflater pulled in but did not consume is past the end of the zlib
			// stream, which makes it the first plain telnet the peer has sent since the marker.
			// Dropping it would lose the peer's own "I have stopped compressing" negotiation.
			written += TakeUnconsumedInput(written);
			return EndAsync(written);
		}

		return new ValueTask<ReadOnlyMemory<byte>>(_output.AsMemory(0, written));
	}

	/// <summary>
	/// Whether the peer has bought more of this process's work than its own bandwidth can justify.
	/// </summary>
	/// <remarks>
	/// The downstream ceilings — the line buffer, the per-subnegotiation limits — bound how much
	/// <i>memory</i> a peer can make this side hold. They do not bound the work of getting there: one
	/// wire byte can become 1,032 through the state machine, so a peer trading its bandwidth for this
	/// side's CPU is limited by neither. Measured, in Release: 4 KiB of deflate holding 4 MiB of
	/// zeros costs about 430 ms of a core, against 1 ms for 4 KiB of plain telnet.
	/// </remarks>
	private bool ExpansionIsImplausible() =>
		_inflatedOut > ExpansionRatioFloorBytes
		&& _inflatedOut > _compressedIn * (long)_maxExpansionRatio;

	private async ValueTask<ReadOnlyMemory<byte>> FailAsync()
	{
		await _onFailureAsync();
		return ReadOnlyMemory<byte>.Empty;
	}

	private async ValueTask<ReadOnlyMemory<byte>> EndAsync(int written)
	{
		await _onStreamEndAsync();
		return _output.AsMemory(0, written);
	}

	/// <summary>
	/// Whether the peer's zlib stream is over. Both implementations say this the same way: the
	/// inflater stops asking for input, so input the transform has already handed it stays
	/// unconsumed.
	/// </summary>
#if NETSTANDARD2_0
	private bool StreamHasEnded() => _inflater.IsFinished;
#else
	private bool StreamHasEnded() => _input.HasUnconsumedInput;
#endif

	/// <summary>
	/// Moves the bytes the inflater declined to consume into the tail of <see cref="_output"/>, and
	/// returns how many there were.
	/// </summary>
	/// <param name="written">How much of <see cref="_output"/> is already in use.</param>
	private int TakeUnconsumedInput(int written)
	{
#if NETSTANDARD2_0
		// The interpreter feeds exactly one byte per call, so the one still in _input is the only
		// byte SharpZipLib's inflater can be holding back.
		if (_inflater.RemainingInput == 0)
		{
			return 0;
		}

		if (written == _output.Length)
		{
			Array.Resize(ref _output, _output.Length * 2);
		}

		_output[written] = _input[0];
		return 1;
#else
		while (_output.Length - written < _input.UnconsumedInputLength)
		{
			Array.Resize(ref _output, _output.Length * 2);
		}

		return _input.TakeUnconsumedInput(_output, written);
#endif
	}

	/// <summary>
	/// Inflates into the tail of <see cref="_output"/>, growing it first when it is full, and
	/// returns how many bytes it produced. Zero means the inflater wants more input.
	/// </summary>
	private int InflateInto(ref int written)
	{
		if (written == _output.Length)
		{
			Array.Resize(ref _output, _output.Length * 2);
		}

#if NETSTANDARD2_0
		var produced = _inflater.Inflate(_output, written, _output.Length - written);
#else
		var produced = _inflater.Read(_output, written, _output.Length - written);
#endif
		written += produced;
		return produced;
	}

	/// <inheritdoc />
	public void Dispose()
	{
#if !NETSTANDARD2_0
		try
		{
			_inflater.Dispose();
		}
		finally
		{
			_input.Dispose();
		}
#endif
	}

#if !NETSTANDARD2_0
	/// <summary>
	/// The bytes the peer has sent that <see cref="ZLibStream"/> has not consumed yet.
	/// </summary>
	/// <remarks>
	/// <see cref="ZLibStream"/> only pulls, so an incremental feed needs something for it to pull
	/// from. Returning 0 from <see cref="Read"/> is how this says "no more input right now", which
	/// ends the current <c>Read</c> without ending the deflate stream: the inflater picks up where
	/// it left off once there is more.
	/// </remarks>
	private sealed class PendingInput : Stream
	{
		private byte[] _buffer = new byte[64];
		private int _start;
		private int _end;

		/// <summary>
		/// Whether the most recent <see cref="Read"/> found nothing left. This distinguishes
		/// "the peer has not sent the rest yet" from "what the peer sent is corrupt", which
		/// <see cref="ZLibStream"/> reports as the same exception under strict validation.
		/// </summary>
		public bool RanDryDuringLastRead { get; private set; }

		/// <summary>
		/// How many pushed bytes <see cref="ZLibStream"/> has not taken.
		/// </summary>
		/// <remarks>
		/// A running inflater always empties this — <c>DeflateStream</c> keeps pulling until a read
		/// comes back empty — so anything left here means it has stopped asking, which for zlib
		/// means the stream ended. These bytes are therefore past the end of it.
		/// </remarks>
		public int UnconsumedInputLength => _end - _start;

		/// <inheritdoc cref="UnconsumedInputLength" />
		public bool HasUnconsumedInput => _end > _start;

		/// <summary>Moves everything left here into <paramref name="destination"/>.</summary>
		public int TakeUnconsumedInput(byte[] destination, int offset)
		{
			var taken = _end - _start;
			Buffer.BlockCopy(_buffer, _start, destination, offset, taken);
			_start = 0;
			_end = 0;
			return taken;
		}

		public void Push(byte value)
		{
			RanDryDuringLastRead = false;

			if (_end == _buffer.Length)
			{
				Compact();
			}

			_buffer[_end++] = value;
		}

		private void Compact()
		{
			var pending = _end - _start;
			Buffer.BlockCopy(_buffer, _start, _buffer, 0, pending);
			_start = 0;
			_end = pending;

			if (_end == _buffer.Length)
			{
				Array.Resize(ref _buffer, _buffer.Length * 2);
			}
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			var taken = Math.Min(count, _end - _start);
			RanDryDuringLastRead = taken == 0;
			Buffer.BlockCopy(_buffer, _start, buffer, offset, taken);
			_start += taken;

			if (_start == _end)
			{
				_start = 0;
				_end = 0;
			}

			return taken;
		}

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
#endif
}

/// <summary>
/// Deflates everything this side sends, as one zlib stream for the life of the connection.
/// </summary>
/// <remarks>
/// Every write is flushed, because the peer cannot decode a partial deflate block and this is a
/// conversation: a prompt that stays in the encoder until the next write is a prompt the user never
/// sees. Flushing costs a few bytes per write and is what every MCCP implementation does.
/// </remarks>
internal sealed class MCCPDeflateTransform : IOutboundByteTransform
{
	private readonly MemoryStream _sink = new();
#if NETSTANDARD2_0
	private readonly DeflaterOutputStream _deflater;
#else
	private readonly ZLibStream _deflater;
#endif

	public MCCPDeflateTransform()
	{
#if NETSTANDARD2_0
		_deflater = new DeflaterOutputStream(_sink) { IsStreamOwner = false };
#else
		_deflater = new ZLibStream(_sink, CompressionLevel.Optimal, leaveOpen: true);
#endif
	}

	/// <inheritdoc />
	public ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> data)
	{
		if (data.IsEmpty)
		{
			return data;
		}

		if (MemoryMarshal.TryGetArray(data, out var segment) && segment.Array is not null)
		{
			_deflater.Write(segment.Array, segment.Offset, segment.Count);
		}
		else
		{
			var copy = data.ToArray();
			_deflater.Write(copy, 0, copy.Length);
		}

		_deflater.Flush();
		var produced = _sink.ToArray();
		_sink.SetLength(0);
		return produced;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			_deflater.Dispose();
		}
		finally
		{
			_sink.Dispose();
		}
	}
}
