using System;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	/// <summary>
	/// The text of the most recent prompt: the partial line that was standing when a prompt boundary
	/// occurred, in the order the bytes arrived and undecoded.
	/// </summary>
	/// <remarks>
	/// The prompt callback carries no payload — it is a <c>Func&lt;ValueTask&gt;</c> and has been since
	/// the callback existed — because a consumer that wants the text usually already has it: the
	/// obvious way to hold a partial line is <see cref="CallbackOnByteAsync"/>, which is what
	/// SharpMUTerm does. This property is for the consumer that does not, so that draining the line
	/// buffer at a boundary does not put the text out of everyone's reach.
	/// <para>
	/// Valid to read from inside the prompt callback, on the byte-processing loop that invokes it.
	/// The setter is not synchronized, so a reader on any other thread has no guarantee against a
	/// torn value.
	/// </para>
	/// </remarks>
	public ReadOnlyMemory<byte> LastPromptBytes { get; private set; }

	/// <summary>True once a genuine <c>IAC GA</c> or <c>IAC EOR</c> prompt has fired on this connection.</summary>
	/// <remarks>
	/// The latch <see cref="Protocols.PacketPatchProtocol"/> reads. A server that marks its prompts is
	/// not a server whose prompts need guessing at, and the guess is worse than the marker wherever
	/// both are available — so the first marked prompt retires the heuristic for the rest of the
	/// connection. Mudlet's <c>mGA_Driver</c> and TinTin++'s <c>TELOPT_FLAG_PROMPT</c> are the same
	/// latch under different names.
	/// </remarks>
	internal bool HasSeenMarkedPrompt { get; private set; }

	/// <summary>True when bytes have arrived since the last line submission or prompt boundary.</summary>
	internal bool HasPartialLine => _bufferPosition > 0;

	/// <summary>
	/// Hands the standing partial line to <see cref="LastPromptBytes"/> and clears it, because a prompt
	/// boundary has just delivered those bytes.
	/// </summary>
	/// <remarks>
	/// <b>Clearing is the point.</b> Before this existed, a prompt fired and the bytes stayed in the
	/// line buffer, so the next <c>CRLF</c> submitted them as the head of a line they were never part
	/// of: a server sending <c>HP:100&gt;</c> <c>IAC GA</c> then <c>You wave.CRLF</c> produced one
	/// line reading <c>HP:100&gt;You wave.</c> — the prompt gone from where it belonged and corrupting
	/// where it landed. See PromptBoundaryTests.
	/// <para>
	/// A boundary closes the line the same way <see cref="WriteToOutput"/> does, and for the same
	/// reason: after this call no line is open, so nothing pinned or flagged about the drained bytes
	/// may survive to be read against whatever the next line turns out to be. <c>_lineEncoding</c> is
	/// cleared so the next line picks up <see cref="CurrentEncoding"/> as it stands when that line
	/// actually starts, not whatever was pinned when the prompt's bytes arrived — otherwise a CHARSET
	/// change that lands between the prompt and the next line is silently ignored and that line is
	/// delivered tagged with the wrong encoding. <c>_bufferOverflowed</c> is cleared so a line that
	/// overflowed before its boundary arrived does not make the connection drop the next, unrelated,
	/// ordinary-length line too.
	/// </para>
	/// </remarks>
	/// <param name="marked">
	/// True when a server marker (<c>IAC GA</c>, <c>IAC EOR</c>) caused this boundary; false when it
	/// was inferred from silence. Only a marked boundary sets <see cref="HasSeenMarkedPrompt"/>.
	/// </param>
	internal void TakePartialLineAsPrompt(bool marked)
	{
		LastPromptBytes = _bufferPosition == 0
			? ReadOnlyMemory<byte>.Empty
			: _buffer!.AsSpan()[.._bufferPosition].ToArray();

		_bufferPosition = 0;
		_lineEncoding = null;
		_bufferOverflowed = false;
		ReleaseLineBufferIfLarge();

		if (marked)
		{
			HasSeenMarkedPrompt = true;
		}
	}
}
