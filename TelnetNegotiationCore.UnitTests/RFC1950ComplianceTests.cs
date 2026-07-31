using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Tests to verify RFC 1950 (ZLIB Compressed Data Format) compliance
/// https://tintin.mudhalla.net/rfc/rfc1950
/// </summary>
public class RFC1950ComplianceTests : BaseTest
{
	[Test]
	public async Task ZLibStreamProducesRFC1950CompliantFormat()
	{
		// Arrange - Test data to compress
		var testData = Encoding.UTF8.GetBytes("MCCP RFC 1950 Compliance Test Data");

		// Act - Compress using ZLibStream
		byte[] compressedData;
		using (var ms = new MemoryStream())
		{
			using (var zlib = new ZLibStream(ms, CompressionMode.Compress))
			{
				zlib.Write(testData, 0, testData.Length);
			}
			compressedData = ms.ToArray();
		}

		// Assert - Verify RFC 1950 header format
		await Assert.That(compressedData.Length).IsGreaterThanOrEqualTo(2, "Compressed data should have at least 2-byte header");

		// RFC 1950 header analysis
		byte cmf = compressedData[0]; // Compression Method and Flags
		byte flg = compressedData[1]; // Flags

		// Extract compression method (bits 0-3 of CMF)
		int compressionMethod = cmf & 0x0F;
		
		// RFC 1950 specifies that compression method should be 8 for DEFLATE
		await Assert.That(compressionMethod).IsEqualTo(8, "Compression method should be 8 (DEFLATE) per RFC 1950");

		// RFC 1950 check: (CMF * 256 + FLG) % 31 == 0
		int headerCheck = (cmf * 256 + flg) % 31;
		await Assert.That(headerCheck).IsEqualTo(0, "Header checksum (CMF*256 + FLG) mod 31 must equal 0 per RFC 1950");

		logger.LogInformation("RFC 1950 compliance verified: CMF=0x{CMF:X2}, FLG=0x{FLG:X2}, Method={Method}, Check={Check}",
			cmf, flg, compressionMethod, headerCheck);
	}

	[Test]
	public async Task ZLibStreamRoundTripSucceeds()
	{
		// Arrange
		var originalData = Encoding.UTF8.GetBytes("Round-trip test for RFC 1950 zlib compression");

		// Act - Compress
		byte[] compressedData;
		using (var ms = new MemoryStream())
		{
			using (var zlib = new ZLibStream(ms, CompressionMode.Compress))
			{
				zlib.Write(originalData, 0, originalData.Length);
			}
			compressedData = ms.ToArray();
		}

		// Act - Decompress
		byte[] decompressedData;
		using (var ms = new MemoryStream(compressedData))
		using (var zlib = new ZLibStream(ms, CompressionMode.Decompress))
		using (var output = new MemoryStream())
		{
			zlib.CopyTo(output);
			decompressedData = output.ToArray();
		}

		// Assert
		await Assert.That(decompressedData.Length).IsEqualTo(originalData.Length);
		await AssertByteArraysEqual(decompressedData, originalData);

		var originalText = Encoding.UTF8.GetString(originalData);
		var decompressedText = Encoding.UTF8.GetString(decompressedData);
		await Assert.That(decompressedText).IsEqualTo(originalText);
	}

	[Test]
	public async Task ZLibStreamAchievesCompressionRatio()
	{
		// Arrange - Create repetitive data that compresses well
		var repetitiveData = Encoding.UTF8.GetBytes(string.Concat(System.Linq.Enumerable.Repeat("MCCP Compression Test ", 100)));

		// Act - Compress
		byte[] compressedData;
		using (var ms = new MemoryStream())
		{
			using (var zlib = new ZLibStream(ms, CompressionMode.Compress))
			{
				zlib.Write(repetitiveData, 0, repetitiveData.Length);
			}
			compressedData = ms.ToArray();
		}

		// Assert - Compression should reduce size significantly
		await Assert.That(compressedData.Length).IsLessThan(repetitiveData.Length, 
			"Compressed data should be smaller than original for repetitive content");

		double compressionRatio = (double)compressedData.Length / repetitiveData.Length * 100;
		logger.LogInformation("Compression ratio: {Ratio:F2}% ({Original} -> {Compressed} bytes)",
			compressionRatio, repetitiveData.Length, compressedData.Length);

		// RFC 1950 format adds minimal overhead (typically 6 bytes: 2-byte header + 4-byte ADLER-32 checksum)
		// For highly repetitive data, we should achieve significant compression
		await Assert.That(compressionRatio).IsLessThan(50.0, "Compression should reduce repetitive data by at least 50%");
	}

