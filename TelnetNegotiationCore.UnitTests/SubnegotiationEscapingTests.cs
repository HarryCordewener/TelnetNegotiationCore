using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Helpers;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// RFC 854's escaping rule, and the two assumptions the rest of the library makes about it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SubnegotiationEscaping"/> has two shapes for one rule: an accumulating form for the
/// protocols that build a frame in a list, and an array-returning form for the paths that build one
/// as a <c>byte[]</c> — ordinary output among them, which is why that one keeps its early exit and
/// its pooled buffer. Two shapes are two chances to disagree, so the first group of tests pins them
/// to each other rather than each to a hand-written expectation.
/// </para>
/// <para>
/// The second assumption is about the sites that send a payload <em>without</em> escaping it: TTYPE's
/// <c>IS</c>, TSPEED's <c>IS</c>, XDISPLOC's <c>IS</c>, and CHARSET's <c>ACCEPTED</c> and
/// <c>REQUEST</c>. All five encode with <see cref="Encoding.ASCII"/>, which cannot produce 0xFF, so
/// there is nothing there to escape. That is a property of the encoder rather than of the code around
/// it, and nothing in those five places says so — so it is checked here instead of remembered.
/// </para>
/// </remarks>
public class SubnegotiationEscapingTests
{
	private const byte IAC = 255;

	private static byte[] ViaList(byte[] input)
	{
		var destination = new List<byte>();
		SubnegotiationEscaping.AppendEscaped(destination, input);
		return [.. destination];
	}

	// ---------------------------------------------------------------------------------------------
	// The rule itself.
	// ---------------------------------------------------------------------------------------------

