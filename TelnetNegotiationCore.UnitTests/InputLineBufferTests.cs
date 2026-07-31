using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The line the interpreter assembles out of ordinary (non-negotiation) bytes, and the ceiling on it.
/// </summary>
/// <remarks>
/// A peer decides when to send a newline, so the line buffer is the one accumulator in the library
/// that an untrusted peer can grow simply by never terminating a line. It matters more now that MSSP
/// has a plaintext transport, which is the first thing in the library that deliberately asks a
/// stranger to send text.
/// </remarks>
public class InputLineBufferTests : BaseTest
{
	private static async Task<(TelnetInterpreter Interpreter, List<string> Submitted)> ClientAsync(int? maxBufferSize)
	{
		var submitted = new List<string>();

		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				lock (submitted) submitted.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask);

		if (maxBufferSize is { } size)
		{
			builder = builder.WithMaxBufferSize(size);
		}

		return (await builder.BuildAsync(), submitted);
	}

	/// <summary>
	/// A line that reaches exactly the ceiling has not passed it, and is delivered whole.
	/// </summary>
	[Test]
	public async Task ALineExactlyAtTheCeilingIsDelivered()
	{
		var (interpreter, submitted) = await ClientAsync(1024);

		await InterpretAndWaitAsync(interpreter, Encoding.ASCII.GetBytes(new string('x', 1024) + "\r\n"));
		await PollUntilAsync(() => submitted.Count > 0);

		await Assert.That(submitted.Count).IsEqualTo(1);
		await Assert.That(submitted[0].Length).IsEqualTo(1024);

		await interpreter.DisposeAsync();
	}

	/// <summary>
	/// A line past the ceiling is dropped rather than truncated, and the connection carries on. Before
	/// this, the write past the end of the buffer threw out of the state machine and killed the
	/// byte-processing loop for the rest of the connection — every later byte, negotiation included,
	/// went nowhere.
	/// </summary>
	[Test]
	public async Task ALinePastTheCeilingIsDroppedAndTheConnectionSurvives()
	{
		var (interpreter, submitted) = await ClientAsync(1024);

		await InterpretAndWaitAsync(interpreter, Encoding.ASCII.GetBytes(new string('x', 4096) + "\r\n"));
		await InterpretAndWaitAsync(interpreter, Encoding.ASCII.GetBytes("still here\r\n"));
		await PollUntilAsync(() => submitted.Count > 0);

		// Dropped, not truncated: a line cut at an arbitrary point is not a shorter line, it is a
		// different one, and nothing downstream could tell the difference.
		await Assert.That(submitted.Count).IsEqualTo(1);
		await Assert.That(submitted[0]).IsEqualTo("still here");

		await interpreter.DisposeAsync();
	}

	/// <summary>
	/// The buffer starts small and grows towards the ceiling, so a line far larger than the starting
	/// capacity has to survive several growth steps with its bytes in the right order. A line just
	/// under the ceiling exercises every one of them.
	/// </summary>
	[Test]
	public async Task ALineJustUnderTheCeilingSurvivesEveryGrowthStep()
	{
		var (interpreter, submitted) = await ClientAsync(8192);

		// Distinguishable content rather than a run of one byte: a growth step that copied the wrong
		// range, or dropped a byte at a boundary, would still produce the right length otherwise.
		var line = string.Concat(Enumerable.Range(0, 8191).Select(i => (char)('a' + (i % 26))));

		await InterpretAndWaitAsync(interpreter, Encoding.ASCII.GetBytes(line + "\r\n"));
		await PollUntilAsync(() => submitted.Count > 0);

		await Assert.That(submitted.Count).IsEqualTo(1);
		await Assert.That(submitted[0].Length).IsEqualTo(8191);
		await Assert.That(submitted[0]).IsEqualTo(line);

		await interpreter.DisposeAsync();
	}

	/// <summary>
	/// Growth does not change what the ceiling means. Several lines that each cross a growth boundary,
	/// then one that crosses the ceiling: the first are delivered whole, the last is dropped whole, and
	/// the connection carries on.
	/// </summary>
	[Test]
	public async Task GrowthDoesNotMoveTheCeiling()
	{
		var (interpreter, submitted) = await ClientAsync(4096);

		foreach (var length in new[] { 100, 1023, 1024, 1025, 4095, 4096 })
		{
			await InterpretAndWaitAsync(interpreter, Encoding.ASCII.GetBytes(new string('x', length) + "\r\n"));
		}

		await InterpretAndWaitAsync(interpreter, Encoding.ASCII.GetBytes(new string('y', 4097) + "\r\n"));
		await InterpretAndWaitAsync(interpreter, Encoding.ASCII.GetBytes("after\r\n"));
		await PollUntilAsync(() => submitted.Count >= 7);

		// Six delivered, the over-long one dropped, then the connection continues.
		await Assert.That(submitted.Count).IsEqualTo(7);
		await Assert.That(submitted.Select(line => line.Length))
			.IsEquivalentTo(new[] { 100, 1023, 1024, 1025, 4095, 4096, 5 });
		await Assert.That(submitted.Any(line => line.Contains('y'))).IsFalse();
		await Assert.That(submitted[^1]).IsEqualTo("after");

		await interpreter.DisposeAsync();
	}

	/// <summary>
	/// The ceiling is what a hostile peer may reach, not what a line costs. A server holding many
	/// connections that are each saying "look" must not pay the ceiling per connection.
	/// </summary>
	/// <remarks>
	/// <para>
	/// At the 5 MiB default, 100 connections would be 500 MiB if the buffer were allocated at its
	/// ceiling — which is what it did before this, first in the constructor and then on the first byte
	/// of input. The threshold is deliberately an order of magnitude above what growth actually costs
	/// (100 KiB of line buffers) and an order of magnitude below what the ceiling would, so this
	/// measures the property rather than the allocator's mood.
	/// </para>
	/// <para>
	/// The line is deliberately left <em>unterminated</em>. A delivered line releases an over-large
	/// buffer anyway, so measuring after a newline would pass whatever the growth policy is; a
	/// connection sitting mid-line is what actually holds the allocation, and is the ordinary state of
	/// a connection parked at a prompt.
	/// </para>
	/// <para>
	/// Serialized, because <see cref="GC.GetTotalMemory"/> measures the whole process and TUnit runs
	/// tests in parallel by default: another test allocating inside the measured window would be
	/// counted against these connections.
	/// </para>
	/// </remarks>
	[Test]
	[NotInParallel]
	public async Task ManyConnectionsDoNotEachPayTheCeiling()
	{
		const int connections = 100;

		var interpreters = new List<TelnetInterpreter>();

		GC.Collect();
		GC.WaitForPendingFinalizers();
		var before = GC.GetTotalMemory(true);

		for (var i = 0; i < connections; i++)
		{
			var (interpreter, _) = await ClientAsync(maxBufferSize: null);   // the 5 MiB default
			interpreters.Add(interpreter);
			await InterpretAndWaitAsync(interpreter, Encoding.ASCII.GetBytes("look"));
		}

		var after = GC.GetTotalMemory(true);
		var perConnection = (after - before) / connections;

		// 5 MiB apiece would be 5,242,880. Growth costs 1 KiB of line buffer plus the interpreter.
		await Assert.That(perConnection).IsLessThan(512 * 1024);

		foreach (var interpreter in interpreters)
		{
			await interpreter.DisposeAsync();
		}

		GC.KeepAlive(interpreters);
	}

	/// <summary>
	/// The ceiling is a byte count, so it must reject a value that cannot be one.
	/// </summary>
	[Test]
	public async Task TheCeilingRejectsNonPositiveValues()
	{
		var builder = new TelnetInterpreterBuilder();

		await Assert.That(() => builder.WithMaxBufferSize(0)).Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => builder.WithMaxBufferSize(-1)).Throws<ArgumentOutOfRangeException>();
	}
}
