using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	// Cached prompt terminators, to avoid allocating one per prompt.
	private static readonly byte[] s_endOfRecord = [(byte)Trigger.IAC, (byte)Trigger.EOR];
	private static readonly byte[] s_goAhead = [(byte)Trigger.IAC, (byte)Trigger.GA];
	private static readonly byte[] s_carriageReturnLineFeed = [(byte)'\r', (byte)'\n'];

	/// <summary>
	/// Sends a byte message as a Prompt: it is marked as the end of a prompt rather than ended with a
	/// newline, so the client can keep it on the input line.
	/// IAC bytes (255) in <paramref name="send"/> are automatically escaped.
	/// </summary>
	/// <param name="send">Byte array</param>
	/// <returns>A completed ValueTask</returns>
	public async ValueTask SendPromptAsync(byte[] send)
	{
		var safeSend = TelnetSafeBytesInternal(send);
		var terminator = PromptTerminator();

		// Pre-allocate exact-size buffer: safeSend + the two terminator bytes
		var output = new byte[safeSend.Length + terminator.Length];
		safeSend.AsSpan().CopyTo(output);
		terminator.CopyTo(output.AsSpan(safeSend.Length));
		await WriteToNetworkAsync(output);
	}

	/// <summary>
	/// The bytes that mark the end of a prompt, given what this end has negotiated.
	/// </summary>
	/// <remarks>
	/// RFC 885 End of Record is the precise marker, so it wins wherever it was negotiated. Failing
	/// that, RFC 854's Go-Ahead marks the turn, unless <em>this end's own outbound</em> Go-Ahead is
	/// suppressed -- a promise not to send it. With neither marker available a prompt cannot be
	/// distinguished from a line, so it ends as a line does, with CR LF.
	///
	/// This reads <see cref="Protocols.SuppressGoAheadProtocol.SuppressesOutboundGoAhead"/>, not
	/// <see cref="Protocols.SuppressGoAheadProtocol.IsGoAheadSuppressed"/>, which is the peer's
	/// direction -- the one that decides whether an <em>inbound</em> GA still means a prompt, not
	/// this one, and independent of it per RFC 858 §5.
	///
	/// The negotiated state belongs to the protocol plugins; the interpreter asks them for it rather
	/// than keeping a second copy that nothing updates.
	/// </remarks>
	private ReadOnlySpan<byte> PromptTerminator()
	{
		if (PluginManager?.GetPlugin<Protocols.EORProtocol>() is { IsEnabled: true, IsEOREnabled: true })
			return s_endOfRecord;

		if (PluginManager?.GetPlugin<Protocols.SuppressGoAheadProtocol>() is { IsEnabled: true, SuppressesOutboundGoAhead: true })
			return s_carriageReturnLineFeed;

		return s_goAhead;
	}

	/// <summary>
	/// Sends a byte message, adding an EOR at the end if needed.
	/// IAC bytes (255) in <paramref name="send"/> are automatically escaped.
	/// </summary>
	/// <param name="send">Byte array</param>
	/// <returns>A completed ValueTask</returns>
	public async ValueTask SendAsync(byte[] send)
	{
		var safeSend = TelnetSafeBytesInternal(send);
		// Pre-allocate exact-size buffer: safeSend + CR LF
		var output = new byte[safeSend.Length + 2];
		safeSend.AsSpan().CopyTo(output);
		output[safeSend.Length] = (byte)'\r';
		output[safeSend.Length + 1] = (byte)'\n';
		await WriteToNetworkAsync(output);
	}
}
