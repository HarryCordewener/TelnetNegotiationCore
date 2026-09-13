// A property reports failure as a reason string and success as null, so this file opts in to
// nullable reference types; the project as a whole does not enable them.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// The same invariants driven through a fully configured interpreter, so that the plugin manager,
/// the builder, the byte-stream transforms and every protocol's own reply path are covered and not
/// just the core machine.
/// </summary>
/// <remarks>
/// <para>
/// One interpreter takes the whole corpus rather than one per stream. That is partly cost — an
/// interpreter negotiates on construction and runs work in the background, and standing up several
/// hundred of them took longer than the rest of the suite put together — but mostly it is the
/// better test: a real connection is long-lived and receives one hostile stream after another, so
/// state wrongly carried from one to the next is a failure this catches and a fresh-per-case loop
/// cannot.
/// </para>
/// <para>
/// The cost of sharing is that a failure cannot be shrunk against the shared instance, whose state
/// is by then unknown. So a failure is reported with the offending stream in full and then replayed
/// on a fresh interpreter, which says whether it reproduces in isolation or needs the accumulated
/// history — a distinction worth having when investigating.
/// </para>
/// </remarks>
public class InterpreterProperties : BaseTest
{
	/// <summary>
	/// Deliberately far smaller than <see cref="EngineProperties"/>'s 3,000. A stream costs roughly
	/// fifty milliseconds through the full pipeline against microseconds through the bare machine,
	/// so this is what fits in a few seconds — and the bare machine is where the migration's risk
	/// sits, with 946 example-based tests already covering the plugin layer. This is confirmation
	/// that the risk does not reappear once the plugins are stacked on, not the primary search.
	/// </summary>
	private const int Cases = 80;
	private const int MaxTokens = 8;

	/// <summary>How many streams pass between liveness probes. See the remarks on the probe test.</summary>
	private const int ProbeEvery = 10;

	/// <summary>
	/// <c>IAC SB</c> with no option byte, fired immediately before each probe so that the resync
	/// token has something to recover from that genuinely requires all of it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Without this the probe was nearly worthless at this sampling rate: most generated streams
	/// happen to end in the text state, where a single <c>IAC SE</c> resyncs just as well as the
	/// real token, so shortening <see cref="TelnetProbe.Resync"/> did not fail this test at all.
	/// </para>
	/// <para>
	/// It has to be this sequence and not, say, an unterminated GMCP subnegotiation. <c>IAC SB
	/// 201</c> lands in GMCP's own state, whose <c>Ended</c> transition a single <c>IAC SE</c>
	/// satisfies — so that would not have exercised the token either. <c>IAC SB</c> alone leaves
	/// the machine in <c>ReadingOption</c>, one of the two states <c>ResyncTests</c> pins as
	/// needing the full two pairs.
	/// </para>
	/// </remarks>
	private static readonly byte[] LeaveItOpen = [255, 250];

