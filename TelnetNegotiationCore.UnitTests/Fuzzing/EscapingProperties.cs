using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Helpers;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// What the library escapes on the way out has to be recovered byte-identically on the way in, for
/// any payload at all.
/// </summary>
/// <remarks>
/// <para>
/// This is the invariant PR #105 found broken in both directions for ENCRYPT and AUTHENTICATION: a
/// credential byte or an encryption key id of 0xFF went out unescaped, where any receiver reads it
/// as the IAC that ends the subnegotiation and the rest of the stream desynchronises. Asserting it
/// over generated payloads rather than over a chosen few is what makes it hard to regress.
/// </para>
/// <para>
/// GMCP is the vehicle because it delivers its payload to the consumer byte for byte: its module
/// collapses <c>IAC IAC</c> into one literal 255 and its <c>Ended</c> transition is guarded on
/// <c>Escaping</c>, so only a real <c>IAC SE</c> terminates the message. The core's generic
/// <c>SubNegotiatedAsync</c> is handed <c>default</c> for its payload, so it cannot serve here.
/// </para>
/// </remarks>
public class EscapingProperties
{
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte GMCP = 201;
	private const byte IAC = 255;

	private const int Cases = 3000;

	/// <summary>Escaping then parsing is the identity, for every payload.</summary>
	[Test]
	public async Task EscapingThenParsingReturnsThePayloadUnchanged()
	{
		for (var i = 0; i < Cases; i++)
		{
			var rng = new Rng(0x5CAFF01D + (ulong)i);

			// Weighted heavily towards 0xFF, because that is the byte the invariant is about.
			var payload = new byte[rng.Next(24)];
			for (var j = 0; j < payload.Length; j++)
			{
				payload[j] = rng.Bool(30) ? IAC : rng.NextByte();
			}

			var escaped = new List<byte>();
			SubnegotiationEscaping.AppendEscaped(escaped, payload);

			byte[] wire = [IAC, SB, GMCP, .. escaped, IAC, SE];

			var recorder = new RecordingTelnetContext();
			await using var machine = new TelnetCoreMachine(recorder, TelnetMachineConfig.Default);
			await machine.StartAsync();
			await machine.FireAsync(wire);

			await Assert.That(recorder.GmcpMessages.Count)
				.IsEqualTo(1)
				.Because($"payload [{Describe(payload)}] did not arrive as exactly one message");

			await Assert.That(recorder.GmcpMessages[0].SequenceEqual(payload))
				.IsTrue()
				.Because($"payload [{Describe(payload)}] came back as [{Describe(recorder.GmcpMessages[0])}]");
		}
	}

	/// <summary>
	/// Escaping must never leave a lone 0xFF behind, which is the shape of the bug itself: one
	/// unescaped 255 in a payload is read by the peer as the IAC that ends the subnegotiation.
	/// </summary>
	[Test]
	public async Task EscapingNeverLeavesALoneIac()
	{
		for (var i = 0; i < Cases; i++)
		{
			var rng = new Rng(0x1ACF00D + (ulong)i);

			var payload = new byte[rng.Next(24)];
			for (var j = 0; j < payload.Length; j++)
			{
				payload[j] = rng.Bool(40) ? IAC : rng.NextByte();
			}

			var escaped = new List<byte>();
			SubnegotiationEscaping.AppendEscaped(escaped, payload);

			var index = 0;
			var lone = false;
			while (index < escaped.Count)
			{
				if (escaped[index] == IAC)
				{
					if (index + 1 >= escaped.Count || escaped[index + 1] != IAC)
					{
						lone = true;
						break;
					}

					index += 2;
					continue;
				}

				index++;
			}

			await Assert.That(lone)
				.IsFalse()
				.Because($"payload [{Describe(payload)}] escaped to a lone IAC");
		}
	}

	/// <summary>
	/// An unescaped trailing 0xFF is what a broken sender produces. The machine has to treat it as
	/// the frame terminator it looks like and keep going, rather than wedging on it.
	/// </summary>
	[Test]
	public async Task AnUnescapedTrailingIacDoesNotWedge()
	{
		for (var i = 0; i < 500; i++)
		{
			var rng = new Rng(0x8BADF00D + (ulong)i);

			var payload = new byte[1 + rng.Next(12)];
			for (var j = 0; j < payload.Length; j++)
			{
				payload[j] = rng.NextByte();
			}

			byte[] wire = [IAC, SB, GMCP, .. payload, IAC];

			var recorder = new RecordingTelnetContext();
			await using var machine = new TelnetCoreMachine(recorder, TelnetMachineConfig.Default);
			await machine.StartAsync();
			await machine.FireAsync(wire);
			await machine.FireAsync(TelnetProbe.Resync);
			await machine.FireAsync(TelnetProbe.ProbeLine);

			await Assert.That(TelnetProbe.Recovered(recorder))
				.IsTrue()
				.Because($"payload [{Describe(payload)}] wedged the machine");
		}
	}

	private static string Describe(IEnumerable<byte> bytes) => string.Join(", ", bytes.Select(b => b.ToString()));
}
