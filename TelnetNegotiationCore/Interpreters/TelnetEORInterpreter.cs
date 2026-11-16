using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TelnetNegotiationCore.Helpers;
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
	/// <remarks>
	/// The marker is <c>IAC EOR</c> when this end has agreed to send EOR, otherwise <c>IAC GA</c>,
	/// otherwise CR LF when this end has agreed to suppress its own Go-Ahead.
	/// </remarks>
	/// <param name="send">Byte array</param>
	/// <returns>A completed ValueTask</returns>
	public async ValueTask SendPromptAsync(byte[] send)
	{
		var terminator = PromptTerminator();

		// One exact-size buffer, escaped straight into: the text with IACs doubled, then the marker.
		var output = new byte[SubnegotiationEscaping.EscapedLength(send) + terminator.Length];
		var written = SubnegotiationEscaping.CopyEscaped(send, output);
		terminator.CopyTo(output.AsSpan(written));
		await WriteToNetworkAsync(output);
	}

	/// <summary>
	/// The bytes that mark the end of a prompt, given what this end has negotiated.
	/// </summary>
	/// <remarks>
	/// RFC 885 End of Record is the precise marker, so it wins wherever <em>this end</em> has agreed
	/// to send it. Failing that, RFC 854's Go-Ahead marks the turn, unless <em>this end's own
	/// outbound</em> Go-Ahead is suppressed -- a promise not to send it. With neither marker
	/// available a prompt cannot be distinguished from a line, so it ends as a line does, with CR LF.
	///
	/// Both readings are this end's own direction, and for the same reason. This reads
	/// <see cref="Protocols.EORProtocol.MarksOutboundRecords"/>, not
	/// <see cref="Protocols.EORProtocol.PeerMarksRecords"/>, because RFC 885 negotiates EOR
	/// "independently for each direction" and a peer's <c>WILL EOR</c> says only that the peer will
	/// send the marker -- it grants no permission to send one back. It likewise reads
	/// <see cref="Protocols.SuppressGoAheadProtocol.SuppressesOutboundGoAhead"/>, not
	/// <see cref="Protocols.SuppressGoAheadProtocol.IsGoAheadSuppressed"/>, which is the peer's
	/// direction -- the one that decides whether an <em>inbound</em> GA still means a prompt, not
	/// this one, and independent of it per RFC 858 §5.
	///
	/// The negotiated state belongs to the protocol plugins; the interpreter asks them for it rather
	/// than keeping a second copy that nothing updates.
	/// </remarks>
	private ReadOnlySpan<byte> PromptTerminator()
	{
		if (PluginManager?.GetPlugin<Protocols.EORProtocol>() is { IsEnabled: true, MarksOutboundRecords: true })
			return s_endOfRecord;

		if (PluginManager?.GetPlugin<Protocols.SuppressGoAheadProtocol>() is { IsEnabled: true, SuppressesOutboundGoAhead: true })
			return s_carriageReturnLineFeed;

		return s_goAhead;
	}

	/// <summary>
	/// Sends a byte message as a line: IAC bytes (255) in <paramref name="send"/> are escaped, and
	/// CR LF is appended.
	/// </summary>
	/// <remarks>
	/// The CR LF is appended unconditionally, so pass the line without its terminator.
	/// <c>hello\r\n</c> goes out as <c>hello\r\n\r\n</c>, and an empty array sends a bare CR LF.
	/// Use <see cref="SendPromptAsync"/> for text that should end with a prompt marker instead.
	/// </remarks>
	/// <param name="send">The line's bytes, without a line terminator</param>
	/// <returns>A completed ValueTask</returns>
	public async ValueTask SendAsync(byte[] send)
	{
		// One exact-size buffer, escaped straight into: the line with IACs doubled, then CR LF.
		var output = new byte[SubnegotiationEscaping.EscapedLength(send) + 2];
		var written = SubnegotiationEscaping.CopyEscaped(send, output);
		output[written] = (byte)'\r';
		output[written + 1] = (byte)'\n';
		await WriteToNetworkAsync(output);
	}

	/// <summary>
	/// Sends several lines as one write: each escaped and ended with CR LF, exactly as
	/// <see cref="SendAsync"/> would send it, but flushed once for the lot.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Every write goes to the transport, and through MCCP when it is on, as its own flush. A room
	/// description sent a line at a time is that many syscalls and that many deflate flushes; sent
	/// through this it is one of each. Deflate also compresses a batch better than lines flushed one
	/// by one: about 11:1 against 6:1 for ten typical MUD lines per flush.
	/// </para>
	/// <para>
	/// Nothing else can be written between the lines, since they are one write.
	/// </para>
	/// </remarks>
	/// <param name="lines">The lines' bytes, each without a line terminator.</param>
	/// <returns>A ValueTask that completes once the batch has been written.</returns>
	public async ValueTask SendLinesAsync(IReadOnlyList<byte[]> lines)
	{
		if (lines is null) throw new ArgumentNullException(nameof(lines));
		if (lines.Count == 0) return;

		var length = 0;
		for (var i = 0; i < lines.Count; i++)
		{
			length += SubnegotiationEscaping.EscapedLength(lines[i] ?? throw new ArgumentException("A line is null.", nameof(lines))) + 2;
		}

		var output = new byte[length];
		var written = 0;
		for (var i = 0; i < lines.Count; i++)
		{
			written += SubnegotiationEscaping.CopyEscaped(lines[i], output.AsSpan(written));
			output[written++] = (byte)'\r';
			output[written++] = (byte)'\n';
		}

		await WriteToNetworkAsync(output);
	}
}
