using System;
using System.Collections.Generic;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// A seeded xorshift64* generator.
/// </summary>
/// <remarks>
/// <see cref="System.Random"/> is deliberately not used: its sequence is not contractually stable
/// across runtimes, and this suite runs on three of them. A case that fails on net8.0 has to be
/// reproducible on net11.0 from the same seed, or a failure cannot be investigated.
/// </remarks>
internal sealed class Rng
{
	// Any non-zero state will do. Xorshift is degenerate at zero, so a caller passing seed 0 is
	// treated as asking for a default sequence rather than for a generator that returns only zero.
	private ulong _state;

	public Rng(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15 : seed;

	private ulong NextUInt64()
	{
		var x = _state;
		x ^= x >> 12;
		x ^= x << 25;
		x ^= x >> 27;
		_state = x;
		return x * 0x2545F4914F6CDD1D;
	}

	/// <summary>A value in <c>[0, exclusiveBound)</c>.</summary>
	public int Next(int exclusiveBound)
	{
		if (exclusiveBound <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(exclusiveBound));
		}

		// The modulo bias here is below one part in 2^55 for any bound this suite uses, far under
		// the point where it could shape a result.
		return (int)(NextUInt64() % (ulong)exclusiveBound);
	}

	public byte NextByte() => (byte)(NextUInt64() >> 56);

	/// <summary>True with the given percent probability.</summary>
	public bool Bool(int percent) => Next(100) < percent;

	public T Pick<T>(IReadOnlyList<T> values) => values[Next(values.Count)];
}
