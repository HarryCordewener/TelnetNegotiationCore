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
/// Infers a prompt boundary from silence, for servers that mark their prompts with nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// RFC 854 gives a server <c>IAC GA</c> and RFC 885 gives it <c>IAC EOR</c>; many MUD servers send
/// neither, ending a prompt with a bare unterminated fragment. Held against the line buffer, that
/// fragment surfaces only when the next newline arrives, glued to a line it was never part of.
/// </para>
/// <para>
/// So it is inferred: hold the fragment, and if nothing more arrives within <see cref="HoldTime"/>,
/// call it a prompt -- the same approach as Mudlet's posting timer and TinTin++'s "packet patch"
/// (500 ms default, 0-10 s range).
/// </para>
/// <para>
/// <b>Retires once not needed.</b> The first genuine <c>IAC GA</c> or <c>IAC EOR</c> on a
/// connection sets <see cref="Interpreters.TelnetInterpreter.HasSeenMarkedPrompt"/> and this plugin
/// stops firing for the rest of it.
/// </para>
/// <para>
/// <b>The timer never touches the line buffer.</b> The buffer has exactly one owner, the
/// byte-processing loop; when <see cref="HoldTime"/> elapses the timer only enqueues a sentinel
/// onto that loop's own channel (the interpreter's internal <c>TryEnqueueInferredPrompt</c>), and
/// the loop itself does the check-and-take when it dequeues it. See
/// <see cref="Interpreters.TelnetInterpreter.TakePartialLineAsPrompt"/>'s remarks for why a losing
/// race reports nothing rather than a spurious second prompt.
/// </para>
/// <para>
/// <b>Arm and disarm are byte-driven, not idle-driven.</b> Every byte the loop fires is reported to
/// <see cref="OnByteProcessedAsync"/> with a flag for whether the channel is empty right now; any
/// byte at all disarms first, and only a genuinely idle byte may re-arm, fresh from the buffer's
/// state at that instant. An idle-only notification would let an arm placed before a sustained
/// burst survive the burst's entire duration and fire behind a backlog of unrelated bytes.
/// </para>
/// <para>
/// <b>A queued callback is not a cancelled one.</b> <see cref="Timer.Change(TimeSpan, TimeSpan)"/>
/// reprograms when the timer next fires; it does nothing about a firing already dispatched to the
/// thread pool. <c>_armDeadline</c> covers that: every arm, disarm and re-arm records what the
/// current arm is, and <see cref="OnTimerElapsed"/> checks its own timestamp against the current
/// value before doing anything else, so a callback dispatched under a superseded arm drops itself.
/// </para>
/// <para>
/// This carries no <c>WILL</c>/<c>DO</c> exchange of its own -- registering it is the whole opt-in,
/// as with <see cref="MSSPPlaintextProtocol"/> -- so it reports
/// <see cref="TelnetProtocolPluginBase.IsNegotiated"/> true from initialization in client mode.
/// Server mode never negotiates at all; see <see cref="OnInitializeAsync"/>.
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
	/// activity. Armed, disarmed and re-armed by <see cref="OnByteProcessedAsync"/>.
	/// </summary>
	private readonly Timer _timer;

	/// <summary>
	/// Tolerance for <see cref="OnTimerElapsed"/>'s staleness check, absorbing clock skew between an
	/// arm's <see cref="Stopwatch"/> sample and the timer engine's own clock -- 15 ms, negligible
	/// against a <see cref="HoldTime"/> in the hundreds of milliseconds to seconds. Inert below that
	/// (zero included): a hold that short means reporting the next silence no matter how brief,
	/// which is what happens either way.
	/// </summary>
	private static readonly long ToleranceTicks = (long)(0.015 * Stopwatch.Frequency);

	/// <summary>
	/// The <see cref="Stopwatch"/> timestamp at or after which the current arm may legitimately
	/// fire, or <see cref="long.MaxValue"/> while nothing is armed. Recorded by
	/// <see cref="OnByteProcessedAsync"/> before every <see cref="Timer.Change(TimeSpan, TimeSpan)"/>
	/// call and read by <see cref="OnTimerElapsed"/> to tell a stale callback from a live one. A
	/// <c>long</c>, guarded with <c>Interlocked</c> rather than a lock: this field is the only state
	/// shared between the two threads that touch it.
	/// </summary>
	private long _armDeadline = long.MaxValue;

	/// <summary>
	/// Set at the top of <see cref="OnInitializeAsync"/>, so <see cref="OnDisposeAsync"/> can tell a
	/// plugin that was built into a connection apart from one merely constructed and never
	/// initialized -- <see cref="TelnetProtocolPluginBase.Context"/> throws for the latter.
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
	/// Zero is accepted and means immediate, not disabled: this plugin has no separate "off" value
	/// -- not registering it is that.
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
	/// The hold time is negative or above <see cref="MaximumHoldTime"/>. Rejected rather than
	/// clamped, so a mistyped value is never silently turned into a different one.
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
	/// Sets the callback invoked when a held fragment is called a prompt. Pass the same callback
	/// given to EOR and Suppress Go-Ahead.
	/// </summary>
	/// <remarks>
	/// Runs on the byte-processing loop, the same thread EOR's and Suppress Go-Ahead's prompt
	/// callbacks run on.
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
		// Nothing: this plugin's whole input is the absence of bytes, which the state machine has
		// no trigger for.
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

		Context.Interpreter.SetByteProcessedHandler(OnByteProcessedAsync);
		Context.Interpreter.SetInferredPromptHandler(FireInferredPromptCallbackAsync);
		await OnNegotiatedAsync(true);
	}

	/// <inheritdoc />
	protected override ValueTask OnDisposeAsync()
	{
		// Reset first: a callback already dispatched before Dispose may still run, and finding
		// long.MaxValue is what makes it drop itself.
		Interlocked.Exchange(ref _armDeadline, long.MaxValue);

		// Parameterless Dispose is safe to call more than once; OnTimerElapsed does no
		// asynchronous work to wait for.
		_timer.Dispose();

		// Server mode never registered anything in OnInitializeAsync.
		if (!_initialized || Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server)
		{
			return default;
		}

		Context.Interpreter.SetByteProcessedHandler(null);
		Context.Interpreter.SetInferredPromptHandler(null);
		return default;
	}

	/// <inheritdoc />
	/// <remarks>
	/// Disarming and unregistering both handlers is what stops a disabled plugin from still
	/// reporting -- an already-enqueued sentinel then finds the handler null and leaves the buffer
	/// alone.
	/// <para>
	/// Resetting <see cref="_armDeadline"/> is required: <see cref="ProtocolPluginManager.EnablePluginAsync{T}"/>
	/// is public, and <see cref="OnProtocolEnabledAsync"/> re-registers the handlers without
	/// touching this field. Left unreset, a callback dispatched before disable could pass
	/// <see cref="OnTimerElapsed"/>'s guard after a re-enable and enqueue a sentinel the freshly
	/// re-registered handler honours.
	/// </para>
	/// </remarks>
	protected override ValueTask OnProtocolDisabledAsync()
	{
		if (_initialized)
		{
			Interlocked.Exchange(ref _armDeadline, long.MaxValue);
			_timer.Change(Timeout.Infinite, Timeout.Infinite);
			Context.Interpreter.SetByteProcessedHandler(null);
			Context.Interpreter.SetInferredPromptHandler(null);
		}

		return default;
	}

	/// <inheritdoc />
	/// <remarks>
	/// Mirrors <see cref="OnProtocolDisabledAsync"/>, unless server mode or an already-latched
	/// <see cref="Interpreters.TelnetInterpreter.HasSeenMarkedPrompt"/> means the handlers should
	/// stay unregistered. Also re-evaluates the arm immediately, so a fragment held across the
	/// disable is reported rather than left waiting for the next byte to trigger a check on its own.
	/// </remarks>
	protected override ValueTask OnProtocolEnabledAsync()
	{
		if (_initialized
			&& Context.Mode != Interpreters.TelnetInterpreter.TelnetMode.Server
			&& !Context.Interpreter.HasSeenMarkedPrompt)
		{
			Context.Interpreter.SetByteProcessedHandler(OnByteProcessedAsync);
			Context.Interpreter.SetInferredPromptHandler(FireInferredPromptCallbackAsync);
			return OnByteProcessedAsync(idle: true);
		}

		return default;
	}

	/// <summary>
	/// Called after every byte the byte-processing loop fires, with <paramref name="idle"/> true
	/// when the channel is empty at that exact instant. See the type remarks, "Arm and disarm are
	/// byte-driven".
	/// </summary>
	private ValueTask OnByteProcessedAsync(bool idle)
	{
		if (Context.Interpreter.HasSeenMarkedPrompt)
		{
			Interlocked.Exchange(ref _armDeadline, long.MaxValue);
			_timer.Change(Timeout.Infinite, Timeout.Infinite);
			Context.Interpreter.SetByteProcessedHandler(null);
			Context.Interpreter.SetInferredPromptHandler(null);
			return default;
		}

		if (!idle)
		{
			// Skipped when already disarmed, so a sustained burst costs one Exchange/Change total.
			if (Interlocked.Read(ref _armDeadline) != long.MaxValue)
			{
				Interlocked.Exchange(ref _armDeadline, long.MaxValue);
				_timer.Change(Timeout.Infinite, Timeout.Infinite);
			}

			return default;
		}

		// Restart, not start: the clock runs from the last byte, not the first.
		var hasPartialLine = Context.Interpreter.HasPartialLine;
		Interlocked.Exchange(ref _armDeadline,
			hasPartialLine
				? Stopwatch.GetTimestamp() + (long)(HoldTime.TotalSeconds * Stopwatch.Frequency)
				: long.MaxValue);
		_timer.Change(hasPartialLine ? HoldTime : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		return default;
	}

	/// <summary>
	/// The <see cref="Timer"/> callback. Enqueues the sentinel the loop watches for, once
	/// <see cref="_armDeadline"/> confirms this is not a stale callback from a superseded arm.
	/// </summary>
	/// <remarks>
	/// Internal, not private, so a test can invoke it directly; reachable only from the test
	/// assembly, via this project's <c>InternalsVisibleTo</c>. <see cref="ToleranceTicks"/> covers
	/// clock skew between the deadline's <see cref="Stopwatch"/> sample and the timer engine's own
	/// clock.
	/// </remarks>
	internal void OnTimerElapsed(object? state)
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
	/// Invoked on the byte-processing loop once it has taken the partial line for an inferred
	/// prompt.
	/// </summary>
	private ValueTask FireInferredPromptCallbackAsync()
	{
		Context.Logger.LogDebug(
			"No marker after {HoldTime} of silence; treating the held fragment as a prompt.", HoldTime);

		return _onPromptReceived?.Invoke() ?? default;
	}
}
