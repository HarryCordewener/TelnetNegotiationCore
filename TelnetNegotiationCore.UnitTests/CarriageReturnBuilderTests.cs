using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// That the carriage-return setting is reachable from the builder, that each named shorthand means
/// what it says, and that the choice actually reaches the machine rather than stopping at the
/// interpreter.
/// </summary>
public class CarriageReturnBuilderTests : BaseTest
{
	private const byte NUL = 0;
	private const byte LF = 10;
	private const byte CR = 13;

	[Test]
	public async Task TheDefaultIsDrop()
	{
		var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.BuildAsync();

		// Asserted on the built interpreter rather than on the const, which the analyzer refuses.
		await Assert.That(interpreter.CarriageReturnMode)
			.IsEqualTo(TelnetInterpreter.DefaultCarriageReturnMode)
			.Because("a builder with no carriage-return call must take the default");
		await Assert.That(interpreter.CarriageReturnMode).IsEqualTo(CarriageReturnMode.Drop);

		await interpreter.DisposeAsync();
	}

	[Test]
	[Arguments(CarriageReturnMode.Drop)]
	[Arguments(CarriageReturnMode.EndOfLine)]
	[Arguments(CarriageReturnMode.Preserve)]
	public async Task WithCarriageReturnModeSetsIt(CarriageReturnMode mode)
	{
		var interpreter = await Build(b => b.WithCarriageReturnMode(mode));

		await Assert.That(interpreter.CarriageReturnMode).IsEqualTo(mode);

		await interpreter.DisposeAsync();
	}

	[Test]
	public async Task EachShorthandMeansItsMode()
	{
		var drop = await Build(b => b.DropCarriageReturns());
		await Assert.That(drop.CarriageReturnMode).IsEqualTo(CarriageReturnMode.Drop);
		await drop.DisposeAsync();

		var endOfLine = await Build(b => b.TreatCarriageReturnNullAsLineEnd());
		await Assert.That(endOfLine.CarriageReturnMode).IsEqualTo(CarriageReturnMode.EndOfLine);
		await endOfLine.DisposeAsync();

		var preserve = await Build(b => b.PreserveCarriageReturns());
		await Assert.That(preserve.CarriageReturnMode).IsEqualTo(CarriageReturnMode.Preserve);
		await preserve.DisposeAsync();
	}

	[Test]
	public async Task AnUndefinedModeIsRefused()
	{
		var builder = new TelnetInterpreterBuilder();

		await Assert.That(() => builder.WithCarriageReturnMode((CarriageReturnMode)99))
			.Throws<ArgumentOutOfRangeException>();
	}

	/// <summary>
	/// The setting has to reach the generated machine, not merely sit on the interpreter. Driven end
	/// to end through a built interpreter, because the wiring runs through
	/// <c>GeneratedContext.CarriageReturnMode</c> and nothing else would catch it being forgotten.
	/// </summary>
	[Test]
	public async Task TheModeReachesTheMachine()
	{
		var preserved = await SubmittedLinesAsync(
			b => b.PreserveCarriageReturns(), [.. "ab"u8, CR, NUL, .. "cd"u8, CR, LF]);

		await Assert.That(preserved).IsEquivalentTo(new[] { "ab\rcd" });

		var dropped = await SubmittedLinesAsync(
			b => b.DropCarriageReturns(), [.. "ab"u8, CR, NUL, .. "cd"u8, CR, LF]);

		await Assert.That(dropped).IsEquivalentTo(new[] { "abcd" });

		var ended = await SubmittedLinesAsync(
			b => b.TreatCarriageReturnNullAsLineEnd(), [.. "ab"u8, CR, NUL, .. "cd"u8, CR, LF]);

		await Assert.That(ended).IsEquivalentTo(new[] { "ab", "cd" });
	}

	private static Task<TelnetInterpreter> Build(Func<TelnetInterpreterBuilder, TelnetInterpreterBuilder> configure) =>
		configure(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)).BuildAsync();

	private static async Task<List<string>> SubmittedLinesAsync(
		Func<TelnetInterpreterBuilder, TelnetInterpreterBuilder> configure,
		byte[] bytes)
	{
		var lines = new List<string>();

		var interpreter = await configure(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(silentLogger)
			.OnSubmit((data, encoding, _) =>
			{
				lines.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)).BuildAsync();

		await using (interpreter)
		{
			await InterpretAndWaitAsync(interpreter, bytes);
			await PollUntilAsync(() => lines.Count > 0, timeoutMs: 2_000);
		}

		return lines;
	}
}
