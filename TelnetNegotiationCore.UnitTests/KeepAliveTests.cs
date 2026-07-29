using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Tests for the opt-in idle keep-alive.
/// </summary>
/// <remarks>
/// These are the only timing-dependent tests in the suite, so they are written to fail only on a
/// real defect, never on a slow or loaded machine:
/// <list type="bullet">
/// <item>Waits are <see cref="BaseTest.PollUntilAsync"/> with a generous timeout, never a fixed
/// sleep sized to the interval. A slow machine makes them take longer, not fail.</item>
/// <item>Assertions on "how many" are lower bounds ("at least one"), except where the assertion is
/// that a <b>stopped</b> keep-alive stays stopped, which only more work can break.</item>
/// <item>The idle-reset test asserts the invariant that actually defines idle-reset — a keep-alive
/// never fires sooner than the interval after the last write — rather than "no keep-alive fired
/// while I was busy". The latter is the same claim, but it goes red if the test thread is
/// descheduled for longer than the interval, in which case the connection really was idle and the
/// keep-alive was right to fire.</item>
/// <item>Every wait is expressed as a multiple of <see cref="ShortInterval"/> rather than as a
/// literal number of milliseconds, so the interval and the budgets can never drift apart.</item>
/// </list>
/// <para>
/// The behavioural tests run at <see cref="TelnetInterpreter.MinimumKeepAliveInterval"/>, which is
/// the fastest the library allows and therefore the fastest these can honestly go: the tests
/// exercise the shipped validation instead of side-stepping it, which is why there is no internal
/// test-only bypass here. Tests within a class run in parallel under TUnit, so the wall clock is
/// the longest test (~4s), not their sum.
/// </para>
/// </remarks>
public class KeepAliveTests : BaseTest
{
	private static readonly byte[] s_iacNop = [(byte)Trigger.IAC, (byte)Trigger.NOP];

	/// <summary>
	/// Shortest interval the library accepts (1 second), used by the behavioural tests. Referenced
	/// rather than hardcoded so that these tests follow the bound if it ever moves.
	/// </summary>
	private static readonly TimeSpan ShortInterval = TelnetInterpreter.MinimumKeepAliveInterval;

	/// <summary>
	/// Budget for poll-until waits that expect a single keep-alive: several times the one interval
	/// they actually need, since a poll-until returns as soon as the condition holds and only
	/// spends the full budget on a genuine failure.
	/// </summary>
	private static readonly int OneKeepAliveTimeoutMs = (int)(ShortInterval.TotalMilliseconds * 10);

	/// <summary>
	/// Budget for the one test that waits for three keep-alives in a row.
	/// </summary>
	private static readonly int ThreeKeepAlivesTimeoutMs = (int)(ShortInterval.TotalMilliseconds * 15);

	private static bool IsIacNop(byte[] data) => data.Length == 2 && data[0] == 255 && data[1] == 241;

	private static TelnetInterpreterBuilder Builder(
		Func<ReadOnlyMemory<byte>, ValueTask> onNegotiation,
		TelnetInterpreter.TelnetMode mode = TelnetInterpreter.TelnetMode.Server) =>
		new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(onNegotiation);

