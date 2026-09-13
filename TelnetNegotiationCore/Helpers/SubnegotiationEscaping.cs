using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace TelnetNegotiationCore.Helpers;

/// <summary>
/// RFC 854's rule about a literal 255 on the wire, in one place.
/// </summary>
/// <remarks>
/// <para>
/// RFC 854: "the 255 code" must be doubled to be sent as data. It applies to everything this library
/// writes — ordinary output and subnegotiation payloads alike — because the peer's parser makes no
/// distinction: an unescaped 255 is read as the <c>IAC</c> beginning a command, and inside a
/// subnegotiation that means the real <c>IAC SE</c> is read as two more bytes of payload and the
/// frame never closes. Several option specifications restate the rule for their own payloads --
/// RFC 1073 for NAWS ("any occurrence of 255 in the subnegotiation must be doubled"), RFC 2066 for
/// CHARSET ("all octets of value 255 (other than IAC) MUST be quoted") -- and none of them exempt
/// anything.
/// </para>
/// <para>
/// It is one rule, and this is the only implementation of it. It had been written out four separate
/// times -- here, in <c>MSSPProtocol</c>, in <c>NewEnvironProtocol</c> and in the interpreter's own
/// span-based path -- and the site that forgot it entirely (LINEMODE's <c>MODE</c> byte) is exactly
/// what a rule with four homes and no single name invites.
/// </para>
/// <para>
/// A specification saying its payload "cannot contain" <c>IAC</c> is not a reason to skip this. MSDP
/// and MSSP both say so, and both are wrong about it the moment a non-ASCII encoding turns one
/// character into 0xFF (ISO-8859-1 'ÿ'). Escaping a byte that never appears costs nothing.
/// </para>
/// </remarks>
internal static class SubnegotiationEscaping
{
	private const byte IAC = 255;

	/// <summary>
	/// The two-byte escaped <c>IAC</c> sequence, kept as an array so it can be copied in one
	/// <c>CopyTo</c> rather than two byte assignments.
	/// </summary>
	private static readonly byte[] s_iacEscape = [IAC, IAC];

	/// <summary>Appends one byte, doubled if it is <c>IAC</c>.</summary>
	internal static void AppendEscaped(List<byte> destination, byte value)
	{
		destination.Add(value);

		if (value == IAC)
		{
			destination.Add(IAC);
		}
	}

	/// <summary>Appends a payload, doubling any <c>IAC</c> among its bytes.</summary>
	internal static void AppendEscaped(List<byte> destination, IEnumerable<byte> payload)
	{
		foreach (var b in payload)
		{
			AppendEscaped(destination, b);
		}
	}

	/// <summary>
	/// Appends <paramref name="text"/> encoded with <paramref name="encoding"/>, doubling any
	/// <c>IAC</c> among the resulting bytes.
	/// </summary>
	/// <remarks>
	/// The encoding is what makes this necessary rather than theoretical: an application's string is
	/// usually plain ASCII, where 0xFF cannot occur, but a single character encodes to 0xFF under
	/// ISO-8859-1 and several other single-byte code pages.
	/// </remarks>
	internal static void AppendEscaped(List<byte> destination, string text, Encoding encoding)
		=> AppendEscaped(destination, encoding.GetBytes(text));

	/// <summary>
	/// A copy of <paramref name="input"/> with every <c>IAC</c> doubled, or the input itself when
	/// there is nothing to escape.
	/// </summary>
	/// <remarks>
	/// The array-returning form, for the paths that build a frame as a <c>byte[]</c> rather than
	/// accumulating into a list — ordinary output among them, which makes this the hot one. It exits
	/// early on <see cref="MemoryExtensions.IndexOf"/> so the overwhelmingly common case of a payload
	/// with no 255 in it costs one vectorised scan and a copy.
	/// <para>
	/// On .NET 9 and later the split enumerator is walked once into a list of ranges, whose count
	/// gives the exact output length for a single precise allocation. Earlier runtimes -- including
	/// netstandard2.0, where <c>Split&lt;T&gt;(T)</c> does not exist and has no polyfill -- use a
	/// pooled worst-case buffer and the same block copies.
	/// </para>
	/// </remarks>
	internal static byte[] Escaped(ReadOnlySpan<byte> input)
	{
		if (input.IndexOf(IAC) < 0)
		{
			return input.ToArray();
		}

#if NET9_0_OR_GREATER
		// SpanSplitEnumerator<T> is a ref struct, so it cannot be projected with LINQ.
		// Capacity hint: roughly one IAC per 256 bytes, since 255 is rare in real payloads.
		var ranges = new List<Range>(capacity: 1 + input.Length / 256);
		foreach (var range in input.Split(IAC))
		{
			ranges.Add(range);
		}

		var result = new byte[input.Length + ranges.Count - 1];
		var writePos = 0;

		for (var i = 0; i < ranges.Count; i++)
		{
			if (i > 0)
			{
				s_iacEscape.AsSpan().CopyTo(result.AsSpan(writePos));
				writePos += 2;
			}

			var segment = input[ranges[i]];
			segment.CopyTo(result.AsSpan(writePos));
			writePos += segment.Length;
		}

		return result;
#else
		var pooled = ArrayPool<byte>.Shared.Rent(input.Length * 2);
		try
		{
			var writePos = 0;
			var remaining = input;

			while (!remaining.IsEmpty)
			{
				var iacPos = remaining.IndexOf(IAC);
				if (iacPos < 0)
				{
					remaining.CopyTo(pooled.AsSpan(writePos));
					writePos += remaining.Length;
					break;
				}

				remaining.Slice(0, iacPos).CopyTo(pooled.AsSpan(writePos));
				writePos += iacPos;

				s_iacEscape.AsSpan().CopyTo(pooled.AsSpan(writePos));
				writePos += 2;

				remaining = remaining.Slice(iacPos + 1);
			}

			return pooled.AsSpan(0, writePos).ToArray();
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(pooled);
		}
#endif
	}
}
