using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// Generated-input checks for the parts of the mudstandards.org work that take arbitrary bytes: the
/// scan that records this side's offers, the MCCP stream end, and the MTTS bit names.
/// </summary>
public class MudStandardsProperties
{
	private const byte IAC = 255, WILL = 251, WONT = 252, DO = 253, DONT = 254, SB = 250, SE = 240;

	private const int Cases = 2000;

	/// <summary>
	/// The offer scan finds every <c>IAC WILL</c> and <c>IAC DO</c> frame in a write and nothing else,
	/// whatever surrounds them: refusals, other commands, escaped 255s in text, and subnegotiations
	/// whose payload holds the bytes of a WILL or DO.
	/// </summary>
	[Test]
	public async Task OfferScanFindsExactlyTheOffers()
	{
		for (var i = 0; i < Cases; i++)
		{
			var rng = new Rng(0x0FFE125CA4 + (ulong)i);
			var write = new List<byte>();
			var expected = new List<(byte, byte)>();

			for (var token = rng.Next(12); token > 0; token--)
			{
				switch (rng.Next(6))
				{
					case 0:
						var verb = rng.Bool(50) ? WILL : DO;
						var option = rng.Bool(10) ? IAC : rng.NextByte();
						write.AddRange([IAC, verb, option]);
						expected.Add((verb, option));
						break;
					case 1:
						write.AddRange([IAC, rng.Bool(50) ? WONT : DONT, rng.NextByte()]);
						break;
					case 2:
						// Text: a literal 255 goes out doubled.
						for (var n = rng.Next(8); n > 0; n--)
						{
							var b = rng.Bool(30) ? IAC : rng.Pick(new byte[] { WILL, DO, (byte)'a', rng.NextByte() });
							write.Add(b);
							if (b == IAC) write.Add(IAC);
						}
						break;
					case 3:
						write.AddRange([IAC, SB, rng.NextByte()]);
						for (var n = rng.Next(8); n > 0; n--)
						{
							var b = rng.Pick(new byte[] { IAC, WILL, DO, rng.NextByte() });
							write.Add(b);
							if (b == IAC) write.Add(IAC);
						}
						write.AddRange([IAC, SE]);
						break;
					case 4:
						// Two-byte commands: NOP, GA, EOR and the like.
						write.AddRange([IAC, rng.Pick(new byte[] { 241, 249, 239, 246 })]);
						break;
					default:
						write.Add((byte)'x');
						break;
				}
			}

			var found = new List<(byte Verb, byte Option)>();
			TelnetInterpreter.CollectOffers(write.ToArray(), found);

			await Assert.That(found.SequenceEqual(expected)).IsTrue()
				.Because($"case {i}: [{Convert.ToHexString(write.ToArray())}] gave {found.Count} offers, expected {expected.Count}");
		}
	}

	/// <summary>
	/// Whatever this side wrote through the MCCP encoder, in whatever pieces, ending it produces a
	/// complete zlib stream holding exactly those bytes, and this library's own inflater reports the
	/// end and passes the plain bytes after it through untouched.
	/// </summary>
	[Test]
	public async Task AFinishedStreamHoldsEverythingWrittenAndEndsCleanly()
	{
		for (var i = 0; i < 300; i++)
		{
			var rng = new Rng(0x2F1415B + (ulong)i);
			var written = new List<byte>();
			var wire = new List<byte>();

			using (var deflate = new MCCPDeflateTransform())
			{
				for (var chunk = rng.Next(10); chunk > 0; chunk--)
				{
					// Mostly short writes as a MUD makes them, sometimes a long repetitive one that
					// compresses hard, sometimes nothing at all.
					var length = rng.Bool(10) ? rng.Next(5000) : rng.Next(40);
					var repetitive = rng.Bool(40);
					var data = new byte[length];
					for (var j = 0; j < length; j++)
					{
						data[j] = repetitive ? (byte)('a' + j % 3) : rng.NextByte();
					}

					written.AddRange(data);
					wire.AddRange(deflate.Encode(data).ToArray());
				}

				wire.AddRange(deflate.Finish().ToArray());
			}

			var stream = wire.ToArray();
			await Assert.That(Inflate(stream).SequenceEqual(written)).IsTrue().Because($"case {i}: ZLibStream disagrees");
			await Assert.That(EndsWithAdler32(stream, written)).IsTrue().Because($"case {i}: no Adler-32 trailer");

			// Through this library's inflater, one byte at a time as the interpreter feeds it, with
			// plain telnet straight after the stream.
			byte[] plain = [IAC, WONT, 86, (byte)'o', (byte)'k'];
			var ends = 0;
			var failures = 0;
			var decoded = new List<byte>();
			using (var inflate = new MCCPInflateTransform(
				NullLogger.Instance,
				() => { failures++; return ValueTask.CompletedTask; },
				() => { ends++; return ValueTask.CompletedTask; }))
			{
				foreach (var b in stream.Concat(plain))
				{
					decoded.AddRange((await inflate.DecodeAsync(b)).ToArray());
				}
			}

			await Assert.That(failures).IsEqualTo(0).Because($"case {i}");
			await Assert.That(ends).IsEqualTo(1).Because($"case {i}");
			await Assert.That(decoded.SequenceEqual(written.Concat(plain))).IsTrue()
				.Because($"case {i}: decoded {decoded.Count} bytes, expected {written.Count + plain.Length}");
		}
	}

	/// <summary>Every MTTS value names each of its bits once, and SSL exactly when bit 2048 is set.</summary>
	[Test]
	public async Task EveryMttsValueNamesEachOfItsBits()
	{
		for (var value = 0; value < 4096; value++)
		{
			var names = MttsCapabilityNames.Expand((MttsCapabilities)value).ToList();

			await Assert.That(names.Count).IsEqualTo(PopCount(value)).Because($"MTTS {value}");
			await Assert.That(names.Distinct().Count()).IsEqualTo(names.Count).Because($"MTTS {value}");
			await Assert.That(names.Contains("SSL")).IsEqualTo((value & 2048) != 0).Because($"MTTS {value}");
		}
	}

	private static int PopCount(int value)
	{
		var count = 0;
		for (; value != 0; value &= value - 1) count++;
		return count;
	}

	private static byte[] Inflate(byte[] zlib)
	{
		using var input = new MemoryStream(zlib);
		using var inflater = new ZLibStream(input, CompressionMode.Decompress);
		using var output = new MemoryStream();
		inflater.CopyTo(output);
		return output.ToArray();
	}

	private static bool EndsWithAdler32(byte[] zlib, List<byte> data)
	{
		uint a = 1, b = 0;
		foreach (var d in data)
		{
			a = (a + d) % 65521;
			b = (b + a) % 65521;
		}

		var adler = (b << 16) | a;
		var tail = zlib.AsSpan(zlib.Length - 4);
		return tail[0] == (byte)(adler >> 24) && tail[1] == (byte)(adler >> 16)
			&& tail[2] == (byte)(adler >> 8) && tail[3] == (byte)adler;
	}
}
