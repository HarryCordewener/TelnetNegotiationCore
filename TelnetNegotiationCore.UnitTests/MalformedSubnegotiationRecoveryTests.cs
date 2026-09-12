using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// A malformed command/sub-command byte, or garbage between a marker's real IAC and its real SE, must
/// never wedge the connection or fire a callback it did not earn. Two distinct bug classes, found via a
/// CodeRabbit review of PR #107 and by tracing the same pattern into every module that shares it:
/// <list type="bullet">
/// <item><description><b>Wedge:</b> a state that reads one of a specific set of command bytes (SEND/IS,
/// MODE/FORWARDMASK/SLC, etc.) had no transition of its own for IAC or SE -- only the specific command
/// bytes moved it anywhere, and everything else, including a well-formed terminating IAC SE, fell into a
/// bare self-loop. One malformed byte there wedged the entire connection for its remaining lifetime, not
/// just the one subnegotiation, because there was no way back to Idle.</description></item>
/// <item><description><b>Misfire:</b> a marker state used a boolean <c>Escaping</c> field to remember "an
/// IAC just arrived, only SE may follow", but its own malformed-byte handler left the field set instead of
/// clearing it. A stray byte between the real IAC and an unrelated, later SE would still satisfy the
/// guard and fire the marker's callback.</description></item>
/// </list>
/// </summary>
public class MalformedSubnegotiationRecoveryTests
{
	private const byte SE = 240;
	private const byte SB = 250;
	private const byte WILL = 251;
	private const byte IAC = 255;

	private static async Task<RecordingTelnetContext> Run(params byte[] bytes)
	{
		var recorder = new RecordingTelnetContext();
		await using var machine = new TelnetCoreMachine(recorder);
		await machine.StartAsync();
		await machine.FireAsync(bytes);
		return recorder;
	}

	// ---------------------------------------------------------------------------------------------
	// Wedge: a malformed command byte must recover to Idle, not lock up the connection.
	// ---------------------------------------------------------------------------------------------

	[Test]
	public async Task MalformedTspeedCommandByteDoesNotWedgeTheConnection()
	{
		var recorder = await Run([IAC, SB, 32, 99, IAC, SE, .. "hello\r\n"u8]);

		await Assert.That(recorder.TerminalSpeedRequests).IsEqualTo(0);
		await Assert.That(recorder.TerminalSpeedReports).IsEmpty();
		await Assert.That(recorder.SubNegotiations).Contains((byte)32);
		await Assert.That(recorder.Lines).Contains("hello");
	}

	[Test]
	public async Task MalformedXDisplayCommandByteDoesNotWedgeTheConnection()
	{
		var recorder = await Run([IAC, SB, 35, 99, IAC, SE, .. "hello\r\n"u8]);

		await Assert.That(recorder.XDisplayLocationRequests).IsEqualTo(0);
		await Assert.That(recorder.XDisplayLocationReports).IsEmpty();
		await Assert.That(recorder.Lines).Contains("hello");
	}

	[Test]
	public async Task MalformedTerminalTypeCommandByteDoesNotWedgeTheConnection()
	{
		var recorder = await Run([IAC, SB, 24, 99, IAC, SE, .. "hello\r\n"u8]);

		await Assert.That(recorder.TerminalTypeRequests).IsEqualTo(0);
		await Assert.That(recorder.TerminalTypeReports).IsEmpty();
		await Assert.That(recorder.Lines).Contains("hello");
	}

	[Test]
	public async Task MalformedCharsetCommandByteDoesNotWedgeTheConnection()
	{
		var recorder = await Run([IAC, SB, 42, 99, IAC, SE, .. "hello\r\n"u8]);

		await Assert.That(recorder.CharsetRequests).IsEmpty();
		await Assert.That(recorder.CharsetAccepted).IsEmpty();
		await Assert.That(recorder.Lines).Contains("hello");
	}

	[Test]
	public async Task MalformedAuthenticationCommandByteDoesNotWedgeTheConnection()
	{
		var recorder = await Run([IAC, SB, 37, 99, IAC, SE, .. "hello\r\n"u8]);

		await Assert.That(recorder.AuthenticationSends).IsEmpty();
		await Assert.That(recorder.AuthenticationIsMessages).IsEmpty();
		await Assert.That(recorder.Lines).Contains("hello");
	}

