using System;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// The Safe Interpreter, providing ways to not crash the system when we are given a STATE we were not expecting.
/// </summary>
public partial class TelnetInterpreter
{
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
	/// RFC 854's escaping, from <see cref="Helpers.SubnegotiationEscaping"/> — the one
	/// implementation, rather than a second copy of the same loop for the text path.
	/// </summary>
	private static byte[] TelnetSafeBytesInternal(ReadOnlySpan<byte> input)
		=> Helpers.SubnegotiationEscaping.Escaped(input);
}
