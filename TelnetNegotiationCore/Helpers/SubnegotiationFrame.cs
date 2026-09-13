using System;

namespace TelnetNegotiationCore.Helpers;

/// <summary>
/// Builds an <c>IAC SB &lt;option&gt; &lt;command&gt; &lt;payload&gt; IAC SE</c> frame with the
/// payload escaped, for the several options that share exactly that shape.
/// </summary>
/// <remarks>
/// <para>
/// The escaping is not conditional on what the payload happens to contain. RFC 855 — the option
/// specifications standard, which governs every option rather than one of them — states it as a
/// general obligation: "if parameters in an option 'subnegotiation' include a byte with a value of
/// 255, it is necessary to double this byte in accordance the general TELNET rules." RFC 1073 calls
/// it "as required by the Telnet protocol, any occurrence of 255 in the subnegotiation", and RFC 2066
/// spells out why an implementer should not reason about whether it can happen: "since TELNET works
/// in octets, it is possible for octets of value 255 to appear 'spontaneously' when using multi-octet
/// or non-8-bit characters. All octets of value 255 (other than IAC) MUST be quoted to conform with
/// TELNET requirements."
/// </para>
/// <para>
/// That is why this builds the frame rather than each caller assembling one and deciding for itself.
/// TTYPE, TSPEED and XDISPLOC say nothing about escaping in RFC 1091, RFC 1079 and RFC 1096 — not
/// because they are exempt, but because RFC 855 already covers them — and a caller reading only its
/// own option's RFC finds no instruction to escape anything. Several of them then passed payloads
/// that could not contain a 255 given the encoder they used, which is true but is a property of a
/// line three lines above the frame, not of the frame.
/// </para>
/// <para>
/// The option and command bytes are not escaped, and must not be: they are the frame's structure
/// rather than its parameters, which is what RFC 855's rule is about. No telnet option is numbered
/// 255 — that value is <c>IAC</c> itself — and the command bytes here are each option's own defined
/// constants.
/// </para>
/// </remarks>
internal static class SubnegotiationFrame
{
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte IAC = 255;

	/// <summary>
	/// <c>IAC SB option command &lt;escaped payload&gt; IAC SE</c>.
	/// </summary>
	/// <param name="option">The option's own number, unescaped: part of the frame's structure.</param>
	/// <param name="command">The option's sub-command — <c>IS</c>, <c>ACCEPTED</c>, <c>MODE</c> and
	/// the like — unescaped for the same reason.</param>
	/// <param name="payload">The parameters, which are escaped.</param>
	internal static byte[] Build(byte option, byte command, ReadOnlySpan<byte> payload)
	{
		var escaped = SubnegotiationEscaping.Escaped(payload);

		var frame = new byte[4 + escaped.Length + 2];
		frame[0] = IAC;
		frame[1] = SB;
		frame[2] = option;
		frame[3] = command;
		escaped.AsSpan().CopyTo(frame.AsSpan(4));
		frame[frame.Length - 2] = IAC;
		frame[frame.Length - 1] = SE;

		return frame;
	}

	/// <summary>
	/// <c>IAC SB option command &lt;escaped payload&gt; IAC SE</c> for a single-byte payload — the
	/// shape LINEMODE's <c>MODE</c> needs.
	/// </summary>
	/// <remarks>
	/// Hands the byte to the overload above rather than escaping it here and passing the result on:
	/// escaping first and then framing an already-escaped payload doubles a 255 twice, which the
	/// peer reads back as one literal 255 followed by the start of a second escape.
	/// </remarks>
	internal static byte[] Build(byte option, byte command, byte payload)
		=> Build(option, command, new[] { payload });
}
