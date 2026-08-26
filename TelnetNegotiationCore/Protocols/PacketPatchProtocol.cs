using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Attributes;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// Infers a prompt boundary from silence, for the many servers that mark their prompts with nothing
/// at all.
/// </summary>
/// <remarks>
/// <para>
/// RFC 854 gives a server <c>IAC GA</c> and RFC 885 gives it <c>IAC EOR</c>, and a great many MUD
/// servers send neither: measured on 2026-08-26, starwars.d20mud.com and tdome.nukefire.org each
/// complete a thirty-odd exchange handshake, offer neither option, ignore an unsolicited
/// <c>IAC DO EOR</c> outright, and end their login screen with a bare unterminated fragment. Held
/// against the line buffer, that fragment surfaces only when the *next* newline arrives, glued to the
/// head of a line it was never part of.
/// </para>
/// <para>
/// So it is inferred: hold the fragment, and if nothing more arrives within <see cref="HoldTime"/>,
/// call it a prompt. The two clients these servers recommend both do exactly this — Mudlet's posting
/// timer (<c>ctelnet.h</c>, <c>mTimeOut = 300</c>) and TinTin++'s "packet patch"
/// (<c>#config {PACKET PATCH}</c>, 500 ms, settable 0–10 s), whose name is the honest one: the
/// problem being patched is TCP fragmentation, and a prompt is only the case where the fragment turns
/// out to have been the end of the server's turn.
/// </para>
/// <para>
/// <b>A guess, and it retires as soon as it is not needed.</b> The first genuine <c>IAC GA</c> or
/// <c>IAC EOR</c> on a connection sets <see cref="IProtocolContext.HasSeenMarkedPrompt"/> and this
/// plugin stops firing for the rest of it. A server that marks its prompts is never second-guessed.
/// </para>
/// <para>
/// This carries no <c>WILL</c>/<c>DO</c> exchange of its own — registering it is the whole opt-in, as
/// with <see cref="MSSPPlaintextProtocol"/> — so it reports <see cref="TelnetProtocolPluginBase.IsNegotiated"/>
/// true from initialization rather than waiting for a handshake it does not have.
/// </para>
/// </remarks>
[RequiredMethod("OnPrompt", Description = "Configure the callback to handle inferred prompt events")]
public class PacketPatchProtocol : TelnetProtocolPluginBase
{
	/// <summary>TinTin++'s default, and this one: 500 milliseconds.</summary>
	public static readonly TimeSpan DefaultHoldTime = TimeSpan.FromMilliseconds(500);

	/// <summary>The longest hold this plugin accepts, matching TinTin++'s own ceiling: 10 seconds.</summary>
	public static readonly TimeSpan MaximumHoldTime = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Fires <see cref="FireAsync"/> once <see cref="HoldTime"/> has elapsed with no intervening
	/// activity. Armed and re-armed by <see cref="OnByteStreamIdleAsync"/>, which runs on the
	/// byte-processing loop; the callback itself runs on the thread pool.
	/// </summary>
	private readonly Timer _timer;

	/// <summary>
	/// The still-running <see cref="FireAsync"/> invocation, if the timer has fired and not yet
	/// finished. Assigned synchronously inside the timer callback before that callback returns, which
	/// is what lets <see cref="OnDisposeAsync"/> wait for it: disposing <see cref="_timer"/> waits for
	/// its (synchronous) callback delegate to return, and by then this field names whatever work that
	/// callback started.
	/// </summary>
	private Task? _pendingFire;

	/// <summary>Guards against two overlapping <see cref="FireAsync"/> bodies. See its own remarks.</summary>
	private int _firing;

	private Func<ValueTask>? _onPromptReceived;