	[Test]
	public async Task MalformedEncryptionCommandByteDoesNotWedgeTheConnection()
	{
		var recorder = await Run([IAC, SB, 38, 99, IAC, SE, .. "hello\r\n"u8]);

		await Assert.That(recorder.EncryptionSends).IsEmpty();
		await Assert.That(recorder.EncryptionIsMessages).IsEmpty();
		await Assert.That(recorder.EncryptionStarts).IsEmpty();
		await Assert.That(recorder.EncryptionEnds).IsEqualTo(0);
		await Assert.That(recorder.Lines).Contains("hello");
	}

	[Test]
	public async Task MalformedEnvironCommandByteDoesNotWedgeTheConnection()
	{
		var recorder = await Run([IAC, SB, 36, 99, IAC, SE, .. "hello\r\n"u8]);

		await Assert.That(recorder.EnvironEvents).IsEmpty();
		await Assert.That(recorder.Lines).Contains("hello");
	}

	[Test]
	public async Task MalformedNewEnvironCommandByteDoesNotWedgeTheConnection()
	{
		var recorder = await Run([IAC, SB, 39, 99, IAC, SE, .. "hello\r\n"u8]);

		await Assert.That(recorder.NewEnvironEvents).IsEmpty();
		await Assert.That(recorder.Lines).Contains("hello");
	}

	[Test]
	public async Task MalformedLineModeCommandByteDoesNotWedgeTheConnection()
	{
		var recorder = await Run([IAC, SB, 34, 99, IAC, SE, .. "hello\r\n"u8]);

		await Assert.That(recorder.LineModeMessages).IsEmpty();
		await Assert.That(recorder.Lines).Contains("hello");
	}

	[Test]
	public async Task MalformedMccp1ByteBeforeWillDoesNotWedgeTheConnection()
	{
		// Mccp1's marker has no doubling at all: IAC SB COMPRESS WILL SE. A byte other than WILL right
		// after the option byte used to have no exit from Mccp1 at all.
		var recorder = await Run([IAC, SB, 85, 99, IAC, SE, .. "hello\r\n"u8]);

		await Assert.That(recorder.Mccp1Markers).IsEqualTo(0);
		await Assert.That(recorder.Lines).Contains("hello");
	}

	// ---------------------------------------------------------------------------------------------
	// Misfire: garbage between a marker's real IAC and an unrelated later SE must not complete it.
	// ---------------------------------------------------------------------------------------------

	[Test]
	public async Task Mccp2GarbageBetweenIacAndSeDoesNotFireTheMarker()
	{
		// IAC (would-be end) then garbage then SE: without resetting Escaping, the guard on the SE
		// below would still pass.
		var recorder = await Run([IAC, SB, 86, IAC, 1, SE]);

		await Assert.That(recorder.Mccp2Markers).IsEqualTo(0);
	}

	[Test]
	public async Task Mccp2StillFiresOnAGenuineMarkerAfterRecovering()
	{
		var recorder = await Run([IAC, SB, 86, IAC, 1, SE, .. new byte[] { IAC, SB, 86, IAC, SE }]);

		await Assert.That(recorder.Mccp2Markers).IsEqualTo(1);
	}

	[Test]
	public async Task Mccp3GarbageBetweenIacAndSeDoesNotFireTheMarker()
	{
		var recorder = await Run([IAC, SB, 87, IAC, 1, SE]);

		await Assert.That(recorder.Mccp3Markers).IsEqualTo(0);
	}

	[Test]
	public async Task Mccp1GarbageAfterWillDoesNotFireTheMarker()
	{
		// WILL (correct so far) then garbage then SE: without re-arming back to Mccp1, the unconditional
		// SE transition below would still fire.
		var recorder = await Run([IAC, SB, 85, WILL, 1, SE]);

		await Assert.That(recorder.Mccp1Markers).IsEqualTo(0);
	}

	[Test]
	public async Task Mccp1StillFiresOnAGenuineMarkerAfterRecovering()
	{
		// The garbage after WILL re-arms to Mccp1 (proven above), whose own malformed byte then routes
		// into the shared discard-until-IAC-SE state -- so this first attempt must be closed out with a
		// real IAC SE of its own before a genuinely independent second attempt can be recognised as one.
		var recorder = await Run([IAC, SB, 85, WILL, 1, SE, IAC, SE, .. new byte[] { IAC, SB, 85, WILL, SE }]);

		await Assert.That(recorder.Mccp1Markers).IsEqualTo(1);
	}

