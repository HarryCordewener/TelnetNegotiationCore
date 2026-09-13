using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Machine;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// CHARSET's <c>TTABLE-IS</c> carries a translation table, which is arbitrary octets — so it is the
/// one CHARSET payload that can contain a literal <c>0xFF</c>.
/// </summary>
/// <remarks>
/// <para>
/// RFC 2066: "All octets of value 255 (other than IAC) MUST be quoted to conform with TELNET
/// requirements." A translation table maps between character sets, so an entry for any 8-bit
/// charset's <c>0xFF</c> — ISO-8859-1 'ÿ', for one — puts that byte in the payload.
/// </para>
/// <para>
/// The receive side already collapses <c>IAC IAC</c> back to one literal byte, in
/// <c>CharsetModule</c>'s <c>Mark</c> transition. Sending without escaping is therefore the same
/// one-directional asymmetry PR #105 fixed for ENCRYPT and AUTHENTICATION, and it means this library
/// mis-parses its own output.
/// </para>
/// <para>
/// CHARSET's other two payloads cannot reach this: <c>ACCEPTED</c> and <c>REQUEST</c> both build
/// their charset lists with <c>Encoding.ASCII</c>, which maps anything outside 0x00-0x7F to
/// <c>'?'</c>.
/// </para>
/// </remarks>
public class CharsetTTableEscapingTests : BaseTest
{
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte IAC = 255;
	private const byte CHARSET = 42;
	private const byte TTABLE_IS = 4;

	/// <summary>Builds a client with CHARSET and returns it plus a log of what it sent.</summary>
	private static async Task<(TelnetInterpreter Interpreter, List<byte[]> Sent)> BuildAsync()
	{
		var sent = new List<byte[]>();

		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(m => { sent.Add(m.ToArray()); return ValueTask.CompletedTask; })
			.AddPlugin<CharsetProtocol>());

		return (interpreter, sent);
	}

	private static byte[] PayloadOf(byte[] frame) => frame[4..^2];

	[Test]
	public async Task ALiteralIacInATranslationTableIsDoubled()
	{
		var (interpreter, sent) = await BuildAsync();

		// A table entry mapping to ISO-8859-1 'y with diaeresis', which is 0xFF.
		byte[] table = [0x01, 0x02, IAC, 0x03];

		await using (interpreter)
		{
			sent.Clear();
			await interpreter.PluginManager!.GetPlugin<CharsetProtocol>()!.SendTTableAsync(table);
			await PollUntilAsync(() => sent.Count > 0, timeoutMs: 2_000);
		}

		var frame = sent.Single(f => f.Length > 4 && f[2] == CHARSET && f[3] == TTABLE_IS);

		await Assert.That(PayloadOf(frame).SequenceEqual(new byte[] { 0x01, 0x02, IAC, IAC, 0x03 }))
			.IsTrue()
			.Because($"RFC 2066 requires the 255 to be quoted. Sent payload: [{string.Join(", ", PayloadOf(frame))}]");
	}

	/// <summary>
	/// The round trip that matters: what this library sends, this library must read back unchanged.
	/// </summary>
	[Test]
	public async Task ATableSurvivesBeingSentAndRead()
	{
		var (interpreter, sent) = await BuildAsync();

		byte[] table = [0x00, IAC, 0x41, IAC, IAC, 0x42];

		await using (interpreter)
		{
			sent.Clear();
			await interpreter.PluginManager!.GetPlugin<CharsetProtocol>()!.SendTTableAsync(table);
			await PollUntilAsync(() => sent.Count > 0, timeoutMs: 2_000);
		}

		var frame = sent.Single(f => f.Length > 4 && f[2] == CHARSET && f[3] == TTABLE_IS);

		// Feed exactly those bytes into the machine and collect what the table handler receives.
		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(frame);

		var received = recorder.CharsetTTables.SelectMany(x => x).ToArray();

		await Assert.That(received.SequenceEqual(table))
			.IsTrue()
			.Because($"sent [{string.Join(", ", table)}] but read back [{string.Join(", ", received)}]");
	}

	/// <summary>A table with no 0xFF in it must be unchanged, so the fix cannot corrupt normal data.</summary>
	[Test]
	public async Task ATableWithoutAnIacIsSentVerbatim()
	{
		var (interpreter, sent) = await BuildAsync();

		byte[] table = [0x01, 0x41, 0x42, 0x7F, 0x80];

		await using (interpreter)
		{
			sent.Clear();
			await interpreter.PluginManager!.GetPlugin<CharsetProtocol>()!.SendTTableAsync(table);
			await PollUntilAsync(() => sent.Count > 0, timeoutMs: 2_000);
		}

		var frame = sent.Single(f => f.Length > 4 && f[2] == CHARSET && f[3] == TTABLE_IS);

		await Assert.That(PayloadOf(frame).SequenceEqual(table))
			.IsTrue()
			.Because($"expected verbatim, got [{string.Join(", ", PayloadOf(frame))}]");
	}
}
