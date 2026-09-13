// A property reports failure as a reason string and success as null, so this file opts in to
// nullable reference types; the project as a whole does not enable them.
#nullable enable

using System;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// Invariants the core machine has to hold for every input, asserted over a generated corpus rather
/// than over hand-picked examples.
/// </summary>
public class EngineProperties
{
	private const int Cases = 3000;
	private const int MaxTokens = 10;

	/// <summary>
	/// A throw here would land on a consumer's read loop, where there is nothing useful to do with
	/// it, so any exception at all is the failure.
	/// </summary>
	[Test]
	public async Task FiringAnyStreamNeverThrows()
	{
		var failure = await PropertyRunner.ForEachStream(0xA11CE, Cases, MaxTokens, async bytes =>
		{
			try
			{
				var recorder = new RecordingTelnetContext();
				await using var machine = new TelnetCoreMachine(recorder);
				await machine.StartAsync();
				await machine.FireAsync(bytes);
				return null;
			}
			catch (Exception ex)
			{
				return $"{ex.GetType().Name}: {ex.Message}";
			}
		});

		await Assert.That(failure).IsNull();
	}

	/// <summary>
	/// Generalises <see cref="MalformedSubnegotiationRecoveryTests"/> from its thirty hand-written
	/// cases to the generated space. The bug class is real and recent: that file documents states
	/// which had no transition for IAC or SE and so wedged the connection for the remainder of its
	/// lifetime on a single malformed byte.
	/// </summary>
	[Test]
	public async Task AnyStreamRecoversAndKeepsParsingText()
	{
		var failure = await PropertyRunner.ForEachStream(0xB0B, Cases, MaxTokens, async bytes =>
		{
			var recorder = new RecordingTelnetContext();
			await using var machine = new TelnetCoreMachine(recorder);
			await machine.StartAsync();
			await machine.FireAsync(bytes);
			await machine.FireAsync(TelnetProbe.Resync);
			await machine.FireAsync(TelnetProbe.ProbeLine);

			return TelnetProbe.Recovered(recorder)
				? null
				: "the probe line did not arrive after the resync: the machine is wedged";
		});

		await Assert.That(failure).IsNull();
	}

	/// <summary>
	/// For input with no IAC in it, every byte must reach the context exactly once and in order.
	/// The machine is allowed to interpret line endings, because that is its job, so the comparison
	/// normalises RFC 854's CR LF and CR NUL and a bare CR to one line break and then requires
	/// equality.
	/// </summary>
	[Test]
	public async Task IacFreeTextArrivesIntactAndInOrder()
	{
		var failure = await PropertyRunner.ForEachStream(0xC0FFEE, Cases, MaxTokens, async bytes =>
		{
			// Only the text path is under test, so a stream containing an IAC is skipped rather
			// than reshaped: reshaping would quietly change what is being asserted.
			foreach (var b in bytes)
			{
				if (b == 255)
				{
					return null;
				}
			}

			var recorder = new RecordingTelnetContext();
			await using var machine = new TelnetCoreMachine(recorder);
			await machine.StartAsync();
			await machine.FireAsync(bytes);

			// Each submitted line is terminated rather than separated, because a separator cannot
			// distinguish no lines at all from one empty line -- and a bare LF produces exactly
			// that empty line.
			var delivered = new StringBuilder();
			foreach (var line in recorder.Lines)
			{
				delivered.Append(line).Append('\n');
			}

			delivered.Append(recorder.PendingText);

			var expected = Normalise(bytes);

			return delivered.ToString() == expected
				? null
				: $"expected \"{Describe(expected)}\" but the context received \"{Describe(delivered.ToString())}\"";
		});

		await Assert.That(failure).IsNull();
	}

	/// <summary>
	/// The engine's line-ending policy, restated so the property compares against it rather than
	/// against an invented one.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>TelnetCoreModule.DropReturn</c> and <c>DropReturnInLine</c> discard a carriage return
	/// wherever it arrives — "a carriage return is not part of the line" — and <c>EndOfLine</c>
	/// submits on a line feed. So the rule is simply: drop every CR, break on every LF.
	/// </para>
	/// <para>
	/// NUL has no transition of its own and is therefore ordinary text, which means RFC 854's
	/// <c>CR NUL</c> reaches a consumer as a literal 0x00 inside the line rather than as the bare
	/// carriage return the RFC defines it to be. That is pre-existing behaviour, outside the change
	/// under test here, and <c>CarriageReturnPolicyTests</c> pins it so it stays deliberate.
	/// </para>
	/// </remarks>
	private static string Normalise(byte[] bytes)
	{
		var sb = new StringBuilder();
		foreach (var b in bytes)
		{
			if (b == (byte)'\r')
			{
				continue;
			}

			sb.Append(b == (byte)'\n' ? '\n' : (char)b);
		}

		return sb.ToString();
	}

	private static string Describe(string value) =>
		value.Replace("\r", "\\r").Replace("\n", "\\n");
}
