using System.Collections.Generic;

namespace TelnetNegotiationCore.Helpers;

/// <summary>
/// RFC 854's escape, for subnegotiation payloads that are arbitrary bytes rather than text.
/// </summary>
/// <remarks>
/// <para>
/// A subnegotiation ends at <c>IAC SE</c>, so a payload byte that happens to be 0xFF has to be sent
/// as <c>IAC IAC</c> or the receiver reads it as the terminator and the rest of the stream desyncs.
/// Text payloads have <c>MSSPProtocol.AppendEscaped</c>, which does the same thing after encoding;
/// this is for the ones that were never text — an ENCRYPT key id, an authentication credential.
/// </para>
/// <para>
/// Cheap to apply and impossible to skip safely: a credential blob is whatever the mechanism
/// produced, so 0xFF is not an edge case there, it is one byte in 256.
/// </para>
/// </remarks>
internal static class SubnegotiationEscaping
{
	/// <summary>The <c>IAC</c> byte, which is what has to be doubled.</summary>
	private const byte IAC = 255;

	/// <summary>Appends <paramref name="payload"/> to <paramref name="destination"/>, doubling every <c>IAC</c>.</summary>
	internal static void AppendEscaped(List<byte> destination, IEnumerable<byte> payload)
	{
		foreach (var b in payload)
		{
			destination.Add(b);

			if (b == IAC)
			{
				destination.Add(IAC);
			}
		}
	}
}