	[Test]
	[Arguments(new byte[] { }, new byte[] { })]
	[Arguments(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 })]
	[Arguments(new byte[] { IAC }, new byte[] { IAC, IAC })]
	[Arguments(new byte[] { IAC, IAC }, new byte[] { IAC, IAC, IAC, IAC })]
	[Arguments(new byte[] { 1, IAC, 2 }, new byte[] { 1, IAC, IAC, 2 })]
	[Arguments(new byte[] { IAC, 1 }, new byte[] { IAC, IAC, 1 })]
	[Arguments(new byte[] { 1, IAC }, new byte[] { 1, IAC, IAC })]
	// 254 and 250 are DONT and SB; only 255 is escaped, and escaping its neighbours would corrupt
	// every payload that happened to contain one.
	[Arguments(new byte[] { 254, 250, 240 }, new byte[] { 254, 250, 240 })]
	public async Task EveryIacIsDoubledAndNothingElseIs(byte[] input, byte[] expected)
	{
		await Assert.That(SubnegotiationEscaping.Escaped(input))
			.IsEquivalentTo(expected, CollectionOrdering.Matching);
		await Assert.That(ViaList(input)).IsEquivalentTo(expected, CollectionOrdering.Matching);
	}

	/// <summary>A single byte, the shape LINEMODE's <c>MODE</c> needs.</summary>
	[Test]
	public async Task ASingleByteIsDoubledOnlyWhenItIsIac()
	{
		var escaped = new List<byte>();
		SubnegotiationEscaping.AppendEscaped(escaped, IAC);
		await Assert.That(escaped).IsEquivalentTo(new List<byte> { IAC, IAC }, CollectionOrdering.Matching);

		var plain = new List<byte>();
		SubnegotiationEscaping.AppendEscaped(plain, (byte)7);
		await Assert.That(plain).IsEquivalentTo(new List<byte> { 7 }, CollectionOrdering.Matching);
	}

	// ---------------------------------------------------------------------------------------------
	// The two shapes must not drift apart.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// Over inputs built to stress the array form's branches: short enough to stay in one segment,
	/// long enough to cross its capacity hint, all-<c>IAC</c>, and no-<c>IAC</c> so the early exit
	/// fires.
	/// </summary>
	[Test]
	public async Task TheTwoShapesAgreeOnEveryGeneratedPayload()
	{
		// xorshift64*, seeded, so a failure is reproducible.
		var state = 0x9E3779B97F4A7C15UL;
		byte NextByte()
		{
			state ^= state >> 12;
			state ^= state << 25;
			state ^= state >> 27;
			return (byte)((state * 0x2545F4914F6CDD1DUL) >> 56);
		}

		var lengths = new[] { 0, 1, 2, 3, 17, 255, 256, 257, 1024 };

		foreach (var length in lengths)
		{
			// Three densities of IAC: none, occasional, and every byte.
			foreach (var iacInEvery in new[] { 0, 4, 1 })
			{
				var input = new byte[length];
				for (var i = 0; i < length; i++)
				{
					input[i] = iacInEvery == 1 ? IAC
						: iacInEvery == 0 ? (byte)(NextByte() % 255)
						: i % iacInEvery == 0 ? IAC : (byte)(NextByte() % 255);
				}

				var viaSpan = SubnegotiationEscaping.Escaped(input);
				var viaList = ViaList(input);

				await Assert.That(viaSpan).IsEquivalentTo(viaList, CollectionOrdering.Matching)
					.Because($"length {length}, IAC in every {iacInEvery}: the two shapes disagree");

				// And the result is the input with exactly the IAC count added.
				var iacCount = input.Count(b => b == IAC);
				await Assert.That(viaSpan.Length).IsEqualTo(input.Length + iacCount)
					.Because($"length {length}, IAC in every {iacInEvery}: wrong output length");
			}
		}
	}

	/// <summary>
	/// A payload with no <c>IAC</c> takes the array form's early exit, which must still produce a
	/// value the caller can hold independently of the input buffer.
	/// </summary>
	[Test]
	public async Task TheEarlyExitStillReturnsItsOwnArray()
	{
		var input = new byte[] { 1, 2, 3 };
		var escaped = SubnegotiationEscaping.Escaped(input);

		input[0] = 9;

		await Assert.That(escaped[0]).IsEqualTo((byte)1)
			.Because("the caller's array was mutated afterwards and the escaped copy followed it");
	}

	// ---------------------------------------------------------------------------------------------
	// The assumption behind the five sites that do not escape.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// TTYPE, TSPEED, XDISPLOC and CHARSET send application-supplied strings without escaping them,
	/// which is only safe because <see cref="Encoding.ASCII"/> replaces anything outside 0x00–0x7F
	/// with <c>?</c> rather than encoding it. Swap in a wider encoding at any of those sites and the
	/// payload becomes injectable, so this is the test that would catch it.
	/// </summary>
	[Test]
	[Arguments("ÿ")]
	[Arguments("ÿabc")]
	[Arguments("XTERM-ÿ")]
	[Arguments("�")]
	[Arguments("Āÿ￿")]
	public async Task AsciiEncodingCannotProduceAnIacByte(string text)
	{
		var encoded = Encoding.ASCII.GetBytes(text);

		await Assert.That(encoded.Any(b => b == IAC))
			.IsFalse().Because($"Encoding.ASCII emitted 0xFF for {text.Length} char(s): [{string.Join(",", encoded)}]");

		await Assert.That(encoded.All(b => b <= 0x7F))
			.IsTrue().Because("ASCII's replacement fallback keeps every byte inside 0x00-0x7F");
	}

	/// <summary>
	/// ISO-8859-1 is the counter-example, and the reason the escaping is not theoretical: one
	/// character, one 0xFF byte. MSDP and MSSP both claim their payloads "cannot contain" IAC, and a
	/// consumer that sets this encoding makes both claims false.
	/// </summary>
	[Test]
	public async Task ASingleByteEncodingCanProduceAnIacByte()
	{
		var encoded = Encoding.Latin1.GetBytes("ÿ");

		await Assert.That(encoded).IsEquivalentTo(new byte[] { IAC }, CollectionOrdering.Matching);

		var escaped = new List<byte>();
		SubnegotiationEscaping.AppendEscaped(escaped, "ÿ", Encoding.Latin1);

		await Assert.That(escaped).IsEquivalentTo(new List<byte> { IAC, IAC }, CollectionOrdering.Matching);
	}
}