	/// <summary>Creates the plugin with <see cref="DefaultHoldTime"/>.</summary>
	public PacketPatchProtocol()
	{
		_timer = new Timer(OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
	}

	/// <summary>How long an unterminated fragment is held before it is called a prompt.</summary>
	public TimeSpan HoldTime { get; private set; } = DefaultHoldTime;

	/// <inheritdoc />
	public override Type ProtocolType => typeof(PacketPatchProtocol);

	/// <inheritdoc />
	public override string ProtocolName => "Packet Patch";

	/// <inheritdoc />
	public override IReadOnlyCollection<Type> Dependencies => Array.Empty<Type>();

	/// <summary>
	/// Sets how long to hold an unterminated fragment before calling it a prompt.
	/// </summary>
	/// <param name="holdTime">A duration in [0, <see cref="MaximumHoldTime"/>]</param>
	/// <returns>This instance for fluent chaining</returns>
	/// <exception cref="ArgumentOutOfRangeException">
	/// The hold time is negative or above <see cref="MaximumHoldTime"/>. Rejected rather than clamped,
	/// so a mistyped value is never silently turned into a different one.
	/// </exception>
	public PacketPatchProtocol WithHoldTime(TimeSpan holdTime)
	{
		if (holdTime < TimeSpan.Zero || holdTime > MaximumHoldTime)
		{
			throw new ArgumentOutOfRangeException(
				nameof(holdTime), holdTime,
				$"The packet-patch hold time must be between zero and {MaximumHoldTime}.");
		}

		HoldTime = holdTime;
		return this;
	}

	/// <summary>
	/// Sets the callback invoked when a held fragment is called a prompt. Pass the same callback the
	/// EOR and Suppress Go-Ahead plugins were given: a consumer wants one prompt notification, not one
	/// per way of detecting one.
	/// </summary>
	/// <param name="callback">The callback to handle prompts</param>
	/// <returns>This instance for fluent chaining</returns>
	public PacketPatchProtocol OnPrompt(Func<ValueTask>? callback)
	{
		_onPromptReceived = callback;
		return this;
	}

	/// <inheritdoc />
	public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
	{
		// Nothing. This plugin reads no telnet command and answers none; its whole input is the
		// absence of bytes, which the state machine has no trigger for.
	}

	/// <inheritdoc />
	protected override async ValueTask OnInitializeAsync()
	{
		if (Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			Context.Logger.LogInformation("Packet Patch is client-only; not arming in server mode.");
			return;
		}

		Context.Logger.LogInformation(
			"Packet Patch initialized: an unterminated fragment becomes a prompt after {HoldTime}.", HoldTime);

		Context.Interpreter.SetByteStreamIdleHandler(OnByteStreamIdleAsync);
		await OnNegotiatedAsync(true);
	}

	/// <inheritdoc />
	/// <remarks>
	/// Disposing <see cref="_timer"/> only waits for its own (synchronous) callback delegate to
	/// return, and that delegate hands the real work to <see cref="_pendingFire"/> and returns
	/// immediately — so waiting on the timer alone is not enough to guarantee <see cref="FireAsync"/>
	/// has finished. Awaiting <see cref="_pendingFire"/> afterwards closes that gap: it is assigned
	/// before the callback delegate returns, so by the time the timer has finished disposing, the
	/// field names whatever fire is (or was) in flight.
	/// </remarks>
	protected override async ValueTask OnDisposeAsync()
	{
		Context.Interpreter.SetByteStreamIdleHandler(null);

#if NET6_0_OR_GREATER
		await _timer.DisposeAsync();
#else
		using var disposed = new ManualResetEventSlim(false);
		_timer.Dispose(disposed.WaitHandle);
		disposed.Wait();
#endif

		if (_pendingFire is { } pending)
		{
			await pending.ConfigureAwait(false);
		}
	}

	private ValueTask OnByteStreamIdleAsync()
	{
		if (Context.HasSeenMarkedPrompt)
		{
			_timer.Change(Timeout.Infinite, Timeout.Infinite);
			Context.Interpreter.SetByteStreamIdleHandler(null);
			return default;
		}

		// Restart, not start: a fragment arriving in two TCP segments is one fragment, and the clock
		// runs from the last byte rather than the first.
		_timer.Change(Context.HasPartialLine ? HoldTime : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		return default;
	}

	/// <summary>
	/// The <see cref="Timer"/> callback. Synchronous and immediate on purpose: the real work is
	/// handed to <see cref="FireAsync"/> and tracked in <see cref="_pendingFire"/> rather than awaited
	/// here, because a <see cref="TimerCallback"/> has no way to signal "still running" to a disposer
	/// other than by not having returned yet, and this method must return quickly regardless.
	/// </summary>
	private void OnTimerElapsed(object? state) => _pendingFire = FireAsync().AsTask();

	private async ValueTask FireAsync()
	{
		// The timer callback runs on the thread pool while the byte loop may be re-arming it. Only
		// one firing may run the check-and-take below, or two prompts can be reported for one
		// fragment.
		if (Interlocked.Exchange(ref _firing, 1) == 1)
		{
			return;
		}

		try
		{
			if (!IsEnabled || Context.HasSeenMarkedPrompt || !Context.HasPartialLine)
			{
				return;
			}

			// Context.TakePartialLineAsPrompt is the interpreter's one seam this plugin shares with a
			// thread it does not own: a genuine IAC GA/EOR can drain the same fragment on the byte loop
			// at the same moment this timer decides to. The interpreter's own lock over the line buffer
			// (see TelnetStandardInterpreter._bufferLock) is what keeps that from tearing the buffer;
			// what it cannot do is stop this call from losing the race entirely and taking an already-
			// emptied buffer. Checking the result before invoking the callback is what stops that loss
			// from being reported as a second prompt for a fragment the marked path already delivered.
			Context.TakePartialLineAsPrompt(marked: false);

			if (Context.Interpreter.LastPromptBytes.Length == 0)
			{
				return;
			}

			Context.Logger.LogDebug("No marker after {HoldTime} of silence; treating the held fragment as a prompt.", HoldTime);

			if (_onPromptReceived is not null)
			{
				await _onPromptReceived().ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			// A throw here is on a timer thread with nobody to catch it, which would take the process
			// down for a prompt.
			Context.Logger.LogError(ex, "The packet-patch prompt callback threw.");
		}
		finally
		{
			Interlocked.Exchange(ref _firing, 0);
		}
	}
}
