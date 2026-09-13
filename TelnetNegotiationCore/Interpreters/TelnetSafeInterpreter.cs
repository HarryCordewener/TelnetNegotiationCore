using System.Collections.Generic;
using System;
using System.Buffers;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// The Safe Interpreter, providing ways to not crash the system when we are given a STATE we were not expecting.
/// </summary>
public partial class TelnetInterpreter
{
	/// <summary>
	/// The two-byte escaped IAC sequence (0xFF 0xFF) written between segments when escaping.
	/// Stored as a static array so it can be sliced as a <see cref="ReadOnlySpan{T}"/> and
	/// copied in a single <c>CopyTo</c> call instead of two individual byte assignments.
	/// </summary>
	private static readonly byte[] s_iacEscape = [255, 255];
	/// <summary>
	/// Create a byte[] that is safe to send over telnet by repeating 255s.
	/// This is handled automatically by <see cref="SendAsync"/> and <see cref="SendPromptAsync"/>;
	/// call it directly only when writing bytes to the peer through some other path, such as
	/// <see cref="Protocols.EchoProtocol"/>'s default handler.
	/// </summary>
	/// <param name="str">The original bytes intent to be sent.</param>
	/// <returns>The new byte[] with 255s duplicated.</returns>
	internal byte[] TelnetSafeBytes(byte[] str)
	{
		return TelnetSafeBytesInternal(str.AsSpan());
	}

	/// <summary>
	/// Internal helper to escape IAC bytes (255) without MemoryStream allocation.
	/// Uses <see cref="MemoryExtensions.IndexOf"/> for an early exit when no IAC bytes are present.
	/// On .NET 9+, <see cref="MemoryExtensions.Split"/> is enumerated once into a <see cref="List{T}"/>
	/// of <see cref="Range"/> values. The list length gives the exact count of segments, allowing a
	/// precise allocation, and then each segment is bulk-copied via <see cref="MemoryExtensions.CopyTo"/>.
	/// On earlier runtimes (where <c>Split&lt;T&gt;(T)</c> is unavailable) the same
	/// <see cref="ArrayPool{T}"/> + <c>IndexOf</c> loop with <c>CopyTo</c> block copies is used.
	/// </summary>
	private static byte[] TelnetSafeBytesInternal(ReadOnlySpan<byte> input)
	{
		// Use IndexOf for early exit - stops at first IAC byte without scanning the whole input
		if (input.IndexOf((byte)255) < 0)
		{
			return input.ToArray();
		}

#if NET9_0_OR_GREATER
		// Single pass: enumerate the SpanSplitEnumerator once into a List<Range>.
		// The list length tells us the number of segments, so we can compute the exact output
		// size (original length + one extra byte per IAC delimiter) for a precise allocation.
		// SpanSplitEnumerator<T> is a ref struct, so LINQ Select/ToArray cannot be used here.
		// Capacity hint: assume roughly one IAC per 256 bytes (IAC bytes are rare in practice).
		var ranges = new List<Range>(capacity: 1 + input.Length / 256);
		foreach (var range in input.Split((byte)255))
		{
			ranges.Add(range);
		}

		var result = new byte[input.Length + ranges.Count - 1];
		int writePos = 0;

		for (int i = 0; i < ranges.Count; i++)
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
		// Fallback for netstandard2.0 and .NET 8: ArrayPool worst-case buffer + IndexOf loop
		// with CopyTo block copies. MemoryExtensions.Split<T>(T value) returning
		// SpanSplitEnumerator<T> was introduced in .NET 9 and has no available polyfill.
		var pooled = ArrayPool<byte>.Shared.Rent(input.Length * 2);
		try
		{
			int writePos = 0;
			var remaining = input;

			while (!remaining.IsEmpty)
			{
				int iacPos = remaining.IndexOf((byte)255);
				if (iacPos < 0)
				{
					remaining.CopyTo(pooled.AsSpan(writePos));
					writePos += remaining.Length;
					break;
				}

				remaining[..iacPos].CopyTo(pooled.AsSpan(writePos));
				writePos += iacPos;

				s_iacEscape.AsSpan().CopyTo(pooled.AsSpan(writePos));
				writePos += 2;

				remaining = remaining[(iacPos + 1)..];
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
