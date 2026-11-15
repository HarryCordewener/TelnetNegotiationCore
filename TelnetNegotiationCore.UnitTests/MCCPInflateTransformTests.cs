using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TelnetNegotiationCore.Protocols;
using TUnit.Assertions.Enums;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="MCCPInflateTransform"/> on its own: whole chunks in, bounded slices out.
/// </summary>
public class MCCPInflateTransformTests
{
	private static byte[] Deflate(byte[] payload, bool finish)
	{
		using var sink = new MemoryStream();
		var z = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true);
		z.Write(payload, 0, payload.Length);
		z.Flush();
		if (finish) z.Dispose();
		var bytes = sink.ToArray();
		if (!finish) z.Dispose();
		return bytes;
	}

	private static MCCPInflateTransform Inflater(Action onEnd = null, Action onFail = null) =>
		new(NullLogger.Instance,
			() => { onFail?.Invoke(); return default; },
			() => { onEnd?.Invoke(); return default; },
			maxExpansionRatio: int.MaxValue);

	/// <summary>
	/// Drives the transform the way the interpreter does, and records every slice it hands back.
	/// </summary>
	private static async Task<(byte[] Output, List<int> SliceSizes, int Consumed)> DrainAsync(MCCPInflateTransform transform, byte[] wire)
	{
		var output = new List<byte>();
		var sizes = new List<int>();
		var offset = 0;
		var more = false;
		while (offset < wire.Length || more)
		{
			var result = await transform.DecodeChunkAsync(wire.AsMemory(offset));
			offset += result.Consumed;
			more = result.HasMoreOutput;
			output.AddRange(result.Decoded.ToArray());
			sizes.Add(result.Decoded.Length);
			if (result.Consumed == 0 && result.Decoded.IsEmpty && !more) break;
		}

		return (output.ToArray(), sizes, offset);
	}

	[Test]
	public async Task AHighlyCompressibleChunkComesOutInBoundedSlices()
	{
		// 1 MiB of zeros is about a kilobyte of deflate: one chunk the peer controls entirely. It must
		// come back as many slices no larger than OutputChunkSize, never as one buffer.
		var payload = new byte[1024 * 1024];
		var wire = Deflate(payload, finish: false);

		using var transform = Inflater();
		var (output, sizes, consumed) = await DrainAsync(transform, wire);

		await Assert.That(consumed).IsEqualTo(wire.Length);
		await Assert.That(output.Length).IsEqualTo(payload.Length);
		await Assert.That(sizes.Max()).IsLessThanOrEqualTo(MCCPInflateTransform.OutputChunkSize);
	}

	[Test]
	public async Task BytesAfterTheEndOfTheStreamAreLeftUnconsumed()
	{
		var text = "the end\n"u8.ToArray();
		var stream = Deflate(text, finish: true);
		byte[] after = [1, 2, 3, 4, 5];
		var wire = stream.Concat(after).ToArray();

		var ended = 0;
		using var transform = Inflater(onEnd: () => ended++);
		var result = await transform.DecodeChunkAsync(wire);

		await Assert.That(result.Decoded.ToArray()).IsEquivalentTo(text, CollectionOrdering.Matching);
		await Assert.That(result.Consumed).IsEqualTo(stream.Length);
		await Assert.That(result.HasMoreOutput).IsFalse();
		await Assert.That(ended).IsEqualTo(1);
	}

	[Test]
	[Arguments(1)]
	[Arguments(7)]
	[Arguments(4096)]
	public async Task ChunkBoundariesDoNotChangeTheOutput(int chunkSize)
	{
		var random = new Random(3);
		var payload = Enumerable.Range(0, 200_000).Select(i => (byte)('a' + random.Next(4))).ToArray();
		var wire = Deflate(payload, finish: false);

		using var transform = Inflater();
		var output = new List<byte>();
		for (var at = 0; at < wire.Length; at += chunkSize)
		{
			var (part, _, consumed) = await DrainAsync(transform, wire.AsSpan(at, Math.Min(chunkSize, wire.Length - at)).ToArray());
			await Assert.That(consumed).IsEqualTo(Math.Min(chunkSize, wire.Length - at));
			output.AddRange(part);
		}

		await Assert.That(output.SequenceEqual(payload)).IsTrue();
	}

	[Test]
	public async Task TheOneByteFormDecodesTheSameStream()
	{
		var payload = Enumerable.Range(0, 50_000).Select(i => (byte)(i % 251)).ToArray();
		var wire = Deflate(payload, finish: true).Concat(new byte[] { 42 }).ToArray();

		using var transform = Inflater();
		var output = new List<byte>();
		foreach (var b in wire)
		{
			output.AddRange((await transform.DecodeAsync(b)).ToArray());
		}

		// The byte past the end of the stream is passed through, as it always was.
		await Assert.That(output.SequenceEqual(payload.Append((byte)42))).IsTrue();
	}

	[Test]
	public async Task GarbageStopsTheInflaterAndConsumesTheChunk()
	{
		var failed = 0;
		using var transform = Inflater(onFail: () => failed++);
		byte[] wire = [0x12, 0x34, 0x56, 0x78, 0x9A];

		var (output, _, consumed) = await DrainAsync(transform, wire);

		await Assert.That(failed).IsEqualTo(1);
		await Assert.That(output).IsEmpty();
		await Assert.That(consumed).IsEqualTo(wire.Length);
	}
}