	[Test]
	public async Task KeepAliveIsDisabledUnlessRequested()
	{
		// Arrange
		var writes = new ConcurrentQueue<byte[]>();

		var telnet = await Builder(data =>
		{
			writes.Enqueue(data.ToArray());
			return ValueTask.CompletedTask;
		}).BuildAsync();

		// Act - no plugins and no keep-alive, so nothing at all should be written. The wait is twice
		// the shortest interval the library will accept: had a keep-alive been enabled at any legal
		// setting, at least one would have fired by now.
		await Task.Delay(ShortInterval + ShortInterval);

		// Assert
		await Assert.That(telnet.KeepAliveInterval).IsNull();
		await Assert.That(writes.Count).IsEqualTo(0);

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task WithKeepAliveDefaultsToThirtySeconds()
	{
		// Arrange & Act
		var telnet = await Builder(_ => ValueTask.CompletedTask)
			.WithKeepAlive()
			.BuildAsync();

		// Assert
		await Assert.That(TelnetInterpreter.DefaultKeepAliveInterval).IsEqualTo(TimeSpan.FromSeconds(30));
		await Assert.That(telnet.KeepAliveInterval).IsEqualTo(TimeSpan.FromSeconds(30));

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task WithKeepAliveUsesTheConfiguredInterval()
	{
		// Arrange
		var sends = 0;

		var telnet = await Builder(_ => ValueTask.CompletedTask)
			.WithKeepAlive(ShortInterval, (_, _) =>
			{
				Interlocked.Increment(ref sends);
				return ValueTask.CompletedTask;
			})
			.BuildAsync();

		// Act - three keep-alives need three intervals; the budget allows fifteen.
		var repeated = await PollUntilAsync(() => Volatile.Read(ref sends) >= 3, timeoutMs: ThreeKeepAlivesTimeoutMs);

		// Assert
		await Assert.That(telnet.KeepAliveInterval).IsEqualTo(ShortInterval);
		await Assert.That(repeated).IsTrue();

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task KeepAliveSendsIacNopByDefault()
	{
		// Arrange
		var writes = new ConcurrentQueue<byte[]>();

		var telnet = await Builder(data =>
		{
			writes.Enqueue(data.ToArray());
			return ValueTask.CompletedTask;
		}).WithKeepAlive(ShortInterval).BuildAsync();

		// Act
		var sent = await PollUntilAsync(() => !writes.IsEmpty, timeoutMs: OneKeepAliveTimeoutMs);

		// Assert
		await Assert.That(sent).IsTrue();
		writes.TryDequeue(out var first);
		await AssertByteArraysEqual(first, s_iacNop);

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task KeepAliveWorksInClientMode()
	{
		// Arrange - NOP is a plain telnet command either end may send (RFC 854), and the keep-alive
		// is opt-in, so it is not gated on mode.
		var writes = new ConcurrentQueue<byte[]>();

		var telnet = await Builder(data =>
		{
			writes.Enqueue(data.ToArray());
			return ValueTask.CompletedTask;
		}, TelnetInterpreter.TelnetMode.Client).WithKeepAlive(ShortInterval).BuildAsync();

		// Act
		var sent = await PollUntilAsync(() => !writes.IsEmpty, timeoutMs: OneKeepAliveTimeoutMs);

		// Assert
		await Assert.That(sent).IsTrue();
		writes.TryDequeue(out var first);
		await AssertByteArraysEqual(first, s_iacNop);

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task KeepAliveSendActionCanBeOverridden()
	{
		// Arrange
		var writes = new ConcurrentQueue<byte[]>();
		var custom = 0;
		TelnetInterpreter seen = null;

		var telnet = await Builder(data =>
		{
			writes.Enqueue(data.ToArray());
			return ValueTask.CompletedTask;
		}).WithKeepAlive(ShortInterval, (interpreter, _) =>
		{
			seen = interpreter;
			Interlocked.Increment(ref custom);
			return ValueTask.CompletedTask;
		}).BuildAsync();

		// Act
		var invoked = await PollUntilAsync(() => Volatile.Read(ref custom) > 0, timeoutMs: OneKeepAliveTimeoutMs);

		// Assert - the override replaces the default entirely; no IAC NOP reaches the wire.
		await Assert.That(invoked).IsTrue();
		await Assert.That(seen).IsEqualTo(telnet);
		await Assert.That(writes.Count).IsEqualTo(0);

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task OutboundTrafficResetsTheIdleWindow()
	{
		// Arrange - a deliberately long interval relative to the write cadence below.
		var interval = ShortInterval;
		const double toleranceMs = 150;

		var clock = Stopwatch.StartNew();
		long lastWriteAtMs = 0;
		var idleAtSendMs = new ConcurrentQueue<double>();

		var telnet = await Builder(_ =>
		{
			Volatile.Write(ref lastWriteAtMs, clock.ElapsedMilliseconds);
			return ValueTask.CompletedTask;
		}).WithKeepAlive(interval, (_, _) =>
		{
			// Record how long the connection had actually been silent when we fired.
			idleAtSendMs.Enqueue(clock.ElapsedMilliseconds - Volatile.Read(ref lastWriteAtMs));
			return ValueTask.CompletedTask;
		}).BuildAsync();

		// Act - stay busy for well over one interval, writing every 100ms...
		for (var i = 0; i < 12; i++)
		{
			await telnet.WriteToNetworkAsync(new byte[] { (byte)'x' });
			await Task.Delay(100);
		}

		// ...then go quiet and wait for the keep-alive that the silence must produce.
		var firedWhenIdle = await PollUntilAsync(() => !idleAtSendMs.IsEmpty, timeoutMs: OneKeepAliveTimeoutMs);

		// Assert
		await Assert.That(firedWhenIdle).IsTrue();

		// Every keep-alive - including any that fired during the busy phase because the machine
		// stalled - must have followed at least one full interval of silence. A keep-alive on a
		// timer that was not reset by the writes above would show up here as a much smaller gap.
		foreach (var idleMs in idleAtSendMs)
		{
			await Assert.That(idleMs).IsGreaterThanOrEqualTo(interval.TotalMilliseconds - toleranceMs);
		}

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task DisposalStopsTheKeepAlive()
	{
		// Arrange
		var sends = 0;

		var telnet = await Builder(_ => ValueTask.CompletedTask)
			.WithKeepAlive(ShortInterval, (_, _) =>
			{
				Interlocked.Increment(ref sends);
				return ValueTask.CompletedTask;
			})
			.BuildAsync();

		var started = await PollUntilAsync(() => Volatile.Read(ref sends) > 0, timeoutMs: OneKeepAliveTimeoutMs);
		await Assert.That(started).IsTrue();

		// Act
		var disposeClock = Stopwatch.StartNew();
		await telnet.DisposeAsync();
		disposeClock.Stop();

		var afterDispose = Volatile.Read(ref sends);
		await Task.Delay(ShortInterval + ShortInterval + ShortInterval);

		// Assert - disposal is not allowed to hang on the keep-alive, and nothing fires afterwards.
		await Assert.That(disposeClock.ElapsedMilliseconds).IsLessThan(5000);
		await Assert.That(Volatile.Read(ref sends)).IsEqualTo(afterDispose);
	}

	[Test]
	public async Task FailingKeepAliveDoesNotTakeDownTheInterpreter()
	{
		// Arrange - a send action that always throws, as a write to a vanished peer would.
		var attempts = 0;
		byte[] submitted = null;

		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, _, _) =>
			{
				submitted = data;
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.WithKeepAlive(ShortInterval, (_, _) =>
			{
				Interlocked.Increment(ref attempts);
				throw new InvalidOperationException("Simulated dead peer.");
			})
			.BuildAsync();

		var failed = await PollUntilAsync(() => Volatile.Read(ref attempts) > 0, timeoutMs: OneKeepAliveTimeoutMs);
		await Assert.That(failed).IsTrue();

		// Act - give it several more intervals' worth of time to misbehave. A keep-alive that kept
		// retrying against the dead peer would have made at least two more attempts by now.
		await Task.Delay(ShortInterval + ShortInterval + ShortInterval);

		// Assert - the failure is swallowed and logged, the keep-alive stops rather than retrying
		// against a dead peer forever, and the byte pipeline is untouched.
		await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(1);

		await InterpretAndWaitAsync(telnet, Encoding.ASCII.GetBytes("still alive\r\n"));
		await Assert.That(submitted).IsNotNull();
		await Assert.That(Encoding.ASCII.GetString(submitted)).IsEqualTo("still alive");

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task KeepAliveQueuesBehindAnInFlightWrite()
	{
		// Arrange - a negotiation callback that blocks, so a keep-alive comes due while the write
		// lock is held by someone else. It must queue, not deadlock and not re-enter.
		var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var writes = new ConcurrentQueue<byte[]>();
		var blockNext = true;

		var telnet = await Builder(async data =>
		{
			if (Volatile.Read(ref blockNext))
			{
				Volatile.Write(ref blockNext, false);
				await release.Task;
			}
			writes.Enqueue(data.ToArray());
		}).WithKeepAlive(ShortInterval).BuildAsync();

		// Act - occupy the write lock, let the keep-alive come due behind it, then release.
		var blocked = telnet.WriteToNetworkAsync(new byte[] { (byte)'x' }).AsTask();
		await Task.Delay(ShortInterval + ShortInterval);

		// Nothing got through while the lock was held: the keep-alive queued rather than
		// re-entering the write path. (Which of the two writers won the lock first is a race, so
		// this deliberately does not assert an order - only that they were serialized.)
		await Assert.That(blocked.IsCompleted).IsFalse();
		await Assert.That(writes.Count).IsEqualTo(0);

		release.SetResult(true);
		await blocked;

		// Assert - both writes complete once the blockage clears; neither deadlocked.
		var delivered = await PollUntilAsync(() => writes.Count >= 2, timeoutMs: OneKeepAliveTimeoutMs);
		await Assert.That(delivered).IsTrue();

		var nops = 0;
		foreach (var write in writes)
		{
			if (IsIacNop(write)) nops++;
		}
		await Assert.That(nops).IsGreaterThanOrEqualTo(1);

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task NonPositiveKeepAliveIntervalIsRejected()
	{
		// Arrange & Act & Assert
		var zero = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
		{
			await Task.CompletedTask;
			new TelnetInterpreterBuilder().WithKeepAlive(TimeSpan.Zero);
		});
		await Assert.That(zero!.Message).Contains("must be between");

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
		{
			await Task.CompletedTask;
			new TelnetInterpreterBuilder().WithKeepAlive(TimeSpan.FromSeconds(-1));
		});
	}

	[Test]
	public async Task IntervalBelowTheMinimumIsRejected()
	{
		// Arrange - anything under a second is a flood, not a keep-alive.
		var justUnder = TelnetInterpreter.MinimumKeepAliveInterval - TimeSpan.FromMilliseconds(1);

		// Act
		var tooSmall = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
		{
			await Task.CompletedTask;
			new TelnetInterpreterBuilder().WithKeepAlive(justUnder);
		});

		// Assert - the message has to name the bounds, or the caller cannot tell what to pass instead.
		await Assert.That(tooSmall!.Message).Contains(TelnetInterpreter.MinimumKeepAliveInterval.ToString());
		await Assert.That(tooSmall.Message).Contains(TelnetInterpreter.MaximumKeepAliveInterval.ToString());
	}

	[Test]
	public async Task IntervalAboveTheMaximumIsRejected()
	{
		// Arrange - an unbounded interval is not just useless as a keep-alive: past int.MaxValue
		// milliseconds Task.Delay itself throws, which would take out the background loop.
		var justOver = TelnetInterpreter.MaximumKeepAliveInterval + TimeSpan.FromMilliseconds(1);

		// Act
		var tooLarge = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
		{
			await Task.CompletedTask;
			new TelnetInterpreterBuilder().WithKeepAlive(justOver);
		});

		// Assert
		await Assert.That(tooLarge!.Message).Contains(TelnetInterpreter.MaximumKeepAliveInterval.ToString());

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
		{
			await Task.CompletedTask;
			new TelnetInterpreterBuilder().WithKeepAlive(TimeSpan.FromDays(365));
		});
	}

	[Test]
	public async Task TheBoundsThemselvesAreAccepted()
	{
		// Arrange - the range is inclusive at both ends, and the default has to sit inside it.
		await Assert.That(TelnetInterpreter.MinimumKeepAliveInterval).IsEqualTo(TimeSpan.FromSeconds(1));
		await Assert.That(TelnetInterpreter.MaximumKeepAliveInterval).IsEqualTo(TimeSpan.FromHours(24));
		await Assert.That(TelnetInterpreter.DefaultKeepAliveInterval)
			.IsGreaterThanOrEqualTo(TelnetInterpreter.MinimumKeepAliveInterval);
		await Assert.That(TelnetInterpreter.DefaultKeepAliveInterval)
			.IsLessThanOrEqualTo(TelnetInterpreter.MaximumKeepAliveInterval);

		// Act
		var atMinimum = await Builder(_ => ValueTask.CompletedTask)
			.WithKeepAlive(TelnetInterpreter.MinimumKeepAliveInterval)
			.BuildAsync();
		var atMaximum = await Builder(_ => ValueTask.CompletedTask)
			.WithKeepAlive(TelnetInterpreter.MaximumKeepAliveInterval)
			.BuildAsync();

		// Assert - and the maximum is short enough for Task.Delay to accept, which is the whole
		// reason there is a maximum: the loop waits on it.
		await Assert.That(atMinimum.KeepAliveInterval).IsEqualTo(TelnetInterpreter.MinimumKeepAliveInterval);
		await Assert.That(atMaximum.KeepAliveInterval).IsEqualTo(TelnetInterpreter.MaximumKeepAliveInterval);
		await Assert.That(TelnetInterpreter.MaximumKeepAliveInterval.TotalMilliseconds)
			.IsLessThanOrEqualTo((double)int.MaxValue);

		// Task.Delay is the ceiling that matters, and it is the tighter int.MaxValue-milliseconds one
		// on .NET Framework, which netstandard2.0 consumers can be running. Prove it takes the bound.
		using var cts = new CancellationTokenSource();
		var delayAtMaximum = Task.Delay(TelnetInterpreter.MaximumKeepAliveInterval, cts.Token);
		cts.Cancel();
		await Assert.That(delayAtMaximum.IsCanceled || delayAtMaximum.IsCompleted).IsTrue();

		await atMinimum.DisposeAsync();
		await atMaximum.DisposeAsync();
	}

	[Test]
	public async Task DirectlyAssignedIntervalIsValidatedOnBuild()
	{
		// Arrange - KeepAliveInterval is a public init property, so the builder is not the only way
		// in. BuildAsync has to apply the same bounds to anyone who sets it directly.
		static TelnetInterpreter WithInterval(TimeSpan interval) =>
			new(TelnetInterpreter.TelnetMode.Server, logger)
			{
				CallbackOnSubmitAsync = NoOpSubmitCallback,
				CallbackNegotiationAsync = _ => ValueTask.CompletedTask,
				KeepAliveInterval = interval
			};

		// Act & Assert
		var tooSmall = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
			await WithInterval(TimeSpan.FromMilliseconds(150)).BuildAsync());
		await Assert.That(tooSmall!.Message).Contains("must be between");

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
			await WithInterval(TimeSpan.FromDays(30)).BuildAsync());

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
			await WithInterval(TimeSpan.Zero).BuildAsync());

		// An in-range value still builds, and a null one (keep-alive off) is untouched by the check.
		var valid = await WithInterval(ShortInterval).BuildAsync();
		await Assert.That(valid.KeepAliveInterval).IsEqualTo(ShortInterval);
		await valid.DisposeAsync();
	}
}
