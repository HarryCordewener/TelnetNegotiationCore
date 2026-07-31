using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
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
/// MCCP is not a sequence of independently compressed messages — the deflate back-reference window
/// and the Huffman state carry across everything the peer sends after the marker, so a fresh
/// inflater per network read decodes the first read and then fails on the second. Nor do reads line
/// up with anything: a read can end in the middle of a symbol, and one byte can complete a match
/// that expands to hundreds. Hence the incremental interface.
/// </remarks>
internal sealed class MCCPInflateTransform : IInboundByteTransform
{
	private readonly ILogger _logger;
	private readonly Action _onFailure;
	private byte[] _output = new byte[1024];
	private bool _failed;

#if NETSTANDARD2_0
	private readonly Inflater _inflater = new Inflater();
	private readonly byte[] _input = new byte[1];
#else
	private readonly PendingInput _input = new();
	private readonly ZLibStream _inflater;
#endif

	/// <param name="logger">Where a corrupt stream is reported.</param>
	/// <param name="onFailure">
	/// Called once, if the peer's stream turns out not to be valid zlib. There is no recovering a
	/// deflate stream that has gone wrong, so after this the transform decodes nothing.
	/// </param>
	public MCCPInflateTransform(ILogger logger, Action onFailure)
	{
		_logger = logger;
		_onFailure = onFailure;
#if !NETSTANDARD2_0
		_inflater = new ZLibStream(_input, CompressionMode.Decompress);
#endif
	}

	/// <inheritdoc />
	public ReadOnlyMemory<byte> Decode(byte raw)
	{
		if (_failed)
		{
			return ReadOnlyMemory<byte>.Empty;
		}

		var written = 0;
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
		catch (Exception ex)
		{
			_failed = true;
			_logger.LogError(ex, "MCCP: the peer's compressed stream is not valid zlib. Decompression stopped");
			_onFailure();
			return ReadOnlyMemory<byte>.Empty;
		}

		return _output.AsMemory(0, written);
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
		_inflater.Dispose();
		_input.Dispose();
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

		public void Push(byte value)
		{
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
		_deflater.Dispose();
		_sink.Dispose();
	}
}
