using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class MXPTests : BaseTest
{
	[Test]
	public async Task ServerAnnouncesMXPOnBuild()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		// Assert - Server should announce WILL MXP during initialization
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientRespondsDoMXPToServerWill()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		// Act - Server sends WILL MXP
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });
		await client_ti.WaitForProcessingAsync();

		// Assert - Client should respond with DO MXP
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP });

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerMXPEnabledOnClientDo()
	{
		// Arrange
		byte[] negotiationOutput = null;
		bool mxpEnabledCallbackFired = false;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
				.OnMXPEnabled(() =>
				{
					mxpEnabledCallbackFired = true;
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		var mxpPlugin = server_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - Client sends DO MXP
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP });
		await server_ti.WaitForProcessingAsync();

		var gotCallback = await PollUntilAsync(() => mxpEnabledCallbackFired);

		// Assert
		await Assert.That(gotCallback).IsTrue();
		await Assert.That(mxpPlugin!.IsMXPActive).IsTrue();

		await server_ti.DisposeAsync();
	}

	/// <summary>
	/// WILL/DO settles the telnet option, and nothing more. MXP mode itself begins at the server's
	/// IAC SB MXP IAC SE, so the activation callback must not fire yet.
	/// </summary>
	[Test]
	public async Task ClientMXPEnabledOnServerWill()
	{
		// Arrange
		bool mxpEnabledCallbackFired = false;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
				.OnMXPEnabled(() =>
				{
					mxpEnabledCallbackFired = true;
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		var mxpPlugin = client_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - Server sends WILL MXP
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });
		await client_ti.WaitForProcessingAsync();

		var negotiated = await PollUntilAsync(() => mxpPlugin!.IsMXPActive);

		// Assert
		await Assert.That(negotiated).IsTrue();
		await Assert.That(mxpPlugin!.IsMxpModeStarted).IsFalse();
		await Assert.That(mxpEnabledCallbackFired).IsFalse();

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerMXPDisabledOnClientDont()
	{
		// Arrange
		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		var mxpPlugin = server_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - Client sends DONT MXP
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MXP });
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(100);

		// Assert
		await Assert.That(mxpPlugin!.IsMXPActive).IsFalse();

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientMXPDisabledOnServerWont()
	{
		// Arrange
		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		var mxpPlugin = client_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - Server sends WONT MXP
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MXP });
		await client_ti.WaitForProcessingAsync();
		await Task.Delay(100);

		// Assert
		await Assert.That(mxpPlugin!.IsMXPActive).IsFalse();

		await client_ti.DisposeAsync();
	}

	/// <summary>
	/// The step this library skipped: MXP does not begin at DO. Both Zuggsoft's specification and
	/// Gammon's server-implementation guide have the server answer IAC DO MXP with
	/// IAC SB MXP IAC SE, and a client that never sees that marker -- MUSHclient among them --
	/// stays in plain telnet and prints every tag and entity the server writes literally.
	/// </summary>
	[Test]
	public async Task ServerSendsMxpSubnegotiationOnClientDo()
	{
		// Arrange
		var negotiationOutput = new List<byte>();

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			lock (negotiationOutput) { negotiationOutput.AddRange(arg1.ToArray()); }
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		var mxpPlugin = server_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - Client sends DO MXP
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP });
		await server_ti.WaitForProcessingAsync();

		var sentMarker = await PollUntilAsync(() => CountOccurrences(Snapshot(negotiationOutput), MxpStartMarker) == 1);

		// Assert
		await Assert.That(sentMarker).IsTrue();
		await Assert.That(mxpPlugin!.IsMxpModeStarted).IsTrue();

		await server_ti.DisposeAsync();
	}

	/// <summary>
	/// The marker means "MXP output starts here", so a re-affirmed DO in the middle of a session
	/// whose tags are already flowing must not restate it -- nor re-run the host's activation
	/// callback, which on a MUSH is what switches the connection's renderer over.
	/// </summary>
	[Test]
	public async Task ServerDoesNotRepeatMxpSubnegotiationOnSecondDo()
	{
		// Arrange
		var negotiationOutput = new List<byte>();
		var callbackCount = 0;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			lock (negotiationOutput) { negotiationOutput.AddRange(arg1.ToArray()); }
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
				.OnMXPEnabled(() =>
				{
					Interlocked.Increment(ref callbackCount);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		// Act - Client sends DO MXP twice
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP });
		await server_ti.WaitForProcessingAsync();
		await PollUntilAsync(() => Volatile.Read(ref callbackCount) == 1);

		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP });
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(100);

		// Assert
		await Assert.That(CountOccurrences(Snapshot(negotiationOutput), MxpStartMarker)).IsEqualTo(1);
		await Assert.That(Volatile.Read(ref callbackCount)).IsEqualTo(1);

		await server_ti.DisposeAsync();
	}

	/// <summary>
	/// The receiving half of the same marker. Before this was wired the client had no
	/// SubNegotiation transition for option 91 at all, so a correct server's IAC SB MXP IAC SE fell
	/// through to the safety net and was skipped as an unsupported subnegotiation.
	/// </summary>
	[Test]
	public async Task ClientMxpModeStartsOnServerSubnegotiation()
	{
		// Arrange
		var callbackCount = 0;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
				.OnMXPEnabled(() =>
				{
					Interlocked.Increment(ref callbackCount);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		var mxpPlugin = client_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - Server sends WILL MXP, then the start marker
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });
		await client_ti.WaitForProcessingAsync();
		await client_ti.InterpretByteArrayAsync(MxpStartMarker);
		await client_ti.WaitForProcessingAsync();

		var started = await PollUntilAsync(() => mxpPlugin!.IsMxpModeStarted);

		// Assert
		await Assert.That(started).IsTrue();
		await Assert.That(Volatile.Read(ref callbackCount)).IsEqualTo(1);

		await client_ti.DisposeAsync();
	}

	/// <summary>
	/// A server may repeat the marker; the session is already in MXP mode and the host callback
	/// must not run a second time for it.
	/// </summary>
	[Test]
	public async Task ClientIgnoresRepeatedMxpSubnegotiation()
	{
		// Arrange
		var callbackCount = 0;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
				.OnMXPEnabled(() =>
				{
					Interlocked.Increment(ref callbackCount);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		// Act
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });
		await client_ti.WaitForProcessingAsync();
		await client_ti.InterpretByteArrayAsync(MxpStartMarker);
		await client_ti.WaitForProcessingAsync();
		await PollUntilAsync(() => Volatile.Read(ref callbackCount) == 1);

		await client_ti.InterpretByteArrayAsync(MxpStartMarker);
		await client_ti.WaitForProcessingAsync();
		await Task.Delay(100);

		// Assert
		await Assert.That(Volatile.Read(ref callbackCount)).IsEqualTo(1);

		await client_ti.DisposeAsync();
	}

	/// <summary>
	/// Ordinary text after the marker still reaches the host application: the marker is a telnet
	/// subnegotiation and changes nothing about how the byte stream is framed, unlike MCCP's.
	/// </summary>
	[Test]
	public async Task ClientReadsTextFollowingTheMxpSubnegotiation()
	{
		// Arrange
		string submitted = null;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				submitted = encoding.GetString(data);
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		// Act
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });
		await client_ti.WaitForProcessingAsync();
		byte[] markerThenLine = [.. MxpStartMarker, .. Encoding.ASCII.GetBytes("Room Zero\r\n")];
		await client_ti.InterpretByteArrayAsync(markerThenLine);
		await client_ti.WaitForProcessingAsync();

		var gotLine = await PollUntilAsync(() => submitted is not null);

		// Assert
		await Assert.That(gotLine).IsTrue();
		await Assert.That(submitted).IsEqualTo("Room Zero");

		await client_ti.DisposeAsync();
	}

	/// <summary>
	/// The marker says when a negotiated option begins; it cannot stand in for negotiating it. A peer
	/// that sends it cold must not switch this side into MXP mode, which would run the host's
	/// activation callback for an option the peer never asked for.
	/// </summary>
	[Test]
	public async Task ClientIgnoresAnMxpMarkerThatWasNeverNegotiated()
	{
		// Arrange
		var callbackCount = 0;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MXPProtocol>()
				.OnMXPEnabled(() =>
				{
					Interlocked.Increment(ref callbackCount);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		var mxpPlugin = client_ti.PluginManager!.GetPlugin<MXPProtocol>();

		// Act - the marker arrives with no WILL MXP behind it
		await client_ti.InterpretByteArrayAsync(MxpStartMarker);
		await client_ti.WaitForProcessingAsync();
		await Task.Delay(100);

		// Assert
		await Assert.That(mxpPlugin!.IsMXPActive).IsFalse();
		await Assert.That(mxpPlugin!.IsMxpModeStarted).IsFalse();
		await Assert.That(Volatile.Read(ref callbackCount)).IsEqualTo(0);

		await client_ti.DisposeAsync();
	}

	/// <summary>IAC SB MXP IAC SE -- the marker that starts MXP mode.</summary>
	private static readonly byte[] MxpStartMarker =
		[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MXP, (byte)Trigger.IAC, (byte)Trigger.SE];

	private static byte[] Snapshot(List<byte> buffer)
	{
		lock (buffer) { return [.. buffer]; }
	}

	private static int CountOccurrences(byte[] haystack, byte[] needle)
	{
		var count = 0;
		for (var i = 0; i + needle.Length <= haystack.Length; i++)
		{
			if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) count++;
		}
		return count;
	}
}
