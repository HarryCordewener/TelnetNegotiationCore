using System;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	/// <summary>The registered idle handler, or null. See <see cref="SetByteStreamIdleHandler"/>.</summary>
	private Func<ValueTask>? _onByteStreamIdle;

	/// <summary>The registered inferred-prompt handler, or null. See <see cref="SetInferredPromptHandler"/>.</summary>
	private Func<ValueTask>? _onInferredPrompt;

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
	/// Set only from the byte-processing loop. <see cref="TakePartialLineAsPrompt"/>'s only callers are
	/// a marked prompt's state-machine entry handler (<c>EORProtocol</c>, <c>SuppressGoAheadProtocol</c>)
	/// and this loop's own handling of <c>InferredPromptSentinel</c>
	/// (<see cref="Protocols.PacketPatchProtocol"/>'s silence-inferred prompt) — both run on the loop,
	/// and so does every registered prompt callback in turn, EOR, Suppress Go-Ahead and Packet Patch
	/// alike. Reading this from inside any of those callbacks is safe. A reader on any other thread has
	/// no synchronization against a concurrent write and may observe a torn value.
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
	public bool HasSeenMarkedPrompt { get; private set; }

	/// <summary>True when bytes have arrived since the last line submission or prompt boundary.</summary>
	public bool HasPartialLine => _bufferPosition > 0;

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
	/// <para>
	/// <b>Callers, and why this stays single-threaded.</b> Every caller runs on the byte-processing
	/// loop: a marked boundary from within a state-machine entry handler (itself invoked from the loop),
	/// and a silence-inferred boundary from the loop's own handling of
	/// <c>TelnetStandardInterpreter.InferredPromptSentinel</c>. Nothing here is called from
	/// <see cref="Protocols.PacketPatchProtocol"/>'s timer thread — that timer only enqueues the
	/// sentinel (<see cref="TryEnqueueInferredPrompt"/>) and never touches the line buffer itself — so
	/// this needs no lock: the line buffer genuinely has one writer.
	/// </para>
	/// <para>
	/// <b>The false-positive case.</b> A silence-inferred call can lose a race it does not know it is
	/// in: the timer's sentinel and a genuine marker can both be in flight for the same fragment, and
	/// whichever byte the loop happens to process first wins. If the marker wins first, the buffer is
	/// already empty by the time the sentinel is processed. Returning <see langword="false"/> for that
	/// case — an unmarked call finding nothing held — is what stops it from being reported as a second,
	/// spurious prompt for a fragment the marker already delivered; see the caller in
	/// <c>TelnetStandardInterpreter.ProcessBytesAsync</c>. A marked call is never speculative in this
	/// way and always reports: a bare <c>IAC GA</c> with nothing buffered is a legitimately empty
	/// prompt, and callers such as <c>SuppressGoAheadProtocol.OnGoAheadAsync</c> rely on that.
	/// </para>
	/// </remarks>
	/// <param name="marked">
	/// True when a server marker (<c>IAC GA</c>, <c>IAC EOR</c>) caused this boundary; false when it
	/// was inferred from silence. Only a marked boundary sets <see cref="HasSeenMarkedPrompt"/>.
	/// </param>
	/// <returns>
	/// True if a prompt should be reported: always for <paramref name="marked"/>, or for an unmarked
	/// call that actually found a held fragment. False only for an unmarked call against an
	/// already-empty buffer, which the caller must not report as a prompt.
	/// </returns>
	public bool TakePartialLineAsPrompt(bool marked)
	{
		if (_bufferPosition == 0 && !marked)
		{
			return false;
		}

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

		return true;
	}

	/// <summary>
	/// Registers the handler called when the inbound byte stream has gone quiet — every queued byte
	/// processed, nothing waiting. Pass null to remove it.
	/// </summary>
	/// <remarks>
	/// One delegate rather than a plugin-manager walk, because this is checked on the byte-processing
	/// loop: a null check and an integer compare per byte is affordable, iterating every registered
	/// plugin is not. <see cref="Protocols.PacketPatchProtocol"/> is the only caller.
	/// </remarks>
	internal void SetByteStreamIdleHandler(Func<ValueTask>? handler) => _onByteStreamIdle = handler;

	/// <summary>
	/// Registers the handler invoked, on the byte-processing loop, after the loop itself has taken an
	/// inferred prompt's partial line (<see cref="TakePartialLineAsPrompt"/> returned true for an
	/// unmarked call). Pass null to remove it.
	/// </summary>
	/// <remarks>
	/// <see cref="Protocols.PacketPatchProtocol"/> is the only caller: this is the second half of its
	/// two-part registration alongside <see cref="SetByteStreamIdleHandler"/>, split because the two
	/// fire at different times for different reasons — one on every idle point to arm or re-arm the
	/// hold-time timer, this one only once, when a held fragment has actually just been taken.
	/// </remarks>
	internal void SetInferredPromptHandler(Func<ValueTask>? handler) => _onInferredPrompt = handler;
}
