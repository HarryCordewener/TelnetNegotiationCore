using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Interpreters;
using ICSharpCode.SharpZipLib.Zip.Compression;
#if NETSTANDARD2_0
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
#endif

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Inflates an MCCP stream: <b>one</b> zlib stream for the life of the connection, fed a chunk of wire
/// bytes at a time and drained a bounded amount at a time.
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
/// <para>
/// <b>Why SharpZipLib's <see cref="Inflater"/> on every target, not <see cref="ZLibStream"/>.</b> The
/// end of the stream has to be found to the byte: whatever follows it is plain telnet, possibly a
/// fresh compression marker and a new stream. <see cref="ZLibStream"/> pulls its input in blocks and
/// keeps what it pulled past the end, so it could only be fed one byte per call, which cost about 37
/// times what inflating in blocks does. <see cref="Inflater.RemainingInput"/> says exactly what it has
/// not used, so it can be fed a whole chunk.
/// </para>
/// <para>
/// <b>The output is bounded, not the input.</b> Each call produces at most
/// <see cref="OutputChunkSize"/> bytes, which the interpreter hands to the state machine before it asks
/// for more. A decompression bomb arrives as any number of bounded slices and never as one buffer, so
/// memory stays flat however hostile the peer is; <see cref="ExpansionIsImplausible"/> bounds the CPU.
/// </para>
/// </remarks>
internal sealed class MCCPInflateTransform : IInboundChunkTransform
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

	/// <summary>The most one <see cref="DecodeChunkAsync"/> call produces, and the size of its buffer.</summary>
	internal const int OutputChunkSize = 2048;

	private readonly ILogger _logger;
	private readonly Func<ValueTask> _onFailureAsync;
	private readonly Func<ValueTask> _onStreamEndAsync;
	private readonly int _maxExpansionRatio;
	private readonly Inflater _inflater = new();
	private readonly byte[] _output = new byte[OutputChunkSize];
	private long _compressedIn;
	private long _inflatedOut;
	private bool _failed;
	private bool _ended;

	/// <summary>
	/// Wire bytes handed to the inflater that have not yet been reported consumed: the start of the
	/// next call's input, which the inflater still holds. Non-zero only while its window has bytes left.
	/// </summary>
	private int _unreported;

	/// <summary>Storage for <see cref="DecodeAsync"/>, the one-byte form.</summary>
	private readonly byte[] _single = new byte[1];
	private byte[] _singleOutput = new byte[OutputChunkSize];

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
	}

	/// <inheritdoc />
	/// <remarks>
	/// The inflater reads <paramref name="wire"/> where it lies rather than copying it, so the caller
	/// must keep that memory alive, and keep calling, until a result reports
	/// <see cref="InboundDecodeResult.HasMoreOutput"/> false with all of it consumed.
	/// </remarks>
	public ValueTask<InboundDecodeResult> DecodeChunkAsync(ReadOnlyMemory<byte> wire)
	{
		if (_failed)
		{
			// A broken deflate stream cannot be resumed. Everything after it is dropped, as before.
			return new ValueTask<InboundDecodeResult>(new InboundDecodeResult(wire.Length, ReadOnlyMemory<byte>.Empty, false));
		}

		if (_ended)
		{
			// The peer stopped compressing and the owner has not swapped this out yet. These are
			// telnet bytes, not deflate bytes; passing them through is the only thing that is not
			// data loss.
			var passed = Math.Min(wire.Length, _output.Length);
			wire.Span.Slice(0, passed).CopyTo(_output);
			return new ValueTask<InboundDecodeResult>(new InboundDecodeResult(passed, _output.AsMemory(0, passed), false));
		}

		int written;
		try
		{
			if (_unreported == 0 && !wire.IsEmpty)
			{
				if (!MemoryMarshal.TryGetArray(wire, out var segment) || segment.Array is null)
				{
					throw new ArgumentException("MCCP input must be array-backed.", nameof(wire));
				}

				_inflater.SetInput(segment.Array, segment.Offset, segment.Count);
				_unreported = segment.Count;

				// Counted as handed over, so the ratio below is judged against everything the peer has
				// sent, including bytes still in the window while their output drains.
				_compressedIn += segment.Count;
			}

			// Called even with no new input: output that did not fit last time is still in its window.
			written = _inflater.Inflate(_output, 0, _output.Length);
		}
		catch (Exception ex)
		{
			_failed = true;
			_unreported = 0;
			_logger.LogError(ex, "MCCP: the peer's compressed stream is not valid zlib. Decompression stopped");
			return FailAsync(wire.Length);
		}

		// Counted before the stream-end check, not after: a peer that blows the budget on the very
		// call that ends its stream still blew it, and leaving that call out of the accounting would
		// make the running total quietly wrong for anyone who reads it later.
		_inflatedOut += written;

		if (_inflater.IsFinished)
		{
			// Whatever the inflater was handed but did not use is past the end of the zlib stream,
			// which makes it the first plain telnet the peer has sent since the marker. It is left
			// unconsumed, so the interpreter hands it to the state machine itself -- and a fresh
			// marker in it installs a fresh inflater at exactly the right byte.
			var leftover = Math.Min(_inflater.RemainingInput, _unreported);
			var consumed = _unreported - leftover;
			_compressedIn -= leftover;
			_unreported = 0;
			_ended = true;

			if (ExpansionIsImplausible())
			{
				return FailImplausibleAsync(consumed);
			}

			_logger.LogInformation(
				"MCCP: the peer ended its compressed stream; the connection continues in the clear");
			return EndAsync(new InboundDecodeResult(consumed, _output.AsMemory(0, written), false));
		}

		var reported = 0;
		if (_inflater.IsNeedingInput)
		{
			// Everything handed over is inside the inflater now. A few bits of it may sit in its bit
			// buffer, but those are its state, not input it declined.
			reported = _unreported;
			_unreported = 0;
		}
		else if (written == 0)
		{
			// Input left, room left, and nothing produced: the only way there is a preset dictionary,
			// which MCCP never uses. Stop rather than spin.
			_failed = true;
			_unreported = 0;
			_logger.LogError("MCCP: the peer's compressed stream asks for a preset dictionary. Decompression stopped");
			return FailAsync(wire.Length);
		}

		if (ExpansionIsImplausible())
		{
			return FailImplausibleAsync(Math.Max(reported, wire.Length));
		}

		// Output full means there may be more without any new input; input left in the window means
		// there certainly is more to do before the caller may let go of it.
		var more = written == _output.Length || _unreported > 0;
		return new ValueTask<InboundDecodeResult>(new InboundDecodeResult(reported, _output.AsMemory(0, written), more));
	}

	/// <inheritdoc />
	/// <remarks>
	/// The one-byte form the public interface requires. The interpreter uses
	/// <see cref="DecodeChunkAsync"/>; this drains everything one byte produces, at most DEFLATE's
	/// 1,032 bytes, and passes a byte left past the end of the stream through.
	/// </remarks>
	public async ValueTask<ReadOnlyMemory<byte>> DecodeAsync(byte raw)
	{
		_single[0] = raw;
		var input = _single.AsMemory();
		var total = 0;
		InboundDecodeResult result;
		do
		{
			result = await DecodeChunkAsync(input);
			input = input.Slice(result.Consumed);
			if (_singleOutput.Length - total < result.Decoded.Length)
			{
				Array.Resize(ref _singleOutput, Math.Max(_singleOutput.Length * 2, total + result.Decoded.Length));
			}

			result.Decoded.Span.CopyTo(_singleOutput.AsSpan(total));
			total += result.Decoded.Length;
		}
		while (result.HasMoreOutput || (!input.IsEmpty && (result.Consumed > 0 || !result.Decoded.IsEmpty || _ended)));

		return _singleOutput.AsMemory(0, total);
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

	private ValueTask<InboundDecodeResult> FailImplausibleAsync(int consumed)
	{
		_failed = true;
		_unreported = 0;
		_logger.LogError(
			"MCCP: the peer's stream claims to expand {InflatedBytes} bytes out of {CompressedBytes} "
			+ "({Ratio}:1, ceiling {Ceiling}:1). Decompression stopped",
			_inflatedOut, _compressedIn, _inflatedOut / Math.Max(_compressedIn, 1), _maxExpansionRatio);
		return FailAsync(consumed);
	}

	private async ValueTask<InboundDecodeResult> FailAsync(int consumed)
	{
		await _onFailureAsync();
		return new InboundDecodeResult(consumed, ReadOnlyMemory<byte>.Empty, false);
	}

	private async ValueTask<InboundDecodeResult> EndAsync(InboundDecodeResult result)
	{
		await _onStreamEndAsync();
		return result;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		// The inflater holds no unmanaged state.
	}
}

