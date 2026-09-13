// A property reports failure as a reason string and success as null, so this file opts in to
// nullable reference types; the project as a whole does not enable them.
#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// A stream fed in chunks has to be indistinguishable from the same stream fed whole.
/// </summary>
/// <remarks>
/// <para>
/// This is the property a state-machine rewrite most needs. Every <c>ref self</c> field in
/// <c>TelnetStates.cs</c> — <c>SubNegotiation.Option</c>, <c>Connected.Width</c>,
/// <c>Connected.Height</c>, and each protocol module's own — is state that has to survive a chunk
/// boundary, and a socket splits wherever it likes. A machine that reads a two-byte NAWS dimension
/// correctly in one call and incorrectly across two would be broken in a way no example-based test
/// in this suite would notice, because every one of them hands over its bytes in a single array.
/// </para>
/// <para>
/// The comparison is <see cref="RecordingTelnetContext.Snapshot"/>, which is deliberately blind to
/// how byte runs batched into <c>Write</c> calls; see its remarks. Chunking changes that batching
/// legitimately, and a comparison that could see it would fail on every case for no reason.
/// </para>
/// </remarks>
public class FragmentationProperties
{
	// Each case runs its stream several times over, so this gets a smaller budget than the
	// single-pass properties in EngineProperties.
	private const int Cases = 1000;
	private const int MaxTokens = 8;

	private static async Task<string> Whole(byte[] bytes)
	{
		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(bytes);
		return recorder.Snapshot();
	}

	private static async Task<string> InChunks(byte[] bytes, IReadOnlyList<int> boundaries)
	{
		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();

		var start = 0;
		foreach (var boundary in boundaries)
		{
			if (boundary <= start || boundary > bytes.Length)
			{
				continue;
			}

			await machine.FireAsync(bytes[start..boundary]);
			start = boundary;
		}

		if (start < bytes.Length)
		{
			await machine.FireAsync(bytes[start..]);
		}

		return recorder.Snapshot();
	}

	/// <summary>The worst case a socket can produce, and the one most likely to expose held state.</summary>
	[Test]
	public async Task OneByteAtATimeIsTheSameAsAllAtOnce()
	{
		var failure = await PropertyRunner.ForEachStream(0xD15EA5E, Cases, MaxTokens, async bytes =>
		{
			var whole = await Whole(bytes);
			var split = await InChunks(bytes, [.. Enumerable.Range(1, bytes.Length)]);

			return whole == split
				? null
				: $"\n            whole:   {whole}\n            chunked: {split}";
		});

		await Assert.That(failure).IsNull();
	}

	[Test]
	public async Task AnyChunkingIsTheSameAsAllAtOnce()
	{
		var failure = await PropertyRunner.ForEachStream(0xE1F, Cases, MaxTokens, async bytes =>
		{
			var whole = await Whole(bytes);

			// Several independent chunkings per stream. The boundary choice is derived from the
			// stream's own length so it stays deterministic without threading another seed in.
			for (var attempt = 0; attempt < 4; attempt++)
			{
				var rng = new Rng(((ulong)bytes.Length + 1) * 0x100000001B3 + (ulong)attempt);
				var boundaries = new List<int>();
				for (var i = 1; i < bytes.Length; i++)
				{
					if (rng.Bool(30))
					{
						boundaries.Add(i);
					}
				}

				var split = await InChunks(bytes, boundaries);
				if (whole != split)
				{
					return $"boundaries [{string.Join(", ", boundaries)}]"
						+ $"\n            whole:   {whole}\n            chunked: {split}";
				}
			}

			return null;
		});

		await Assert.That(failure).IsNull();
	}

	/// <summary>
	/// An empty chunk is what a socket hands over on a zero-length read, and it has to change
	/// nothing at all.
	/// </summary>
	[Test]
	public async Task EmptyChunksChangeNothing()
	{
		var failure = await PropertyRunner.ForEachStream(0xF00D, Cases, MaxTokens, async bytes =>
		{
			var whole = await Whole(bytes);

			var recorder = new RecordingTelnetContext();
			await using var machine = new TelnetCoreMachine(recorder);
			await machine.StartAsync();

			byte[] nothing = [];
			await machine.FireAsync(nothing);
			foreach (var b in bytes)
			{
				byte[] one = [b];
				await machine.FireAsync(one);
				await machine.FireAsync(nothing);
			}

			var padded = recorder.Snapshot();

			return whole == padded
				? null
				: $"\n            whole:  {whole}\n            padded: {padded}";
		});

		await Assert.That(failure).IsNull();
	}
}
