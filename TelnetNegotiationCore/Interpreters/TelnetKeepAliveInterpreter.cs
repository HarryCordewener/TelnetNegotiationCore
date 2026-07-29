using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	/// <summary>
	/// The interval used when the keep-alive is enabled without an explicit interval: 30 seconds.
	/// </summary>
	public static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(30);

	/// <summary>
	/// The shortest accepted keep-alive interval: 1 second.
	/// </summary>
	/// <remarks>
	/// Below a second a "keep-alive" is a flood, not a keep-alive. Nothing it exists to defeat —
	/// NAT table eviction, load balancer idle timeouts, <c>tcp_keepalive_time</c> — is measured in
	/// anything shorter than seconds, so a sub-second interval buys no extra protection and only
	/// spends bandwidth, wakeups and (for MUD clients that log raw protocol) log volume.
	/// </remarks>
	public static readonly TimeSpan MinimumKeepAliveInterval = TimeSpan.FromSeconds(1);

	/// <summary>
	/// The longest accepted keep-alive interval: 24 hours.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This bound is not merely advisory. The loop waits with <see cref="Task.Delay(TimeSpan, CancellationToken)"/>,
	/// which throws <see cref="ArgumentOutOfRangeException"/> for intervals it cannot express as a
	/// timer: over <see cref="int.MaxValue"/> milliseconds (~24.85 days) on .NET Framework — reachable
	/// through the <c>netstandard2.0</c> target — and over 4294967294 milliseconds (~49.71 days) on
	/// .NET Core and later. An unbounded interval is therefore a latent crash on a background task,
	/// not just a bad idea. 24 hours is 25x under the lower of those two ceilings.
	/// </para>
	/// <para>
	/// It is also generous as a keep-alive. Real idle timeouts are far shorter: 60 seconds on an AWS
	/// Application Load Balancer by default, 350 seconds on a Network Load Balancer, 4–30 minutes on
	/// an Azure Load Balancer, 2 hours for Linux's default <c>tcp_keepalive_time</c>, and minutes to
	/// a couple of hours for consumer NAT. Anything idle for a day has long since been dropped by
	/// something in the path, so an interval beyond that cannot keep a connection alive.
	/// </para>
	/// </remarks>
	public static readonly TimeSpan MaximumKeepAliveInterval = TimeSpan.FromHours(24);

	/// <summary>
	/// Cached IAC NOP sequence, to avoid allocating one per keep-alive.
	/// </summary>
	private static readonly byte[] s_keepAliveNop = [(byte)Trigger.IAC, (byte)Trigger.NOP];

	/// <summary>
	/// Conversion factor from <see cref="Stopwatch"/> ticks to <see cref="TimeSpan"/> ticks.
	/// </summary>
	private static readonly double s_stopwatchTicksToTimeSpanTicks = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

	/// <summary>
	/// Timestamp (in <see cref="Stopwatch"/> ticks) of the most recent outbound write.
	/// Written from <see cref="WriteToNetworkAsync"/>, read from the keep-alive loop, hence
	/// the interlocked access: this is a 64-bit field and the library targets 32-bit runtimes.
	/// </summary>
	private long _lastWriteTimestamp = Stopwatch.GetTimestamp();

	/// <summary>
	/// The background keep-alive loop, or null when the keep-alive is disabled.
	/// </summary>
	private Task? _keepAliveTask;

	/// <summary>
	/// How long the connection may stay silent before a keep-alive is sent, or <see langword="null"/>
	/// (the default) to disable the keep-alive entirely.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The window is an <b>idle</b> window, not a fixed heartbeat: every outbound write through
	/// <see cref="WriteToNetworkAsync"/> restarts it, so a busy connection never sends anything extra.
	/// </para>
	/// <para>
	/// A non-null value must be between <see cref="MinimumKeepAliveInterval"/> and
	/// <see cref="MaximumKeepAliveInterval"/>; anything else is rejected by <c>BuildAsync</c> with an
	/// <see cref="ArgumentOutOfRangeException"/> rather than silently clamped.
	/// </para>
	/// <para>
	/// Prefer <c>TelnetInterpreterBuilder.WithKeepAlive()</c> over setting this directly.
	/// </para>
	/// </remarks>
	public TimeSpan? KeepAliveInterval { get; init; }

	/// <summary>
	/// Throws if <paramref name="interval"/> is outside
	/// [<see cref="MinimumKeepAliveInterval"/>, <see cref="MaximumKeepAliveInterval"/>].
	/// Shared by the builder and by <c>Validate</c>, so both entry points agree on the bounds.
	/// </summary>
	/// <param name="interval">The interval to check</param>
	/// <param name="parameterName">The name reported on the thrown exception</param>
	/// <exception cref="ArgumentOutOfRangeException">The interval is out of range.</exception>
	internal static void ValidateKeepAliveInterval(TimeSpan interval, string parameterName)
	{
		if (interval < MinimumKeepAliveInterval || interval > MaximumKeepAliveInterval)
		{
			throw new ArgumentOutOfRangeException(parameterName, interval,
				$"Keep-alive interval must be between {MinimumKeepAliveInterval} (1 second) and {MaximumKeepAliveInterval} (24 hours).");
		}
	}

	/// <summary>
	/// The action invoked when the idle window elapses. When <see langword="null"/>, the interpreter
	/// sends <c>IAC NOP</c> via <see cref="SendKeepAliveAsync"/>.
	/// </summary>
	/// <remarks>
	/// The action is invoked from a background loop. If it throws, the exception is logged and the
	/// keep-alive stops for the remaining life of this interpreter; it is never rethrown onto the
	/// host application or the byte-processing loop. See <see cref="KeepAliveInterval"/>.
	/// </remarks>
	public Func<TelnetInterpreter, CancellationToken, ValueTask>? KeepAliveAsync { get; init; }

	/// <summary>
	/// Sends a single <c>IAC NOP</c> (RFC 854 "No Operation") through the shared write lock.
	/// This is the default keep-alive payload.
	/// </summary>
	/// <remarks>
	/// <b>This does not verify that the peer is alive.</b> It only proves that the local write
	/// succeeded — that the bytes were handed to the transport. A peer that has silently vanished
	/// is not detected until TCP retransmission gives up, which can take minutes. NOP is enough to
	/// stop NAT tables, load balancers and idle timers from tearing down a quiet connection, and
	/// that is all it is for. Genuine peer-liveness detection needs a request the peer must answer,
	/// such as TIMING-MARK (RFC 860, option 6), which this library does not implement.
	/// </remarks>
	/// <param name="cancellationToken">Token to cancel the wait for the write lock.</param>
	/// <returns>A ValueTask representing the asynchronous operation.</returns>
	public ValueTask SendKeepAliveAsync(CancellationToken cancellationToken = default)
		=> WriteToNetworkAsync(s_keepAliveNop, cancellationToken);

	/// <summary>
	/// Records that traffic has just gone out, restarting the idle window.
	/// </summary>
	private void MarkNetworkWrite()
		=> Interlocked.Exchange(ref _lastWriteTimestamp, Stopwatch.GetTimestamp());

	/// <summary>
	/// Time elapsed since a <see cref="Stopwatch"/> timestamp.
	/// </summary>
	private static TimeSpan ElapsedSince(long timestamp)
		=> TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - timestamp) * s_stopwatchTicksToTimeSpanTicks));

	/// <summary>
	/// Starts the keep-alive loop if one is configured. Called once, from <see cref="BuildAsync"/>.
	/// </summary>
	private void StartKeepAlive()
	{
		if (_keepAliveTask is not null) return;
		if (KeepAliveInterval is not { } interval) return;

		MarkNetworkWrite();
		_keepAliveTask = Task.Run(() => KeepAliveLoopAsync(interval, _processingCts.Token));
		_logger.LogDebug("Keep-alive enabled with an idle interval of {KeepAliveInterval}", interval);
	}

	/// <summary>
	/// Background loop that sends a keep-alive after <paramref name="interval"/> of outbound silence.
	/// </summary>
	/// <remarks>
	/// The idle window is tracked as a timestamp rather than a resettable timer, which is what keeps
	/// this deadlock-free: <see cref="WriteToNetworkAsync"/> restarts the window with a single
	/// interlocked store while it owns the write lock, and the loop only ever takes that lock from
	/// out here, where it holds nothing. A keep-alive that comes due mid-write simply queues behind
	/// the write like any other caller.
	/// </remarks>
	private async Task KeepAliveLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var idle = ElapsedSince(Interlocked.Read(ref _lastWriteTimestamp));
				if (idle < interval)
				{
					// Traffic went out while we were waiting: sleep out the rest of the (restarted)
					// idle window instead of sending. This is the whole point of the idle reset.
					await Task.Delay(interval - idle, cancellationToken);
					continue;
				}

				_logger.LogTrace("Connection: {ConnectionState}", "Idle, sending keep-alive.");
				await (KeepAliveAsync ?? DefaultKeepAliveAsync)(this, cancellationToken);

				// Count our own keep-alive as traffic, so an override that bypasses
				// WriteToNetworkAsync still restarts the window instead of spinning.
				MarkNetworkWrite();
			}
		}
		catch (OperationCanceledException)
		{
			// Expected on shutdown.
			_logger.LogDebug("Keep-alive cancelled");
		}
		catch (Exception ex)
		{
			// The peer is gone, or the caller's send action is broken. Either way this runs on a
			// background task: rethrowing would take down the host application, and retrying would
			// only produce the same exception every interval until disposal. Log it and stop; the
			// caller learns the connection is dead from its own read loop completing.
			_logger.LogWarning(ex, "Keep-alive send failed. Keep-alive is now stopped for this connection.");
		}
	}

	/// <summary>
	/// The default keep-alive send action: <c>IAC NOP</c>.
	/// </summary>
	private static ValueTask DefaultKeepAliveAsync(TelnetInterpreter interpreter, CancellationToken cancellationToken)
		=> interpreter.SendKeepAliveAsync(cancellationToken);

	/// <summary>
	/// Waits for the keep-alive loop to observe cancellation and exit.
	/// Called from <see cref="DisposeAsync"/> before the write lock is disposed, so that a
	/// keep-alive can never write to a disposed lock or a completed channel.
	/// </summary>
	private async ValueTask StopKeepAliveAsync()
	{
		if (_keepAliveTask is null) return;

		try
		{
			await _keepAliveTask;
		}
		catch (OperationCanceledException)
		{
			// Expected.
		}
	}
}
