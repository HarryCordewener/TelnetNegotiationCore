#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// LINEMODE's <c>MODE</c> byte is a byte like any other, and RFC 1184 inherits RFC 854's rule about
/// what has to happen when one of them is 255.
/// </summary>
/// <remarks>
/// <para>
/// RFC 854: "the 255 code ... must be doubled" inside data. RFC 1184 builds its subnegotiations out
/// of ordinary bytes and adds no exemption, so a <c>MODE</c> byte of 255 has to go out as
/// <c>IAC IAC</c> or the peer reads it as the <c>IAC</c> beginning the end of the subnegotiation —
/// and then reads the real <c>IAC SE</c> as two more bytes of payload, so the frame never closes.
/// </para>
/// <para>
/// 255 is reachable without any help from a hostile peer. The mode bits RFC 1184 defines are EDIT
/// (1), TRAPSIG (2), MODE_ACK (4), SOFT_TAB (8) and LIT_ECHO (16); bits 32, 64 and 128 are
/// undefined, and this library passes a peer's undefined bits through rather than masking them. So a
/// peer proposing <c>0xFB</c> — every bit except the acknowledgment — gets acknowledged with
/// <c>0xFB | MODE_ACK</c>, which is <c>0xFF</c>.
/// </para>
/// </remarks>
public class LineModeEscapingTests : BaseTest
{
	private const byte IAC = 255;
	private const byte SB = 250;
	private const byte SE = 240;
	private const byte LINEMODE = 34;
	private const byte SUBNEG_MODE = 1;

	/// <summary>Every bit RFC 1184 defines, and every bit it does not, except the acknowledgment.</summary>
	private const byte EveryModeBitButAck = 0xFB;

	private static async Task<List<byte[]>> FramesFrom(
		TelnetInterpreter.TelnetMode mode,
		byte[] input)
	{
		var sent = new List<byte[]>();

		await using var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(f => { lock (sent) { sent.Add(f.ToArray()); } return ValueTask.CompletedTask; })
			.AddPlugin<LineModeProtocol>()
			.BuildAsync();

		await interpreter.WaitForProcessingAsync();
		await Task.Delay(150);
		lock (sent) { sent.Clear(); }

		await InterpretAndWaitAsync(interpreter, input);
		await PollUntilAsync(() => { lock (sent) { return sent.Count > 0; } }, timeoutMs: 400);

		lock (sent) { return [.. sent]; }
	}

	private static string Render(IEnumerable<byte[]> frames) =>
		string.Join(" | ", frames.Select(f => string.Concat(f.Select(b => b.ToString("x2")))));

	/// <summary>
	/// The subnegotiation this library sends back must be readable by the same parser that reads an
	/// incoming one. An unescaped 255 in the payload is not.
	/// </summary>
	[Test]
	public async Task AModeAcknowledgementEscapesAnIacInItsModeByte()
	{
		var frames = await FramesFrom(TelnetInterpreter.TelnetMode.Server,
			[IAC, SB, LINEMODE, SUBNEG_MODE, EveryModeBitButAck, IAC, SE]);

		var ack = frames.FirstOrDefault(f =>
			f.Length >= 4 && f[0] == IAC && f[1] == SB && f[2] == LINEMODE && f[3] == SUBNEG_MODE);

		await Assert.That(ack).IsNotNull()
			.Because($"a proposed mode without the ACK bit is acknowledged. Sent: {Render(frames)}");

		// IAC SB LINEMODE MODE IAC IAC IAC SE -- the payload 255 doubled, then the real terminator.
		await Assert.That(ack!).IsEquivalentTo(
			new byte[] { IAC, SB, LINEMODE, SUBNEG_MODE, IAC, IAC, IAC, SE }, CollectionOrdering.Matching);
	}

	/// <summary>
	/// The plainer statement of the same thing: what goes out has to survive being read back. An
	/// unescaped 255 leaves the frame unterminated, so the parser never reports it at all.
	/// </summary>
	[Test]
	public async Task AModeAcknowledgementSurvivesBeingReadBack()
	{
		var frames = await FramesFrom(TelnetInterpreter.TelnetMode.Server,
			[IAC, SB, LINEMODE, SUBNEG_MODE, EveryModeBitButAck, IAC, SE]);

		var ack = frames.FirstOrDefault(f =>
			f.Length >= 4 && f[0] == IAC && f[1] == SB && f[2] == LINEMODE && f[3] == SUBNEG_MODE);

		await Assert.That(ack).IsNotNull().Because($"Sent: {Render(frames)}");

		byte[]? readBackData = null;

		await using var reader = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<LineModeProtocol>()
				.OnModeChanged(m => { readBackData = [m]; return ValueTask.CompletedTask; })
			.BuildAsync();

		await InterpretAndWaitAsync(reader, ack!);
		await PollUntilAsync(() => readBackData is not null, timeoutMs: 400);

		await Assert.That(readBackData).IsNotNull()
			.Because("an unescaped 255 leaves the subnegotiation unterminated, so nothing is ever reported");
	}

	/// <summary>
	/// <see cref="LineModeProtocol.SetModeAsync"/> is public and takes any byte, so a host
	/// application can ask for 255 directly without a peer being involved at all.
	/// </summary>
	[Test]
	public async Task SetModeEscapesAnIacInTheModeByte()
	{
		var sent = new List<byte[]>();

		await using var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(silentLogger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(f => { lock (sent) { sent.Add(f.ToArray()); } return ValueTask.CompletedTask; })
			.AddPlugin<LineModeProtocol>()
			.BuildAsync();

		await server.WaitForProcessingAsync();
		await Task.Delay(150);
		lock (sent) { sent.Clear(); }

		var lineMode = server.PluginManager!.GetPlugin<LineModeProtocol>();
		await lineMode!.SetModeAsync(0xFF);
		await PollUntilAsync(() => { lock (sent) { return sent.Count > 0; } }, timeoutMs: 400);

		byte[][] frames;
		lock (sent) { frames = [.. sent]; }

		var frame = frames.FirstOrDefault(f => f.Length >= 4 && f[2] == LINEMODE && f[3] == SUBNEG_MODE);

		await Assert.That(frame).IsNotNull().Because($"Sent: {Render(frames)}");
		await Assert.That(frame!).IsEquivalentTo(
			new byte[] { IAC, SB, LINEMODE, SUBNEG_MODE, IAC, IAC, IAC, SE }, CollectionOrdering.Matching);
	}
}
