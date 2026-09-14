using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Helpers;
using TelnetNegotiationCore.Models;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The shared <c>IAC SB &lt;option&gt; &lt;command&gt; &lt;payload&gt; IAC SE</c> builder, which is
/// where RFC 855's obligation about a 255 in a subnegotiation parameter is discharged.
/// </summary>
/// <remarks>
/// <para>
/// RFC 855 states it for every option rather than for particular ones: "if parameters in an option
/// 'subnegotiation' include a byte with a value of 255, it is necessary to double this byte in
/// accordance the general TELNET rules." RFC 1073 puts it as "as required by the Telnet protocol,
/// any occurrence of 255 in the subnegotiation", and RFC 2066 tells implementers not to reason about
/// whether it can occur: "it is possible for octets of value 255 to appear 'spontaneously' when
/// using multi-octet or non-8-bit characters. All octets of value 255 (other than IAC) MUST be
/// quoted to conform with TELNET requirements."
/// </para>
/// <para>
/// TTYPE, TSPEED, XDISPLOC and CHARSET's <c>ACCEPTED</c> and <c>REQUEST</c> all encode with
/// <see cref="Encoding.ASCII"/>, which cannot produce a 255, so routing them through this builder
/// changes nothing on the wire today. That is the point: the obligation is discharged by the code
/// that writes the frame, testably, instead of resting on a property of whichever encoder a line
/// above it happened to pick. It is tested here directly rather than through those five options,
/// because none of them can be made to carry a 255 through its public API — and the end-to-end proof
/// that a 255 built by this helper survives a real parser is
/// <see cref="LineModeEscapingTests.AModeAcknowledgementSurvivesBeingReadBack"/>, LINEMODE's
/// <c>MODE</c> byte being the one payload here that genuinely reaches 255.
/// </para>
/// </remarks>
public class SubnegotiationFrameTests
{
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte IAC = 255;

	private const byte TTYPE = 24;
	private const byte IS = 0;

	[Test]
	public async Task APayloadWithNoIacIsFramedUnchanged()
	{
		var frame = SubnegotiationFrame.Build(TTYPE, IS, "XTERM"u8.ToArray());

		byte[] expected = [IAC, SB, TTYPE, IS, .. "XTERM"u8, IAC, SE];

		await Assert.That(frame).IsEquivalentTo(expected, CollectionOrdering.Matching);
	}

	[Test]
	public async Task AnEmptyPayloadIsFramedAsJustTheStructure()
	{
		var frame = SubnegotiationFrame.Build(TTYPE, IS, Array.Empty<byte>());

		await Assert.That(frame).IsEquivalentTo(new byte[] { IAC, SB, TTYPE, IS, IAC, SE }, CollectionOrdering.Matching);
	}

	/// <summary>The rule itself: a 255 among the parameters is doubled.</summary>
	[Test]
	public async Task AnIacInThePayloadIsDoubled()
	{
		var frame = SubnegotiationFrame.Build(TTYPE, IS, new byte[] { (byte)'A', IAC, (byte)'B' });

		await Assert.That(frame).IsEquivalentTo(
			new byte[] { IAC, SB, TTYPE, IS, (byte)'A', IAC, IAC, (byte)'B', IAC, SE },
			CollectionOrdering.Matching);
	}

	[Test]
	public async Task EveryIacInThePayloadIsDoubled()
	{
		var frame = SubnegotiationFrame.Build(TTYPE, IS, new byte[] { IAC, IAC, 1, IAC });

		await Assert.That(frame).IsEquivalentTo(
			new byte[] { IAC, SB, TTYPE, IS, IAC, IAC, IAC, IAC, 1, IAC, IAC, IAC, SE },
			CollectionOrdering.Matching);
	}

	/// <summary>
	/// A payload that is nothing but a 255 is the case that would otherwise leave the frame
	/// unterminated: the peer reads the payload byte as the <c>IAC</c> opening the terminator, then
	/// the real <c>IAC SE</c> as two more payload bytes.
	/// </summary>
	[Test]
	public async Task APayloadOfOneIacStillTerminates()
	{
		var frame = SubnegotiationFrame.Build(TTYPE, IS, new byte[] { IAC });

		await Assert.That(frame).IsEquivalentTo(
			new byte[] { IAC, SB, TTYPE, IS, IAC, IAC, IAC, SE }, CollectionOrdering.Matching);

		// The terminator is the last two bytes and is preceded by an even number of IACs, so the
		// peer's parser sees the payload's 255 resolve before the terminator begins.
		var payloadIacs = frame.Skip(4).Take(frame.Length - 6).Count(b => b == IAC);
		await Assert.That(payloadIacs % 2).IsEqualTo(0);
	}

	/// <summary>
	/// The option and command bytes are the frame's structure, not its parameters, so they are not
	/// escaped — RFC 855's rule is about parameters, no option is numbered 255, and doubling a
	/// command byte would make the frame unreadable.
	/// </summary>
	[Test]
	public async Task TheOptionAndCommandBytesAreNotEscaped()
	{
		// 254 and 250 are DONT and SB as verbs; as an option number and a sub-command they are
		// ordinary values and must be passed through.
		var frame = SubnegotiationFrame.Build(254, 250, Array.Empty<byte>());

		await Assert.That(frame).IsEquivalentTo(
			new byte[] { IAC, SB, 254, 250, IAC, SE }, CollectionOrdering.Matching);
	}

	/// <summary>The single-byte overload must agree with the span one, for all 256 values.</summary>
	[Test]
	public async Task TheSingleByteOverloadAgreesWithTheSpanOverload()
	{
		for (var value = 0; value <= 255; value++)
		{
			var one = SubnegotiationFrame.Build(TTYPE, IS, (byte)value);
			var span = SubnegotiationFrame.Build(TTYPE, IS, new[] { (byte)value });

			await Assert.That(one).IsEquivalentTo(span, CollectionOrdering.Matching)
				.Because($"payload byte {value}");
		}
	}

	/// <summary>
	/// The five options that route through this builder still put the same bytes on the wire as
	/// before, which is what makes the change safe to fold in: their payloads are ASCII, so the
	/// escaping finds nothing to do.
	/// </summary>
	[Test]
	[Arguments("XTERM-256COLOR")]
	[Arguments("38400,38400")]
	[Arguments("host.example.org:0.1")]
	[Arguments(";utf-8;iso-8859-1;us-ascii")]
	public async Task AnAsciiPayloadIsFramedByteForByteAsAHandBuiltFrameWas(string payload)
	{
		var encoded = Encoding.ASCII.GetBytes(payload);

		var built = SubnegotiationFrame.Build(TTYPE, IS, encoded);
		byte[] byHand = [IAC, SB, TTYPE, IS, .. encoded, IAC, SE];


		await Assert.That(built).IsEquivalentTo(byHand, CollectionOrdering.Matching);
	}
}
