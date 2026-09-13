// A property reports failure as a reason string and success as null, so this file opts in to
// nullable reference types; the project as a whole does not enable them.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// Runs one property over a generated corpus and, on failure, shrinks the offending stream to a
/// minimal one and formats it as a C# literal.
/// </summary>
/// <remarks>
/// The literal matters more than the seed. A seed reproduces a failure only while the generator is
/// unchanged, whereas a byte array reproduces it forever. Every counterexample this prints is meant
/// to be pasted into a named test of its own, which is where it keeps its value.
/// </remarks>
internal static class PropertyRunner
{
	/// <summary>
	/// Checks <paramref name="check"/> against <paramref name="cases"/> generated streams.
	/// </summary>
	/// <param name="seed">The run's seed. Each case derives its own generator from it.</param>
	/// <param name="cases">How many streams to try.</param>
	/// <param name="maxTokens">The token ceiling for each stream.</param>
	/// <param name="check">Null when the stream satisfies the property, or a reason when it does not.</param>
	/// <returns>Null when every case passed, or a report naming the smallest failing stream.</returns>
	public static async Task<string?> ForEachStream(
		ulong seed,
		int cases,
		int maxTokens,
		Func<byte[], Task<string?>> check)
	{
		for (var i = 0; i < cases; i++)
		{
			// Each case gets its own generator, derived from the run's seed, so that case 900 is
			// reproducible without replaying the 899 before it.
			var rng = new Rng(seed + ((ulong)i * 0x9E3779B97F4A7C15));
			var tokens = TelnetTokens.Stream(rng, maxTokens);
			var bytes = TelnetTokens.Flatten(tokens);

			var reason = await check(bytes);
			if (reason is null)
			{
				continue;
			}

			var shrunk = ShrinkStream(tokens, check);

			// The shrinker keeps any non-null failure, so the shrunk stream may well fail for a
			// different reason than the one the full stream failed for. Reporting the original
			// reason beside the shrunk bytes would describe a counterexample that does not exist.
			var shrunkReason = await check(shrunk) ?? reason;

			return Report(seed, i, shrunkReason, shrunk);
		}

		return null;
	}

	private static byte[] ShrinkStream(
		IReadOnlyList<TelnetTokens.Token> tokens,
		Func<byte[], Task<string?>> check)
	{
		// Token granularity first, which keeps frames intact and so keeps the counterexample
		// readable, then byte granularity for the final squeeze.
		var byToken = Shrink.Sequence(tokens, candidate => Fails(TelnetTokens.Flatten(candidate)));
		var bytes = TelnetTokens.Flatten(byToken);
		var byByte = Shrink.Sequence<byte>(bytes, candidate => Fails(candidate.ToArray()));

		return byByte.ToArray();

		bool Fails(byte[] candidate) => check(candidate).GetAwaiter().GetResult() is not null;
	}

	private static string Report(ulong seed, int caseIndex, string reason, byte[] shrunk)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"Property failed on case {caseIndex} of seed 0x{seed:X}.");
		sb.AppendLine($"Reason: {reason}");
		sb.AppendLine($"Shrunk to {shrunk.Length} bytes. Paste this into a named regression test:");
		sb.Append("    byte[] bytes = [");
		sb.Append(string.Join(", ", shrunk.Select(b => b.ToString())));
		sb.AppendLine("];");
		return sb.ToString();
	}
}
