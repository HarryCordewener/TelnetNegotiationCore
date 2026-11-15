#nullable enable
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="TelnetInterpreter.WaitForProcessingAsync"/>: the barrier the tests in this suite --
/// and any consumer driving the interpreter by hand -- rely on to know that what they fed in has
/// been dealt with.
/// </summary>
public class ProcessingBarrierTests : BaseTest
{
	/// <summary>
	/// The barrier covers the <em>handling</em> of every byte fed in before it, not merely the
	/// removal of those bytes from the queue.
	/// </summary>
	/// <remarks>
	/// The distinction is the whole of it. Bytes are dequeued one at a time and then run through the
	/// state machine and the consumer's callbacks, so a barrier that watches the queue returns while
	/// the last byte -- the one that completes a subnegotiation, or submits a line -- is still being
	/// handled. Waiting a fixed extra moment afterwards hides that on an idle machine and stops
	/// hiding it on a loaded one, which is exactly the shape of a CI flake.
	/// </remarks>
	[Test]
	public async Task TheBarrierWaitsForHandlingAndNotJustForTheQueueToDrain()
	{
		var handled = false;

		var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(async (_, _, _) =>
			{
				// Comfortably longer than the fixed slop this used to lean on, and comfortably under
				// the ceiling: the barrier has to know, not guess.
				await Task.Delay(300);
				handled = true;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.BuildAsync();

		await interpreter.InterpretByteArrayAsync(Encoding.UTF8.GetBytes("a line\r\n"));
		await interpreter.WaitForProcessingAsync(additionalDelayMs: 0);

		await Assert.That(handled).IsTrue();
	}

	/// <summary>
	/// With nothing pending the barrier returns without awaiting anything, so a read loop calling it
	/// after every read pays nothing for it.
	/// </summary>
	/// <remarks>
	/// It used to poll with <c>Task.Delay(1)</c>, and even a satisfied wait went through the method's
	/// state machine; a pending one cost a timer tick per read, about 15.6 ms on Windows, which is
	/// what the builder's <c>OnInputProcessed</c> read loop paid for every read.
	/// </remarks>
	[Test]
	public async Task ABarrierWithNothingPendingCompletesSynchronously()
	{
		var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((_, _, _) => ValueTask.CompletedTask)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.BuildAsync();

		await interpreter.InterpretByteArrayAsync(Encoding.UTF8.GetBytes("a line\r\n"));
		await interpreter.WaitForProcessingAsync(additionalDelayMs: 0);

		var again = interpreter.WaitForProcessingAsync(additionalDelayMs: 0);
		await Assert.That(again.IsCompletedSuccessfully).IsTrue();
		await again;
	}

	/// <summary>
	/// Every caller waiting at once is released by the handling it was waiting for, not only the
	/// first to arrive.
	/// </summary>
	[Test]
	public async Task EveryConcurrentWaiterIsReleased()
	{
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var handled = false;

		var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(async (_, _, _) =>
			{
				await release.Task;
				handled = true;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.BuildAsync();

		await interpreter.InterpretByteArrayAsync(Encoding.UTF8.GetBytes("a line\r\n"));
		var waits = Enumerable.Range(0, 8)
			.Select(_ => interpreter.WaitForProcessingAsync(maxWaitMs: 10_000, additionalDelayMs: 0).AsTask())
			.ToArray();

		await Task.Delay(50);
		await Assert.That(waits.Any(w => w.IsCompleted)).IsFalse();

		release.SetResult();
		var stopwatch = Stopwatch.StartNew();
		await Task.WhenAll(waits);

		await Assert.That(handled).IsTrue();
		await Assert.That(stopwatch.Elapsed).IsLessThan(System.TimeSpan.FromSeconds(5));
	}

	/// <summary>
	/// A handler that never finishes still lets the barrier go at its ceiling.
	/// </summary>
	[Test]
	public async Task AHandlerThatNeverFinishesReleasesTheBarrierAtItsCeiling()
	{
		var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(async (_, _, _) => await never.Task)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.BuildAsync();

		await interpreter.InterpretByteArrayAsync(Encoding.UTF8.GetBytes("a line\r\n"));

		var stopwatch = Stopwatch.StartNew();
		await interpreter.WaitForProcessingAsync(maxWaitMs: 200, additionalDelayMs: 0);

		await Assert.That(stopwatch.ElapsedMilliseconds).IsGreaterThanOrEqualTo(150);
		await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(5_000);
		never.SetResult();
	}
}
