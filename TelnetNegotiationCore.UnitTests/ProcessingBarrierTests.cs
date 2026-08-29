#nullable enable
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
}
