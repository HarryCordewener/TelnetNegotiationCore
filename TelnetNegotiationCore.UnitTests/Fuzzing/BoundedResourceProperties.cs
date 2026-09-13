using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Helpers;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// The resources a peer can make this side spend have to stay bounded whatever it sends.
/// </summary>
public class BoundedResourceProperties : BaseTest
{
	private static readonly byte[] WillMccp2 =
		[(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2];

	private static readonly byte[] StartMccp2 =
		[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP2, (byte)Trigger.IAC, (byte)Trigger.SE];

	/// <summary>
	/// A payload past the cap is marked overflowed and stops growing the buffer, for any sequence
	/// of appends and any cap.
	/// </summary>
	/// <remarks>
	/// It is marked rather than truncated deliberately: a GMCP message with its tail removed is not
	/// a smaller message, it is a corrupt one, and a consumer parsing it as JSON cannot tell a
	/// truncated payload from a malformed server.
	/// </remarks>
	[Test]
	public async Task ASubnegotiationBufferNeverGrowsPastItsCap()
	{
		for (var i = 0; i < 300; i++)
		{
			var rng = new Rng(0xBADDCAFE + (ulong)i);
			var cap = 1 + rng.Next(512);
			var buffer = new SubnegotiationBuffer(cap);

			var written = 0;
			var chunks = 1 + rng.Next(8);
			for (var c = 0; c < chunks; c++)
			{
				var length = rng.Next(400);
				for (var b = 0; b < length; b++)
				{
					buffer.Add(rng.NextByte());
					written++;
				}
			}

			await Assert.That(buffer.Count)
				.IsLessThanOrEqualTo(cap)
				.Because($"cap {cap} after {written} bytes written");

			await Assert.That(buffer.Overflowed)
				.IsEqualTo(written > cap)
				.Because($"cap {cap} after {written} bytes written");

			await Assert.That(buffer.ReceivedBytes)
				.IsEqualTo(written)
				.Because($"cap {cap} after {written} bytes written");
		}
	}

	[Test]
	public async Task ResettingABufferClearsItsOverflowFlag()
	{
		var buffer = new SubnegotiationBuffer(4);
		for (var i = 0; i < 100; i++)
		{
			buffer.Add(1);
		}

		await Assert.That(buffer.Overflowed).IsTrue();
		await Assert.That(buffer.Count).IsEqualTo(4);

		buffer.Reset();

		await Assert.That(buffer.Overflowed).IsFalse();
		await Assert.That(buffer.Count).IsEqualTo(0);
	}

	/// <summary>
	/// Any deflate stream either inflates and is delivered, or is refused with the inflater stopped
	/// and an error logged. Never a throw onto the read loop, and never the two states disagreeing.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The invariant asserted is that the inflater is running exactly when nothing was reported
	/// wrong with the stream. There are two legitimate reasons to stop one -- the expansion ceiling
	/// and a stream that is not valid zlib -- and the property deliberately does not care which,
	/// nor does it re-derive the 200:1-past-a-mebibyte policy: re-implementing that here would only
	/// prove the test agrees with itself.
	/// </para>
	/// <para>
	/// The existing limits bound the memory a peer can make this side hold, not the work of getting
	/// there: one compressed byte can inflate to 1,032, and 4 KiB of deflate holding 4 MiB of zeros
	/// costs about 430 ms of a core against about 1 ms for 4 KiB of plain telnet.
	/// </para>
	/// </remarks>
	[Test]
	public async Task AnyCompressedStreamIsEitherDeliveredOrStopped()
	{
		var outcomes = new Dictionary<string, bool>();
		var delivered = new Dictionary<string, ConcurrentQueue<string>>();

		// Each case builds an interpreter and inflates a stream, so the corpus is small and chosen
		// to span the shapes that matter rather than generated wholesale.
		foreach (var (name, wire) in CompressedCorpus())
		{
			var log = new CapturingLogger(logger);

			// The inflater's output has to be observed, not discarded. Comparing IsMCCP2Enabled
			// against the error log alone would pass a stream that decompressed to nothing at all,
			// since "still running and nothing reported wrong" is exactly what that looks like.
			var lines = new ConcurrentQueue<string>();
			delivered[name] = lines;

			var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(log)
				.OnSubmit((data, encoding, _) =>
				{
					lines.Enqueue(encoding.GetString(data));
					return ValueTask.CompletedTask;
				})
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<MCCPProtocol>());

			await InterpretAndWaitAsync(client, WillMccp2);
			await InterpretAndWaitAsync(client, StartMccp2);

			var plugin = client.PluginManager!.GetPlugin<MCCPProtocol>()!;

			// The property is that this does not throw, whatever the peer sent.
			await InterpretAndWaitAsync(client, wire);

			// InterpretAndWaitAsync has already waited for the inflater to finish with these bytes,
			// so the error is there or it is not coming. The timeout is short on purpose: polling
			// for an absence pays the full timeout on every case that is not refused, which is most
			// of them, and a generous one here cost more wall-clock than the rest of the suite.
			var refused = await PollUntilAsync(
				() => log.Entries(LogLevel.Error).Any(),
				timeoutMs: 500);

			await Assert.That(plugin.IsMCCP2Enabled)
				.IsEqualTo(!refused)
				.Because($"\"{name}\": the inflater is "
					+ (plugin.IsMCCP2Enabled ? "still running" : "stopped")
					+ " but the log "
					+ (refused ? "reported an error" : "reported nothing")
					+ ". Errors: " + string.Join(" | ", log.Entries(LogLevel.Error)));

			outcomes[name] = refused;

			await client.DisposeAsync();
		}

		// The consistency check above would pass vacuously if the ceiling were disabled outright --
		// every stream would survive and every survival would agree with an empty log. These two
		// assertions are what make the property sensitive to the ceiling actually being enforced.
		await Assert.That(outcomes["2 MiB of zeros"])
			.IsTrue()
			.Because("a stream expanding a thousandfold past the mebibyte floor must be refused");

		await Assert.That(outcomes["realistic prose"])
			.IsFalse()
			.Because("prose compresses about fivefold and must be left alone");

		await Assert.That(outcomes["incompressible noise"])
			.IsFalse()
			.Because("a stream that barely compresses at all must be left alone");

		// And the streams that were left alone actually delivered what they carried. Without this
		// the consistency check above is satisfied by an inflater that quietly produces nothing.
		var prose = await PollUntilAsync(
			() => delivered["realistic prose"].Any(line => line.Contains("boarded front door")),
			timeoutMs: 5_000);

		await Assert.That(prose)
			.IsTrue()
			.Because("the prose stream stayed enabled, so its lines must have reached the consumer. "
				+ $"Saw {delivered["realistic prose"].Count} lines");

		// Only the prose case can be observed this way, and deliberately so. A submitted line needs
		// a newline to terminate it: 64 KiB of NULs inflates perfectly well but completes no line,
		// so its bytes reach the context's Write and nothing is ever submitted. Asserting delivery
		// there would be asserting something false about a stream that is behaving correctly.
	}

