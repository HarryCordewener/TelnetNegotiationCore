using System;
using System.Collections.Generic;
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
