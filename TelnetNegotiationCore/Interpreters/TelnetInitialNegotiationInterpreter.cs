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
	ConnectionClosed
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
	/// </remarks>
	public async Task<InitialNegotiationEnd> WaitForInitialNegotiationAsync(TimeSpan maximumWait, CancellationToken cancellationToken = default)
	{
		if (maximumWait < TimeSpan.Zero || maximumWait.TotalMilliseconds > int.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(maximumWait));
		cancellationToken.ThrowIfCancellationRequested();
		using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		try
		{
			var delay = Task.Delay(maximumWait, timer.Token);
			var ended = await Task.WhenAny(_initialConnectionClosed.Task, _initialApplicationInput.Task, delay).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
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
