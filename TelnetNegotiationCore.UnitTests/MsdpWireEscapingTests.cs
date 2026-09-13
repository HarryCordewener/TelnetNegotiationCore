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
/// What MSDP actually puts on the wire, and that a literal <c>0xFF</c> in a payload is doubled.
/// </summary>
/// <remarks>
/// <para>
/// <c>SendMSDPPayloadAsync</c> and <c>SendMSDPCommand</c> escape through <c>TelnetSafeBytes</c>, and
/// until now that was asserted nowhere — the behaviour was documented in a comment and nothing held
/// it in place. GMCP's equivalent has <c>GMCPTests.SendGMCPCommandEscapesALiteralIACByte</c>, MSSP has
/// <c>MSSPWireTests</c>, and ENCRYPT and AUTHENTICATION got theirs when their escaping was fixed;
/// MSDP had the code and not the test.
/// </para>
/// <para>
/// MSDP's own specification says a variable or value "cannot contain the MSDP_VAR, MSDP_VAL, IAC, or
/// NUL byte", so a well-behaved caller never triggers this. It is reachable anyway: a non-ASCII
/// <see cref="TelnetInterpreter.CurrentEncoding"/> can encode a single character to <c>0xFF</c>
/// (ISO-8859-1 'ÿ'), and <c>SendMSDPPayloadAsync</c> takes raw bytes from a consumer that may have
/// built them itself.
/// </para>
/// </remarks>
public class MsdpWireEscapingTests : BaseTest
{
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte IAC = 255;
	private const byte MSDP = 69;
	private const byte MSDP_VAR = 1;
	private const byte MSDP_VAL = 2;

	/// <summary>Captures whole frames written to the network, which is where MSDP sends land.</summary>
	private static async Task<byte[]> FrameFor(Func<TelnetInterpreter, ValueTask> send)
	{
		var written = new List<byte>();

		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(m => { written.AddRange(m.ToArray()); return ValueTask.CompletedTask; })
			.AddPlugin<GMCPProtocol>());

		await using (interpreter)
		{
			written.Clear();
			await send(interpreter);
			await PollUntilAsync(() => written.Count > 0, timeoutMs: 2_000);
		}

		return [.. written];
	}

	private static byte[] PayloadOf(byte[] frame) => frame[3..^2];

	[Test]
	public async Task APayloadIsFramedAsASubnegotiation()
	{
		byte[] payload = [MSDP_VAR, .. "SEND"u8, MSDP_VAL, .. "PLAYERS"u8];

		var frame = await FrameFor(t => t.SendMSDPPayloadAsync(payload));

		await Assert.That(frame[0]).IsEqualTo(IAC);
		await Assert.That(frame[1]).IsEqualTo(SB);
		await Assert.That(frame[2]).IsEqualTo(MSDP);
		await Assert.That(frame[^2]).IsEqualTo(IAC);
		await Assert.That(frame[^1]).IsEqualTo(SE);
		await Assert.That(PayloadOf(frame).SequenceEqual(payload)).IsTrue();
	}

	[Test]
	public async Task ALiteralIacInAPayloadIsDoubled()
	{
		byte[] payload = [MSDP_VAR, .. "X"u8, MSDP_VAL, IAC, 0x41];

		var frame = await FrameFor(t => t.SendMSDPPayloadAsync(payload));

		byte[] expected = [MSDP_VAR, (byte)'X', MSDP_VAL, IAC, IAC, 0x41];

		await Assert.That(PayloadOf(frame).SequenceEqual(expected))
			.IsTrue()
			.Because($"expected the 255 doubled, got [{string.Join(", ", PayloadOf(frame))}]");
	}

	[Test]
	public async Task ALiteralIacInACommandArgumentIsDoubled()
	{
		var frame = await FrameFor(t => t.SendMSDPCommand([(byte)'S'], [IAC, (byte)'v']));

		byte[] expected = [MSDP_VAR, (byte)'S', MSDP_VAL, IAC, IAC, (byte)'v'];

		await Assert.That(PayloadOf(frame).SequenceEqual(expected))
			.IsTrue()
			.Because($"got [{string.Join(", ", PayloadOf(frame))}]");
	}

	/// <summary>
	/// The round trip: what MSDP writes, the machine must read back as the same payload bytes.
	/// </summary>
	[Test]
	public async Task APayloadSurvivesBeingSentAndRead()
	{
		byte[] payload = [MSDP_VAR, .. "K"u8, MSDP_VAL, IAC, 0x42, IAC, IAC];

		var frame = await FrameFor(t => t.SendMSDPPayloadAsync(payload));

		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(frame);

		var received = recorder.MsdpMessages.SelectMany(x => x).ToArray();

		await Assert.That(received.SequenceEqual(payload))
			.IsTrue()
			.Because($"sent [{string.Join(", ", payload)}] but read back [{string.Join(", ", received)}]");
	}

	/// <summary>A payload with no 0xFF must go out verbatim.</summary>
	[Test]
	public async Task APayloadWithoutAnIacIsSentVerbatim()
	{
		byte[] payload = [MSDP_VAR, .. "ROOM"u8, MSDP_VAL, 0x7F, 0x80, 0xFE];

		var frame = await FrameFor(t => t.SendMSDPPayloadAsync(payload));

		await Assert.That(PayloadOf(frame).SequenceEqual(payload))
			.IsTrue()
			.Because($"got [{string.Join(", ", PayloadOf(frame))}]");
	}
}
