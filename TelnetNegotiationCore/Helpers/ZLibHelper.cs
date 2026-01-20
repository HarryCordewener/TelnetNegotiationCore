using System;
using System.IO;
using System.IO.Compression;

namespace TelnetNegotiationCore.Helpers;

#if NETSTANDARD2_1
/// <summary>
/// ZLib compression helper for .NET Standard 2.1 using DeflateStream with manual header/trailer handling.
/// Implements RFC 1950 zlib format.
/// </summary>
internal static class ZLibHelper
{
	/// <summary>
	/// Decompresses zlib-formatted data using DeflateStream.
	/// </summary>
	/// <param name="data">The compressed data with zlib header and trailer</param>
	/// <returns>The decompressed data</returns>
	public static byte[] Decompress(byte[] data)
	{
		if (data == null || data.Length < 6)
			throw new ArgumentException("Data is too small to be valid zlib format", nameof(data));

		// Verify and skip zlib header (2 bytes)
		// Expected: 0x78 (CMF: DEFLATE with 32K window) and 0x9C or other FLG byte
		byte cmf = data[0];
		byte flg = data[1];
		
		// Verify this is deflate method (lower 4 bits of CMF should be 8)
		if ((cmf & 0x0F) != 0x08)
			throw new InvalidDataException("Not a valid zlib stream (invalid compression method)");

		// Verify checksum of header
		if (((cmf * 256 + flg) % 31) != 0)
			throw new InvalidDataException("Not a valid zlib stream (header checksum failed)");

		// Extract compressed data (skip 2-byte header, 4-byte ADLER-32 trailer)
		int compressedLength = data.Length - 6;
		using var compressedStream = new MemoryStream(data, 2, compressedLength);
		using var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
		using var outputStream = new MemoryStream();
		
		deflateStream.CopyTo(outputStream);
		
		// Note: We don't verify ADLER-32 checksum for performance
		// The checksum is in the last 4 bytes of data[]
		
		return outputStream.ToArray();
	}

	/// <summary>
	/// Creates a stream that compresses data in zlib format using DeflateStream.
	/// </summary>
	/// <param name="outputStream">The stream to write compressed data to</param>
	/// <returns>A wrapper stream that handles compression</returns>
	public static Stream CreateCompressStream(Stream outputStream)
	{
		return new ZLibCompressStream(outputStream);
	}

	/// <summary>
	/// Wrapper stream that adds zlib header/trailer around DeflateStream.
	/// </summary>
	private class ZLibCompressStream : Stream
	{
		private readonly Stream _baseStream;
		private readonly DeflateStream _deflateStream;
		private bool _headerWritten;
		private bool _disposed;
		private uint _adler32 = 1; // ADLER-32 starts at 1

		public ZLibCompressStream(Stream baseStream)
		{
			_baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
			_deflateStream = new DeflateStream(baseStream, CompressionMode.Compress, leaveOpen: true);
		}

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
			_deflateStream.Flush();
			_baseStream.Flush();
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			if (!_headerWritten)
			{
				// Write zlib header
				// CMF: 0x78 = DEFLATE (8) with 32K window size (7 << 4)
				// FLG: 0x9C = default compression level, checksum
				_baseStream.WriteByte(0x78);
				_baseStream.WriteByte(0x9C);
				_headerWritten = true;
			}

			// Update ADLER-32 checksum
			UpdateAdler32(buffer, offset, count);

			// Write compressed data
			_deflateStream.Write(buffer, offset, count);
		}

		private void UpdateAdler32(byte[] buffer, int offset, int count)
		{
			const uint ADLER_MOD = 65521;
			uint s1 = _adler32 & 0xFFFF;
			uint s2 = (_adler32 >> 16) & 0xFFFF;

			for (int i = 0; i < count; i++)
			{
				s1 = (s1 + buffer[offset + i]) % ADLER_MOD;
				s2 = (s2 + s1) % ADLER_MOD;
			}

			_adler32 = (s2 << 16) | s1;
		}

		protected override void Dispose(bool disposing)
		{
			if (!_disposed && disposing)
			{
				// Close deflate stream
				_deflateStream.Dispose();

				// Write ADLER-32 checksum trailer (4 bytes, big-endian)
				_baseStream.WriteByte((byte)((_adler32 >> 24) & 0xFF));
				_baseStream.WriteByte((byte)((_adler32 >> 16) & 0xFF));
				_baseStream.WriteByte((byte)((_adler32 >> 8) & 0xFF));
				_baseStream.WriteByte((byte)(_adler32 & 0xFF));

				_baseStream.Flush();
				_disposed = true;
			}

			base.Dispose(disposing);
		}
	}
}
#endif