	/// <summary>
	/// MCCP's inflater feeds <see cref="ZLibStream"/> from a stream it pushes bytes into, and reads
	/// until <c>Read</c> returns 0. That only works because a base stream with nothing left is not
	/// treated as the end of the deflate stream. Nothing documents that, so it is pinned here.
	/// </summary>
	[Test]
	public async Task ZLibStreamResumesAfterTheBaseStreamRunsDry()
	{
		var sink = new MemoryStream();
		using var deflater = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true);

		byte[] Emit(string text)
		{
			var bytes = Encoding.ASCII.GetBytes(text);
			deflater.Write(bytes, 0, bytes.Length);
			deflater.Flush();
			var produced = sink.ToArray();
			sink.SetLength(0);
			return produced;
		}

		var first = Emit("first chunk");
		var second = Emit("second chunk");

		var feed = new DrainableStream();
		using var inflater = new ZLibStream(feed, CompressionMode.Decompress);
		var decoded = new StringBuilder();
		var buffer = new byte[256];

		foreach (var chunk in new[] { first, second })
		{
			foreach (var b in chunk)
			{
				feed.Push(b);
				int n;
				while ((n = inflater.Read(buffer, 0, buffer.Length)) > 0)
				{
					decoded.Append(Encoding.ASCII.GetString(buffer, 0, n));
				}
			}
		}

		// One byte at a time means the base stream ran dry on almost every read, and the inflater
		// carried on across every one of them.
		await Assert.That(feed.EmptyReads).IsGreaterThan(first.Length);
		await Assert.That(decoded.ToString()).IsEqualTo("first chunksecond chunk");
	}

	/// <summary>A stream that hands back whatever has been pushed, and 0 when there is nothing.</summary>
	private sealed class DrainableStream : Stream
	{
		private readonly System.Collections.Generic.Queue<byte> _pending = new();

		public int EmptyReads { get; private set; }

		public void Push(byte value) => _pending.Enqueue(value);

		public override int Read(byte[] buffer, int offset, int count)
		{
			var taken = 0;
			while (taken < count && _pending.Count > 0)
			{
				buffer[offset + taken++] = _pending.Dequeue();
			}

			if (taken == 0)
			{
				EmptyReads++;
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

	[Test]
	public async Task ZLibStreamIncludesADLER32Checksum()
	{
		// Arrange
		var testData = Encoding.UTF8.GetBytes("ADLER-32 checksum test");

		// Act - Compress
		byte[] compressedData;
		using (var ms = new MemoryStream())
		{
			using (var zlib = new ZLibStream(ms, CompressionMode.Compress))
			{
				zlib.Write(testData, 0, testData.Length);
			}
			compressedData = ms.ToArray();
		}

		// Assert - RFC 1950 requires ADLER-32 checksum at the end (4 bytes)
		// Minimum compressed data: 2-byte header + compressed data + 4-byte checksum
		await Assert.That(compressedData.Length).IsGreaterThanOrEqualTo(6,
			"RFC 1950 format requires minimum 6 bytes (2-byte header + data + 4-byte ADLER-32)");

		// Extract and log the last 4 bytes (ADLER-32 checksum)
		var checksumBytes = new byte[4];
		Array.Copy(compressedData, compressedData.Length - 4, checksumBytes, 0, 4);
		
		// ADLER-32 is stored in network byte order (big-endian)
		if (BitConverter.IsLittleEndian)
			Array.Reverse(checksumBytes);
		
		var adler32 = BitConverter.ToUInt32(checksumBytes, 0);
		logger.LogInformation("ADLER-32 checksum: 0x{Checksum:X8}", adler32);
		
		await Assert.That(adler32).IsGreaterThan(0u, "ADLER-32 checksum should be non-zero for non-empty data");
	}
}
