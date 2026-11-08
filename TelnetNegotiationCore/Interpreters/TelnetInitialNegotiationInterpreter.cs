using System;
using System.Threading;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>Why the bounded initial negotiation window ended. No result implies that every capability is known.</summary>
public enum InitialNegotiationEnd
{
	/// <summary>The requested time elapsed; late replies are still processed.</summary>
	DeadlineElapsed,
	/// <summary>A complete application line arrived, after protocol filtering.</summary>
	ApplicationInput,
	/// <summary>The interpreter was disposed.</summary>
	ConnectionClosed,
	/// <summary>The caller requested cancellation; no further startup work should proceed.</summary>
	Cancelled
}

public partial class TelnetInterpreter
{
	private readonly TaskCompletionSource<bool> _initialApplicationInput = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource<bool> _initialConnectionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Allow startup replies time to arrive without blocking the input-processing loop.</summary>
	/// <remarks>
	/// Call after building the interpreter and while the transport feeds received bytes normally.
	/// A complete application line or disposal ends the wait early. Silent clients reach the deadline;
	/// timeout does not mean unsupported capabilities, and later replies remain valid.
	/// Cancellation returns Cancelled and takes precedence when observed with another outcome.
	/// Await the returned value once; use AsTask() if the caller needs to share or compose the wait.
	/// </remarks>
	public ValueTask<InitialNegotiationEnd> WaitForInitialNegotiationAsync(TimeSpan maximumWait, CancellationToken cancellationToken = default) =>
		WaitForInitialNegotiationAsync(maximumWait, cancellationToken, endOnApplicationInput: true);

	/// <summary>Wait for startup replies, optionally continuing after application input arrives.</summary>
	/// <param name="maximumWait">The bounded window; zero skips waiting.</param>
	/// <param name="cancellationToken">Cancellation ends the window with Cancelled.</param>
	/// <param name="endOnApplicationInput">
	/// True preserves interactive startup behavior. False keeps the window open for clients that send
	/// saved login commands before their capability replies. Input is still delivered normally; hosts
	/// that defer registration must queue application input themselves.
	/// </param>
	/// <remarks>
	/// Disposal and cancellation still end the window. Replies arriving after the deadline remain valid;
	/// this method does not establish that every capability is known. Keep feeding transport input while waiting.
	/// </remarks>
	public async ValueTask<InitialNegotiationEnd> WaitForInitialNegotiationAsync(TimeSpan maximumWait,
		CancellationToken cancellationToken, bool endOnApplicationInput)
	{
		if (maximumWait < TimeSpan.Zero || maximumWait.TotalMilliseconds > int.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(maximumWait));
		if (cancellationToken.IsCancellationRequested) return InitialNegotiationEnd.Cancelled;
		// Avoid allocating a timer, linked cancellation source, and WhenAny promise when no wait is needed.
		if (_initialConnectionClosed.Task.IsCompleted) return InitialNegotiationEnd.ConnectionClosed;
		if (endOnApplicationInput && _initialApplicationInput.Task.IsCompleted) return InitialNegotiationEnd.ApplicationInput;
		if (maximumWait == TimeSpan.Zero) return InitialNegotiationEnd.DeadlineElapsed;
		using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		try
		{
			var delay = Task.Delay(maximumWait, timer.Token);
			var ended = await (endOnApplicationInput
				? Task.WhenAny(_initialConnectionClosed.Task, _initialApplicationInput.Task, delay)
				: Task.WhenAny(_initialConnectionClosed.Task, delay)).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested) return InitialNegotiationEnd.Cancelled;
			if (ended == _initialConnectionClosed.Task) return InitialNegotiationEnd.ConnectionClosed;
			return ended == _initialApplicationInput.Task
				? InitialNegotiationEnd.ApplicationInput
				: InitialNegotiationEnd.DeadlineElapsed;
		}
		finally
		{
			timer.Cancel();
		}
	}
}
