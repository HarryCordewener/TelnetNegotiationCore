using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// <c>IAC EOR</c> on a connection sets <see cref="Interpreters.TelnetInterpreter.HasSeenMarkedPrompt"/>
/// and this plugin stops firing for the rest of it. A server that marks its prompts is never
/// second-guessed.
/// </para>
/// <para>
/// <b>The timer never touches the line buffer.</b> It runs on the thread pool, and the line buffer
/// has exactly one owner: the interpreter's byte-processing loop. So the timer's only job, when
/// <see cref="HoldTime"/> elapses, is to enqueue a sentinel onto that same loop's own channel
/// (the interpreter's internal <c>TryEnqueueInferredPrompt</c>) and get out of the way — the actual
/// check-and-take runs on the loop itself, the moment it dequeues that sentinel. See
/// <c>TelnetStandardInterpreter.ProcessBytesAsync</c> and
/// <see cref="Interpreters.TelnetInterpreter.TakePartialLineAsPrompt"/>'s remarks for the rest of the
/// story, including why a losing race reports nothing rather than a spurious second prompt.
/// </para>
/// <para>
/// <b>A queued callback is not a cancelled one.</b> <see cref="Timer.Change(TimeSpan, TimeSpan)"/>
/// reprograms when the timer next fires; it does nothing about a firing the runtime has already
/// dispatched to the thread pool and simply not yet run. Left unguarded, that gap admits exactly one
/// bad interleaving: the timer expires with nothing queued yet, new bytes arrive and extend the same
/// fragment before the queued callback gets a turn to run, and that stale callback then enqueues a
/// sentinel the loop honours -- reporting a prompt that eats the head of whatever just arrived, and
/// leaving the rest of it to submit as a truncated line. <c>_armDeadline</c> closes it: every
/// (re)arm records when it is legitimately allowed to fire, and <see cref="OnTimerElapsed"/> checks
/// its own timestamp against the *current* value of that field, not whichever one was in force when
/// it was scheduled, before it does anything else. A stale callback always finds a deadline a re-arm
/// has since pushed later and drops itself; the re-armed timer's own future fire is untouched.
/// </para>
/// <para>
/// This carries no <c>WILL</c>/<c>DO</c> exchange of its own — registering it is the whole opt-in, as
/// with <see cref="MSSPPlaintextProtocol"/> — so it reports
/// <see cref="TelnetProtocolPluginBase.IsNegotiated"/> true from initialization in client mode,
/// rather than waiting for a handshake it does not have. Server mode never negotiates at all; see
/// <see cref="OnInitializeAsync"/>.
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
	/// Fires <see cref="OnTimerElapsed"/> once <see cref="HoldTime"/> has elapsed with no intervening
	/// activity. Armed and re-armed by <see cref="OnByteStreamIdleAsync"/>, which runs on the
	/// byte-processing loop; the callback itself runs on the thread pool, but does no more than
	/// enqueue a sentinel — see the type remarks — so disposing it needs no special handling beyond
	/// the ordinary parameterless <see cref="Timer.Dispose()"/>.
	/// </summary>
	private readonly Timer _timer;

	/// <summary>
	/// .NET/Windows timer resolution is commonly quoted at roughly 15 ms; a fire that lands a few
	/// milliseconds ahead of its own recorded deadline is ordinary scheduling jitter, not a stale
	/// callback, and must not be dropped as one. Chosen at that scale deliberately: smaller risks
	/// treating a legitimate fire as stale, and this plugin's shortest realistic
	/// <see cref="HoldTime"/> is still hundreds of milliseconds, so 15 ms costs it nothing worth
	/// noticing at the other end.
	/// </summary>
	private static readonly long ToleranceTicks = (long)(0.015 * Stopwatch.Frequency);

	/// <summary>
	/// The <see cref="Stopwatch"/> timestamp at or after which the current arm is legitimately
	/// allowed to fire, or <see cref="long.MaxValue"/> while nothing is armed. Recorded by
	/// <see cref="OnByteStreamIdleAsync"/> immediately before every <see cref="Timer.Change(TimeSpan, TimeSpan)"/>
	/// call, and read back by <see cref="OnTimerElapsed"/> to tell a stale callback from a live one --
	/// see the type remarks, "A queued callback is not a cancelled one". A <c>long</c> rather than a
	/// <see cref="TimeSpan"/>/<see cref="DateTime"/> pairing needs no lock of its own: this field is
	/// the entire piece of state shared between the two threads that touch it, and plain
	/// <c>Interlocked.Exchange</c>/<c>Interlocked.Read</c> are all a single 64-bit field ever needs,
	/// on the 32-bit runtimes this library still targets included.
	/// </summary>
	private long _armDeadline = long.MaxValue;

	/// <summary>
	/// Set at the top of <see cref="OnInitializeAsync"/>, so <see cref="OnDisposeAsync"/> can tell
	/// apart a plugin that was genuinely built into a connection from one that was merely constructed
	/// (both of this class's <c>WithHoldTime</c>/<c>HoldTime</c> tests do exactly that) and never
	/// initialized — <see cref="TelnetProtocolPluginBase.Context"/> throws for the latter, and
	/// <c>ProtocolPluginManager.DisposeAllAsync</c> has no try/catch around each plugin's disposal, so
	/// one throwing here would abandon every other plugin's cleanup along with its own.
	/// </summary>
	private bool _initialized;

	private Func<ValueTask>? _onPromptReceived;

	/// <summary>Creates the plugin with <see cref="DefaultHoldTime"/>.</summary>
	public PacketPatchProtocol()
	{
		_timer = new Timer(OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
	}

	/// <summary>
	/// How long an unterminated fragment is held before it is called a prompt.
	/// </summary>
	/// <remarks>
	/// Zero is accepted and means immediate, not disabled: unlike TinTin++'s own <c>0</c>, which turns
	/// packet patch off there, this plugin has no separate "off" value — not registering it is that.
	/// </remarks>
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
	/// <remarks>
	/// Runs on the byte-processing loop — the same thread every registered prompt callback runs on,
	/// EOR's and Suppress Go-Ahead's included, so a handler shared across all three (as
	/// <c>AddDefaultMUDProtocols</c> does by default) needs no thread-safety of its own on that
	/// account.
	/// </remarks>
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
		_initialized = true;

		if (Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			Context.Logger.LogInformation("Packet Patch is client-only; not arming in server mode.");
			return;
		}

		Context.Logger.LogInformation(
			"Packet Patch initialized: an unterminated fragment becomes a prompt after {HoldTime}.", HoldTime);

		Context.Interpreter.SetByteStreamIdleHandler(OnByteStreamIdleAsync);
		Context.Interpreter.SetInferredPromptHandler(FireInferredPromptCallbackAsync);
		await OnNegotiatedAsync(true);
	}

	/// <inheritdoc />
	protected override ValueTask OnDisposeAsync()
	{
		// Ordinary parameterless Dispose is enough here, and safe to call more than once: the timer
		// callback (OnTimerElapsed) does no asynchronous work of its own to wait for -- it only
		// enqueues a sentinel, synchronously, and returns. See the type remarks. Disposed even for a
		// plugin that was constructed but never initialized (nothing is scheduled against
		// Timeout.Infinite in that case, but there is still no reason to leave the Timer undisposed).
		_timer.Dispose();

		if (!_initialized)
		{
			return default;
		}

		Context.Interpreter.SetByteStreamIdleHandler(null);
		Context.Interpreter.SetInferredPromptHandler(null);
		return default;
	}

	/// <inheritdoc />
	/// <remarks>
	/// Disarming the timer and unregistering both handlers is what stops a disabled plugin from
	/// still reporting inferred prompts -- mirrors the latch handling in
	/// <see cref="OnByteStreamIdleAsync"/>, which does the same thing for the same reason when a
	/// marked prompt retires the heuristic. Nothing in the byte-processing loop has to know about
	/// <see cref="TelnetProtocolPluginBase.IsEnabled"/> this way: an already-enqueued sentinel finds
	/// the handler null and leaves the line buffer alone, rather than draining it for a report a
	/// disabled plugin should not be making. See <c>TelnetStandardInterpreter.ProcessBytesAsync</c>'s
	/// handling of the sentinel.
	/// </remarks>
	protected override ValueTask OnProtocolDisabledAsync()
	{
		if (_initialized)
		{
			_timer.Change(Timeout.Infinite, Timeout.Infinite);
			Context.Interpreter.SetByteStreamIdleHandler(null);
			Context.Interpreter.SetInferredPromptHandler(null);
		}

		return default;
	}

	/// <inheritdoc />
	/// <remarks>
	/// The mirror image of <see cref="OnProtocolDisabledAsync"/>: re-registers both handlers, same as
	/// <see cref="OnInitializeAsync"/> originally did, unless server mode or an already-latched
	/// <see cref="Interpreters.TelnetInterpreter.HasSeenMarkedPrompt"/> means they should stay
	/// unregistered.
	/// </remarks>
	protected override ValueTask OnProtocolEnabledAsync()
	{
		if (_initialized
			&& Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server
			&& !Context.Interpreter.HasSeenMarkedPrompt)
		{
			Context.Interpreter.SetByteStreamIdleHandler(OnByteStreamIdleAsync);
			Context.Interpreter.SetInferredPromptHandler(FireInferredPromptCallbackAsync);
		}

		return default;
	}

	private ValueTask OnByteStreamIdleAsync()
	{
		if (Context.Interpreter.HasSeenMarkedPrompt)
		{
			Interlocked.Exchange(ref _armDeadline, long.MaxValue);
			_timer.Change(Timeout.Infinite, Timeout.Infinite);
			Context.Interpreter.SetByteStreamIdleHandler(null);
			Context.Interpreter.SetInferredPromptHandler(null);
			return default;
		}

		// Restart, not start: a fragment arriving in two TCP segments is one fragment, and the clock
		// runs from the last byte rather than the first. The deadline is recorded before the timer is
		// (re)armed, and under a lock-free Exchange rather than a plain write, so OnTimerElapsed can
		// never observe a due time that has moved without also observing the deadline that goes with
		// it -- see _armDeadline's own remarks and the type remarks, "A queued callback is not a
		// cancelled one".
		var hasPartialLine = Context.Interpreter.HasPartialLine;
		Interlocked.Exchange(ref _armDeadline,
			hasPartialLine
				? Stopwatch.GetTimestamp() + (long)(HoldTime.TotalSeconds * Stopwatch.Frequency)
				: long.MaxValue);
		_timer.Change(hasPartialLine ? HoldTime : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		return default;
	}

	/// <summary>
	/// The <see cref="Timer"/> callback. Does no buffer work of its own -- see the type remarks on why
	/// that must run on the byte-processing loop instead -- so all it does is ask that loop to do it,
	/// by enqueueing the sentinel it watches for -- and only once it has confirmed, against
	/// <see cref="_armDeadline"/>, that it is not a stale callback the runtime queued under an arm a
	/// later re-arm has since superseded. See the type remarks, "A queued callback is not a cancelled
	/// one", and <see cref="ToleranceTicks"/> for why the comparison is not a strict less-than.
	/// </summary>
	private void OnTimerElapsed(object? state)
	{
		if (Stopwatch.GetTimestamp() < Interlocked.Read(ref _armDeadline) - ToleranceTicks)
		{
			return;
		}

		if (!Context.Interpreter.TryEnqueueInferredPrompt())
		{
			Context.Logger.LogWarning(
				"Packet Patch could not enqueue an inferred prompt: the connection's byte channel is full or already closed.");
		}
	}

	/// <summary>
	/// Test-only seam for <see cref="OnTimerElapsed"/>'s staleness check. Nothing can make the thread
	/// pool actually stall a queued callback on demand, so a test proving a stale fire drops itself
	/// invokes this directly -- immediately after a re-arm, before the real due time -- rather than
	/// trying to reproduce the race with a genuine <see cref="Timer"/>.
	/// </summary>
	internal void SimulateTimerElapsedForTests() => OnTimerElapsed(null);

	/// <summary>
	/// Invoked by the byte-processing loop, on the loop itself, once it has already taken the partial
	/// line for an inferred prompt. See <c>TelnetStandardInterpreter.ProcessBytesAsync</c>'s handling
	/// of the sentinel <see cref="OnTimerElapsed"/> enqueues, which calls this only when there was
	/// something to report.
	/// </summary>
	private ValueTask FireInferredPromptCallbackAsync()
	{
		Context.Logger.LogDebug(
			"No marker after {HoldTime} of silence; treating the held fragment as a prompt.", HoldTime);

		return _onPromptReceived?.Invoke() ?? default;
	}
}
