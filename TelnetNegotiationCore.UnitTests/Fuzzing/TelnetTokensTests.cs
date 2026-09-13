using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// The generator's own tests. A generator that produced only noise, or that was not reproducible,
/// would make every property that depends on it worthless while still reporting green.
/// </summary>
public class TelnetTokensTests
{
	[Test]
	public async Task TheSameSeedProducesTheSameStream()
	{
		var a = TelnetTokens.Flatten(TelnetTokens.Stream(new Rng(4242), 20));
		var b = TelnetTokens.Flatten(TelnetTokens.Stream(new Rng(4242), 20));

		await Assert.That(a.SequenceEqual(b)).IsTrue();
	}

	[Test]
	public async Task AStreamIsNeverEmptyAndRespectsItsTokenCeiling()
	{
		for (ulong seed = 1; seed <= 200; seed++)
		{
			var tokens = TelnetTokens.Stream(new Rng(seed), 12);

			await Assert.That(tokens.Count).IsGreaterThan(0);
			await Assert.That(tokens.Count).IsLessThanOrEqualTo(12);
		}
	}

	/// <summary>
	/// The whole point of tokens rather than random bytes: a large share of generated streams has
	/// to contain a well-formed frame, because that is where the state machine lives. Uniform noise
	/// would essentially never produce one.
	/// </summary>
	[Test]
	public async Task WellFormedFramesAreCommon()
	{
		var wellFormed = 0;

		for (ulong seed = 1; seed <= 1000; seed++)
		{
			if (TelnetTokens.Stream(new Rng(seed), 8).Any(t => t.Kind == "frame"))
			{
				wellFormed++;
			}
		}

		await Assert.That(wellFormed).IsGreaterThan(700);
	}

	[Test]
	public async Task EveryKindOfTokenIsReachable()
	{
		var kinds = new HashSet<string>();
		var rng = new Rng(31337);

		for (var i = 0; i < 20_000; i++)
		{
			kinds.Add(TelnetTokens.NextToken(rng).Kind);
		}

		await Assert.That(kinds).Contains("frame");
		await Assert.That(kinds).Contains("text");
		await Assert.That(kinds).Contains("mutated");
		await Assert.That(kinds).Contains("verb");
		await Assert.That(kinds).Contains("adversarial");
	}

	[Test]
	public async Task AWellFormedFrameIsProperlyDelimited()
	{
		var rng = new Rng(555);

		for (var i = 0; i < 2000; i++)
		{
			var token = TelnetTokens.NextToken(rng);
			if (token.Kind != "frame")
			{
				continue;
			}

			await Assert.That(token.Bytes[0]).IsEqualTo((byte)255);
			await Assert.That(token.Bytes[1]).IsEqualTo((byte)250);
			await Assert.That(token.Bytes[^2]).IsEqualTo((byte)255);
			await Assert.That(token.Bytes[^1]).IsEqualTo((byte)240);
		}
	}

	/// <summary>
	/// A well-formed frame's payload must contain no lone 255, or the frame would end early and the
	/// generator would be producing something other than what it claims to.
	/// </summary>
	[Test]
	public async Task AWellFormedFramePayloadHasNoUnescapedIac()
	{
		var rng = new Rng(888);

		for (var i = 0; i < 2000; i++)
		{
			var token = TelnetTokens.NextToken(rng);
			if (token.Kind != "frame")
			{
				continue;
			}

			// The payload sits between IAC SB <option> and the trailing IAC SE.
			var payload = token.Bytes[3..^2];
			var index = 0;
			while (index < payload.Length)
			{
				if (payload[index] == 255)
				{
					await Assert.That(index + 1).IsLessThan(payload.Length);
					await Assert.That(payload[index + 1]).IsEqualTo((byte)255);
					index += 2;
					continue;
				}

				index++;
			}
		}
	}
}
