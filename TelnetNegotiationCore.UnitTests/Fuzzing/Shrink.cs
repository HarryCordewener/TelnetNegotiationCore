using System;
using System.Collections.Generic;
using System.Linq;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// Reduces a failing sequence to a minimal still-failing one, so that a counterexample is small
/// enough to read and to paste into a named regression test.
/// </summary>
internal static class Shrink
{
	/// <summary>
	/// Delta debugging by halves: try removing progressively smaller contiguous runs and keep any
	/// removal that leaves the sequence still failing.
	/// </summary>
	/// <remarks>
	/// Terminates because the granularity strictly decreases whenever a pass removes nothing, and
	/// the sequence never grows. The result is 1-minimal for contiguous removal: no single run of
	/// the final granularity can be dropped without the failure going away.
	/// </remarks>
	/// <param name="input">A sequence known to fail.</param>
	/// <param name="stillFails">Whether a candidate subsequence still fails.</param>
	/// <returns>
	/// The smallest subsequence found that still fails, or <paramref name="input"/> when nothing
	/// could be removed.
	/// </returns>
	public static IReadOnlyList<T> Sequence<T>(IReadOnlyList<T> input, Func<IReadOnlyList<T>, bool> stillFails)
	{
		var current = input.ToList();
		var granularity = current.Count;

		while (granularity >= 1)
		{
			var removedSomething = false;

			for (var start = 0; start + granularity <= current.Count;)
			{
				var candidate = new List<T>(current.Count - granularity);
				candidate.AddRange(current.Take(start));
				candidate.AddRange(current.Skip(start + granularity));

				// No Count > 0 guard: if the empty sequence still fails then it is the minimal
				// counterexample, and refusing to test it would leave a byte in the report that has
				// nothing to do with the failure.
				if (stillFails(candidate))
				{
					current = candidate;
					removedSomething = true;

					// Do not advance: the window now covers elements it has not seen.
					continue;
				}

				start++;
			}

			if (!removedSomething)
			{
				granularity /= 2;
			}
		}

		return current;
	}
}