	[Test]
	public async Task MxpGarbageBetweenIacAndSeDoesNotFireTheMarker()
	{
		var recorder = await Run([IAC, SB, 91, IAC, 1, SE]);

		await Assert.That(recorder.MxpStarts).IsEqualTo(0);
	}

	[Test]
	public async Task TerminalTypeSendGarbageBetweenIacAndSeDoesNotFireRequested()
	{
		var recorder = await Run([IAC, SB, 24, 1 /* SEND */, IAC, 1, SE]);

		await Assert.That(recorder.TerminalTypeRequests).IsEqualTo(0);
	}

	[Test]
	public async Task XDisplaySendGarbageBetweenIacAndSeDoesNotFireRequested()
	{
		var recorder = await Run([IAC, SB, 35, 1 /* SEND */, IAC, 1, SE]);

		await Assert.That(recorder.XDisplayLocationRequests).IsEqualTo(0);
	}

	[Test]
	public async Task CharsetEndingGarbageBetweenIacAndSeDoesNotFireRejected()
	{
		var recorder = await Run([IAC, SB, 42, 3 /* REJECTED */, IAC, 1, SE]);

		await Assert.That(recorder.CharsetRejections).IsEqualTo(0);
	}

	[Test]
	public async Task CharsetEndingStillFiresOnAGenuineMarkerAfterRecovering()
	{
		var recorder = await Run([IAC, SB, 42, 3 /* REJECTED */, IAC, 1, SE, .. new byte[] { IAC, SB, 42, 3, IAC, SE }]);

		await Assert.That(recorder.CharsetRejections).IsEqualTo(1);
	}

	// ---------------------------------------------------------------------------------------------
	// Overflow: an oversized payload must not be buffered without limit, and must not be delivered
	// as if it were the whole (truncated) message once the limit is hit.
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// <paramref name="count"/> bytes of filler, none of them IAC, SE, WILL or SB, so they cannot be
	/// mistaken for framing while accumulating toward an overflow.
	/// </summary>
	private static byte[] Filler(int count)
	{
		var buffer = new byte[count];
		System.Array.Fill(buffer, (byte)'x');
		return buffer;
	}

	[Test]
	public async Task OversizedTerminalTypeReportIsNotDelivered()
	{
		var recorder = await Run([IAC, SB, 24, 0 /* IS */, .. Filler(TerminalTypeModule.MaxTextBytes + 1), IAC, SE]);

		await Assert.That(recorder.TerminalTypeReports).IsEmpty();
	}

	[Test]
	public async Task OversizedXDisplayReportIsNotDelivered()
	{
		var recorder = await Run([IAC, SB, 35, 0 /* IS */, .. Filler(XDisplayModule.MaxTextBytes + 1), IAC, SE]);

		await Assert.That(recorder.XDisplayLocationReports).IsEmpty();
	}

	[Test]
	public async Task OversizedAuthenticationSendIsNotDelivered()
	{
		var recorder = await Run([IAC, SB, 37, 1 /* SEND */, .. Filler(AuthenticationModule.MaxDataBytes + 1), IAC, SE]);

		await Assert.That(recorder.AuthenticationSends).IsEmpty();
	}

	[Test]
	public async Task OversizedEncryptionSupportIsNotDelivered()
	{
		var recorder = await Run([IAC, SB, 38, 1 /* SUPPORT */, .. Filler(EncryptionModule.MaxDataBytes + 1), IAC, SE]);

		await Assert.That(recorder.EncryptionSends).IsEmpty();
	}

	[Test]
	public async Task OversizedEncryptionStartKeyIdIsNotDelivered()
	{
		var recorder = await Run([IAC, SB, 38, 3 /* START */, .. Filler(EncryptionModule.MaxDataBytes + 1), IAC, SE]);

		await Assert.That(recorder.EncryptionStarts).IsEmpty();
	}

	[Test]
	public async Task OversizedLineModeDataIsNotDelivered()
	{
		var recorder = await Run([IAC, SB, 34, 1 /* MODE */, .. Filler(LineModeModule.MaxDataBytes + 1), IAC, SE]);

		await Assert.That(recorder.LineModeMessages).IsEmpty();
	}
}