/// <summary>
/// Deflates everything this side sends, as one zlib stream for the life of the connection.
/// </summary>
/// <remarks>
/// Every write is flushed, because the peer cannot decode a partial deflate block and this is a
/// conversation: a prompt that stays in the encoder until the next write is a prompt the user never
/// sees. Flushing costs a few bytes per write and is what every MCCP implementation does.
/// </remarks>
internal sealed class MCCPDeflateTransform : IFinishableOutboundTransform
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
		_wroteAny |= produced.Length > 0;
		return produced;
	}

	/// <inheritdoc />
	/// <remarks>
	/// Closing the deflater writes the final block and the Adler-32 trailer into the sink; the
	/// deflater does not own the sink, so the bytes are still there to hand back.
	/// </remarks>
	public ReadOnlyMemory<byte> Finish()
	{
		_deflater.Dispose();
		var produced = _sink.ToArray();
		_sink.SetLength(0);

		// Some runtimes' ZLibStream (net8.0 and net10.0) write nothing at all when closed before
		// any data went through. The peer has seen the marker and is waiting for a zlib header, so
		// an empty stream still has to be a stream.
		return produced.Length == 0 && !_wroteAny ? s_emptyStream : produced;
	}

	/// <summary>
	/// A complete zlib stream holding nothing: the header, one empty final block, and the Adler-32
	/// of no bytes (RFC 1950, RFC 1951).
	/// </summary>
	private static readonly byte[] s_emptyStream = [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];

	private bool _wroteAny;

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
