// A property reports failure as a reason string and success as null, so this file opts in to
// nullable reference types; the project as a whole does not enable them.
#nullable enable

using System;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TelnetNegotiationCore.Models;
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
	/// The machine's rule under <see cref="CarriageReturnMode.Drop"/>, which is what
	/// <see cref="RecordingTelnetContext"/> reports: drop every carriage return, consume a
	/// <c>NUL</c> that directly follows one, and break on every line feed.
	/// </para>
	/// <para>
	/// The <c>NUL</c>-consuming half is the part that changed. It used to be ordinary text, so
	/// RFC 854's <c>CR NUL</c> reached a consumer as a literal 0x00 inside the line — which matched
	/// no RFC and no other implementation. Only a <c>NUL</c> <em>directly</em> after a carriage
	/// return is consumed: in <c>CR NUL NUL</c> the second one is text, because the first consumed
	/// the pending carriage return along with itself.
	/// </para>
	/// <para>
	/// The other two modes are covered by <see cref="CarriageReturnPolicyTests"/>, which drives them
	/// explicitly rather than through a generated corpus.
	/// </para>
	/// </remarks>
	private static string Normalise(byte[] bytes)
	{
		var sb = new StringBuilder();
		var afterCarriageReturn = false;

		foreach (var b in bytes)
		{
			if (b == (byte)'\r')
			{
				afterCarriageReturn = true;
				continue;
			}

			if (afterCarriageReturn && b == 0)
			{
				// CR NUL: the pair is consumed entirely.
				afterCarriageReturn = false;
				continue;
			}

			afterCarriageReturn = false;
			sb.Append(b == (byte)'\n' ? '\n' : (char)b);
		}

		return sb.ToString();
	}

	private static string Describe(string value) =>
		value.Replace("\r", "\\r").Replace("\n", "\\n");
}