	private static async Task<(TelnetInterpreter Interpreter, List<string> Submitted)> BuildClientAsync()
	{
		var submitted = new List<string>();

		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(silentLogger)
			.OnSubmit((data, encoding, _) =>
			{
				submitted.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddDefaultMUDProtocols()
			.AddPlugin<AuthenticationProtocol>()
			.AddPlugin<EncryptionProtocol>()
			.AddPlugin<NewEnvironProtocol>()
			.AddPlugin<EnvironProtocol>()
			.AddPlugin<LineModeProtocol>()
			.AddPlugin<TerminalSpeedProtocol>()
			.AddPlugin<XDisplayProtocol>()
			.AddPlugin<FlowControlProtocol>()
			.AddPlugin<EchoProtocol>();

		return (await builder.BuildAsync(), submitted);
	}

	/// <summary>The corpus, generated the same way every other property generates it.</summary>
	private static IEnumerable<byte[]> Corpus(ulong seed)
	{
		for (var i = 0; i < Cases; i++)
		{
			var rng = new Rng(seed + ((ulong)i * 0x9E3779B97F4A7C15));
			yield return TelnetTokens.Flatten(TelnetTokens.Stream(rng, MaxTokens));
		}
	}

	private static string Literal(byte[] bytes) =>
		"byte[] bytes = [" + string.Join(", ", bytes.Select(b => b.ToString())) + "];";

	/// <summary>
	/// A throw anywhere in the stack lands on a consumer's read loop, where there is nothing useful
	/// to do with it.
	/// </summary>
	[Test]
	public async Task AFullyConfiguredInterpreterNeverThrowsOnAnyStream()
	{
		var (interpreter, _) = await BuildClientAsync();
		string? failure = null;

		await using (interpreter)
		{
			var caseIndex = 0;
			foreach (var bytes in Corpus(0x1A7E12))
			{
				try
				{
					await InterpretAndWaitAsync(interpreter, bytes);
				}
				catch (Exception ex)
				{
					failure = $"case {caseIndex} threw {ex.GetType().Name}: {ex.Message}"
						+ $"\n            {Literal(bytes)}"
						+ $"\n            In isolation: {await ReproducesAloneAsync(bytes)}";
					break;
				}

				caseIndex++;
			}
		}

		await Assert.That(failure).IsNull();
	}

	/// <summary>
	/// And the whole stack stays live: a plain line still reaches the submit callback a consumer
	/// actually registered, checked periodically through the corpus and again at the end.
	/// </summary>
	/// <remarks>
	/// Probing after every single stream would be the sharper test, but a submitted line surfaces
	/// through the interpreter's pipeline a couple of hundred milliseconds after the processing
	/// barrier returns, and paying that per stream cost more than the rest of the suite together.
	/// Probing every <see cref="ProbeEvery"/> streams brackets a wedge to a window of that many
	/// rather than to one, which is enough to investigate from — and
	/// <see cref="EngineProperties.AnyStreamRecoversAndKeepsParsingText"/> already checks the core
	/// machine after every single stream, which is where a wedge would originate.
	/// </remarks>
	[Test]
	public async Task AFullyConfiguredInterpreterStaysLiveAcrossTheWholeCorpus()
	{
		var (interpreter, submitted) = await BuildClientAsync();
		string? failure = null;

		await using (interpreter)
		{
			var probesSent = 0;
			var windowStart = 0;
			var window = new List<byte[]>();
			var caseIndex = 0;

			foreach (var bytes in Corpus(0x1A7E13))
			{
				await InterpretAndWaitAsync(interpreter, bytes);
				window.Add(bytes);

				var last = caseIndex == Cases - 1;
				if (!last && (caseIndex + 1) % ProbeEvery != 0)
				{
					caseIndex++;
					continue;
				}

				await InterpretAndWaitAsync(interpreter, LeaveItOpen);
				await InterpretAndWaitAsync(interpreter, TelnetProbe.Resync);
				await InterpretAndWaitAsync(interpreter, TelnetProbe.ProbeLine);
				probesSent++;

				var expected = probesSent;
				var arrived = await PollUntilAsync(
					() => submitted.Count(line => line == TelnetProbe.ProbeText) >= expected,
					timeoutMs: 2_000);

				if (!arrived)
				{
					var actual = submitted.Count(line => line == TelnetProbe.ProbeText);
					failure = $"the stack stopped submitting somewhere in cases "
						+ $"{windowStart}..{caseIndex}: expected {expected} probe lines, saw {actual}."
						+ $"\n            The {window.Count} streams in that window:"
						+ string.Concat(window.Select(w => "\n              " + Literal(w)))
						+ $"\n            Last stream in isolation: {await ReproducesAloneAsync(bytes)}";
					break;
				}

				window.Clear();
				windowStart = caseIndex + 1;
				caseIndex++;
			}
		}

		await Assert.That(failure).IsNull();
	}

	/// <summary>
	/// Replays one stream on a fresh interpreter, to say whether a failure needs the accumulated
	/// history of the corpus or stands on its own.
	/// </summary>
	private static async Task<string> ReproducesAloneAsync(byte[] bytes)
	{
		try
		{
			var (interpreter, submitted) = await BuildClientAsync();
			await using (interpreter)
			{
				await InterpretAndWaitAsync(interpreter, bytes);
				await InterpretAndWaitAsync(interpreter, TelnetProbe.Resync);
				await InterpretAndWaitAsync(interpreter, TelnetProbe.ProbeLine);

				var arrived = await PollUntilAsync(
					() => submitted.Contains(TelnetProbe.ProbeText),
					timeoutMs: 1_000);

				return arrived
					? "no -- on its own this stream is handled correctly, so the failure needs the "
						+ "history before it"
					: "yes -- this stream alone wedges a fresh interpreter";
			}
		}
		catch (Exception ex)
		{
			return $"yes -- this stream alone throws {ex.GetType().Name}: {ex.Message}";
		}
	}
}
