using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	/// <summary>
	/// The registered byte-processed handler, or null. See <see cref="SetByteProcessedHandler"/>.
	/// <see langword="volatile"/>: registration can run on a different thread (a plugin's
	/// disable/enable) than the byte-processing loop that reads this on every byte fired.
	/// </summary>
	private volatile Func<bool, ValueTask>? _onByteProcessed;

	/// <summary>
	/// The registered inferred-prompt handler, or null. See <see cref="SetInferredPromptHandler"/>.
	/// <see langword="volatile"/> for the same reason as <see cref="_onByteProcessed"/>.
	/// </summary>
	private volatile Func<ValueTask>? _onInferredPrompt;

	/// <summary>
	/// The text of the most recent prompt: the partial line that was standing when a prompt boundary
	/// occurred, in the order the bytes arrived and undecoded.
	/// </summary>
	/// <remarks>
	/// The prompt callback carries no payload, because a consumer that wants the text usually
	/// already has it via <see cref="CallbackOnByteAsync"/>. This property is for the consumer that
	/// does not.
	/// <para>
	/// Set only from the byte-processing loop, so reading it from inside the prompt callback (EOR,
	/// Suppress Go-Ahead or Packet Patch alike) is safe. A reader on any other thread has no
	/// synchronization against a concurrent write and may observe a torn value.
	/// </para>
	/// </remarks>
	public ReadOnlyMemory<byte> LastPromptBytes { get; private set; }

	/// <summary>True once a genuine <c>IAC GA</c> or <c>IAC EOR</c> prompt has fired on this connection.</summary>
	/// <remarks>
	/// The latch <see cref="Protocols.PacketPatchProtocol"/> reads: the first marked prompt retires
	/// its silence-inferred heuristic for the rest of the connection.
	/// </remarks>
	public bool HasSeenMarkedPrompt { get; private set; }

	/// <summary>True when bytes have arrived since the last line submission or prompt boundary.</summary>
	public bool HasPartialLine => _bufferPosition > 0;

	/// <summary>
	/// Hands the standing partial line to <see cref="LastPromptBytes"/> and clears it.
	/// </summary>
	/// <remarks>
	/// Also resets <c>_lineEncoding</c> and <c>_bufferOverflowed</c>, exactly as <c>WriteToOutput</c>
	/// does for an ordinary line, so neither survives to taint the next one. If the line had already
	/// overflowed, the reported text is truncated to what was stored before the ceiling, and this
	/// logs a warning rather than staying silent about the truncation.
	/// <para>
	/// <b>Call this only from the byte-processing loop.</b> It is unsynchronized: the line buffer
	/// has exactly one writer as long as every caller honours that. Calling it from any other thread
	/// races the loop's own writes to <c>_bufferPosition</c>, <c>_lineEncoding</c>,
	/// <c>_bufferOverflowed</c> and, via <c>ReleaseLineBufferIfLarge</c>, <c>_buffer</c> itself going
	/// null.
	/// </para>
	/// </remarks>
	/// <param name="marked">
	/// True for a server marker (<c>IAC GA</c>, <c>IAC EOR</c>); false when inferred from silence.
	/// Only a marked boundary sets <see cref="HasSeenMarkedPrompt"/>.
	/// </param>
	/// <returns>
	/// True if a prompt should be reported. Always true when <paramref name="marked"/>; for an
	/// unmarked call, false if nothing was held, in which case the call is a no-op.
	/// </returns>
	public bool TakePartialLineAsPrompt(bool marked)
	{
		if (_bufferPosition == 0 && !marked)
		{
			return false;
		}

		if (_bufferOverflowed)
		{
			_logger.LogWarning(
				"A prompt boundary arrived on a line that had already overflowed past {MaxBufferSize} bytes; the reported prompt text is truncated to what was stored before the ceiling.",
				MaxBufferSize);
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
	/// Registers the handler called after every byte the byte-processing loop fires into the state
	/// machine, with a flag saying whether the channel is empty right now. Pass null to remove it.
	/// </summary>
	/// <remarks>
	/// <see cref="Protocols.PacketPatchProtocol"/> is the only caller. Called on every byte, not
	/// only idle ones, so a sustained burst cannot hide a stale arm behind unprocessed backlog.
	/// </remarks>
	internal void SetByteProcessedHandler(Func<bool, ValueTask>? handler) => _onByteProcessed = handler;

	/// <summary>
	/// Registers the handler invoked, on the byte-processing loop, after the loop itself has taken
	/// an inferred prompt's partial line (<see cref="TakePartialLineAsPrompt"/> returned true for an
	/// unmarked call). Pass null to remove it.
	/// </summary>
	/// <remarks>
	/// <see cref="Protocols.PacketPatchProtocol"/> is the only caller: the second half of its
	/// two-part registration alongside <see cref="SetByteProcessedHandler"/>.
	/// </remarks>
	internal void SetInferredPromptHandler(Func<ValueTask>? handler) => _onInferredPrompt = handler;
}
