using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// The generator and the shrinker are the two pieces every property depends on, so they are tested
/// in their own right. A shrinker that loses the failure, or a generator that is not reproducible,
/// would make every counterexample useless.
/// </summary>
public class RngTests
{
	[Test]
	public async Task TheSameSeedProducesTheSameSequence()
	{
		var first = new Rng(12345);
		var second = new Rng(12345);

		for (var i = 0; i < 100; i++)
		{
			await Assert.That(first.Next(1000)).IsEqualTo(second.Next(1000));
		}
	}

	[Test]
	public async Task DifferentSeedsDiverge()
	{
		var first = new Rng(1);
		var second = new Rng(2);

		var a = Enumerable.Range(0, 50).Select(_ => first.Next(1000)).ToArray();
		var b = Enumerable.Range(0, 50).Select(_ => second.Next(1000)).ToArray();

		await Assert.That(a.SequenceEqual(b)).IsFalse();
	}

	[Test]
	public async Task NextStaysInsideItsBound()
	{
		var rng = new Rng(99);

		for (var i = 0; i < 10_000; i++)
		{
			var value = rng.Next(7);

			await Assert.That(value).IsGreaterThanOrEqualTo(0);
			await Assert.That(value).IsLessThan(7);
		}
	}

	[Test]
	public async Task EveryByteValueIsReachable()
	{
		var rng = new Rng(7);
		var seen = new HashSet<byte>();

		for (var i = 0; i < 100_000; i++)
		{
			seen.Add(rng.NextByte());
		}

		await Assert.That(seen.Count).IsEqualTo(256);
	}

	[Test]
	public async Task ShrinkFindsTheSingleOffendingElement()
	{
		var input = Enumerable.Range(0, 40).ToArray();

		// "Still fails" means the sequence contains 17, so the minimum such sequence is [17].
		var shrunk = Shrink.Sequence<int>(input, candidate => candidate.Contains(17));

		await Assert.That(shrunk.Count).IsEqualTo(1);
		await Assert.That(shrunk[0]).IsEqualTo(17);
	}

	[Test]
	public async Task ShrinkKeepsAPairThatMustTravelTogether()
	{
		var input = Enumerable.Range(0, 40).ToArray();

		var shrunk = Shrink.Sequence<int>(input, candidate => candidate.Contains(3) && candidate.Contains(9));

		await Assert.That(shrunk.Count).IsEqualTo(2);
		await Assert.That(shrunk.Contains(3)).IsTrue();
		await Assert.That(shrunk.Contains(9)).IsTrue();
	}

	/// <summary>
	/// When the empty sequence still fails, it is the minimal counterexample and the shrinker has to
	/// be able to reach it. Anything it kept back would be a byte in the report with nothing to do
	/// with the failure.
	/// </summary>
	[Test]
	public async Task ShrinkReachesTheEmptySequenceWhenThatStillFails()
	{
		var shrunk = Shrink.Sequence<int>([1, 2, 3, 4, 5], _ => true);

		await Assert.That(shrunk.Count).IsEqualTo(0);
	}

	[Test]
	public async Task ShrinkReturnsTheInputWhenNothingCanBeRemoved()
	{
		var shrunk = Shrink.Sequence<int>([1, 2], _ => false);

		await Assert.That(shrunk.Count).IsEqualTo(2);
	}
}