	/// <summary>The compressed shapes worth driving: harmless, hostile, and malformed.</summary>
	private static IEnumerable<(string Name, byte[] Wire)> CompressedCorpus()
	{
		var rng = new Rng(0xC0FFEE11);

		var line = Encoding.ASCII.GetBytes(
			"You are standing in an open field west of a white house, with a boarded front door.\r\n");

		// Realistic prose, which deflate gets perhaps five-fold: must survive.
		yield return ("realistic prose", Deflate(Enumerable.Range(0, 2000).SelectMany(_ => line).ToArray()));

		// Incompressible noise, a ratio near 1:1: must survive.
		var noise = new byte[64 * 1024];
		for (var i = 0; i < noise.Length; i++)
		{
			noise[i] = rng.NextByte();
		}

		yield return ("incompressible noise", Deflate(noise));

		// Zero runs across the ceiling: the small one survives, the large one is a bomb. 2 MiB is
		// the smallest round size that still clears the mebibyte floor and so trips the 200:1
		// ratio; MCCPExpansionLimitTests drives the full 4 MiB, and paying for it twice would cost
		// this property more wall-clock than the rest of the suite combined.
		yield return ("64 KiB of zeros", Deflate(new byte[64 * 1024]));
		yield return ("2 MiB of zeros", Deflate(new byte[2 * 1024 * 1024]));

		// Malformed streams, which must not throw onto the read loop.
		var bomb = Deflate(new byte[2 * 1024 * 1024]);
		yield return ("truncated bomb", bomb[..(bomb.Length / 2)]);
		yield return ("garbage claiming to be deflate", [0x78, 0x9C, 0xFF, 0x00, 0x13, 0x37, 0x42]);
		yield return ("empty", []);
	}

	private static byte[] Deflate(byte[] payload)
	{
		using var sink = new MemoryStream();
		using (var z = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true))
		{
			z.Write(payload, 0, payload.Length);
			z.Flush();
		}

		return sink.ToArray();
	}
}
