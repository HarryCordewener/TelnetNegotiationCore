using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class GMCPTests : BaseTest
{
	[Test]
	public async Task ServerCanSendGMCPMessage()
	{
		// Arrange
		byte[] negotiationOutput = null;
		(string Package, string Info)? receivedGMCP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToGMCP((string Package, string Info) tuple)
		{
			receivedGMCP = tuple;
			logger.LogInformation("Received GMCP: Package={Package}, Info={Info}", tuple.Package, tuple.Info);
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(WriteBackToGMCP)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = server_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "My Telnet Negotiated Server",
			UTF_8 = true,
			Gameplay = ["ABC", "DEF"],
			Extended = new Dictionary<string, dynamic>
			{
				{ "Foo", "Bar"},
				{ "Baz", (string[]) ["Moo", "Meow"] }
			}
		});

		var package = "Core.Hello";
		var message = "{\"client\":\"TestClient\",\"version\":\"1.0\"}";

		// Act
		await server_ti.SendGMCPCommand(package, message);

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		
		// Verify the message format: IAC SB GMCP <package> <space> <message> IAC SE
		var encoding = server_ti.CurrentEncoding;
		var expectedBytes = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.GMCP
		};
		expectedBytes.AddRange(encoding.GetBytes(package));
		expectedBytes.AddRange(encoding.GetBytes(" "));
		expectedBytes.AddRange(encoding.GetBytes(message));
		expectedBytes.Add((byte)Trigger.IAC);
		expectedBytes.Add((byte)Trigger.SE);

		await AssertByteArraysEqual(negotiationOutput, expectedBytes.ToArray());

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientCanSendGMCPMessage()
	{
		// Arrange
		byte[] negotiationOutput = null;
		(string Package, string Info)? receivedGMCP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToGMCP((string Package, string Info) tuple)
		{
			receivedGMCP = tuple;
			logger.LogInformation("Received GMCP: Package={Package}, Info={Info}", tuple.Package, tuple.Info);
			return ValueTask.CompletedTask;
		}

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(WriteBackToGMCP)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var clientMssp = client_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		clientMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "My Telnet Negotiated Client",
			UTF_8 = true,
			Gameplay = ["ABC", "DEF"],
			Extended = new Dictionary<string, dynamic>
			{
				{ "Foo", "Bar"},
				{ "Baz", (string[]) ["Moo", "Meow"] }
			}
		});

		var package = "Core.Supports.Set";
		var message = "[\"Char 1\",\"Char.Skills 1\",\"Char.Items 1\"]";

		// Act
		await client_ti.SendGMCPCommand(package, message);

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		
		// Verify the message format
		var encoding = client_ti.CurrentEncoding;
		var expectedBytes = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.GMCP
		};
		expectedBytes.AddRange(encoding.GetBytes(package));
		expectedBytes.AddRange(encoding.GetBytes(" "));
		expectedBytes.AddRange(encoding.GetBytes(message));
		expectedBytes.Add((byte)Trigger.IAC);
		expectedBytes.Add((byte)Trigger.SE);

		await AssertByteArraysEqual(negotiationOutput, expectedBytes.ToArray());

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerCanReceiveGMCPMessage()
	{
		// Arrange
		byte[] negotiationOutput = null;
		(string Package, string Info)? receivedGMCP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToGMCP((string Package, string Info) tuple)
		{
			receivedGMCP = tuple;
			logger.LogInformation("Received GMCP: Package={Package}, Info={Info}", tuple.Package, tuple.Info);
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(WriteBackToGMCP)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = server_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "My Telnet Negotiated Server",
			UTF_8 = true,
			Gameplay = ["ABC", "DEF"],
			Extended = new Dictionary<string, dynamic>
			{
				{ "Foo", "Bar"},
				{ "Baz", (string[]) ["Moo", "Meow"] }
			}
		});

		// Complete GMCP negotiation first
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP });
		await server_ti.WaitForProcessingAsync();
		receivedGMCP = null; // Reset after negotiation

		var package = "Core.Hello";
		var message = "{\"client\":\"TestClient\",\"version\":\"1.0\"}";
		var encoding = Encoding.ASCII;
		
		var gmcpBytes = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.GMCP
		};
		gmcpBytes.AddRange(encoding.GetBytes(package));
		gmcpBytes.AddRange(encoding.GetBytes(" "));
		gmcpBytes.AddRange(encoding.GetBytes(message));
		gmcpBytes.Add((byte)Trigger.IAC);
		gmcpBytes.Add((byte)Trigger.SE);

		// Act
		await server_ti.InterpretByteArrayAsync(gmcpBytes.ToArray());
		await server_ti.WaitForProcessingAsync();
		
		// Poll until callback fires
		var gotMessage = await PollUntilAsync(() => receivedGMCP != null);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for GMCP message callback. receivedGMCP is null");
		}

		// Assert
		await Assert.That(receivedGMCP).IsNotNull();
		await Assert.That(receivedGMCP.Value.Package).IsEqualTo(package);
		await Assert.That(receivedGMCP.Value.Info).IsEqualTo(message);

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientCanReceiveGMCPMessage()
	{
		// Arrange
		byte[] negotiationOutput = null;
		(string Package, string Info)? receivedGMCP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToGMCP((string Package, string Info) tuple)
		{
			receivedGMCP = tuple;
			logger.LogInformation("Received GMCP: Package={Package}, Info={Info}", tuple.Package, tuple.Info);
			return ValueTask.CompletedTask;
		}

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(WriteBackToGMCP)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var clientMssp = client_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		clientMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "My Telnet Negotiated Client",
			UTF_8 = true,
			Gameplay = ["ABC", "DEF"],
			Extended = new Dictionary<string, dynamic>
			{
				{ "Foo", "Bar"},
				{ "Baz", (string[]) ["Moo", "Meow"] }
			}
		});

		// Complete GMCP negotiation first
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
		await client_ti.WaitForProcessingAsync();
		receivedGMCP = null; // Reset after negotiation

		var package = "Char.Vitals";
		var message = "{\"hp\":1000,\"maxhp\":1500,\"mp\":500,\"maxmp\":800}";
		var encoding = Encoding.ASCII;
		
		var gmcpBytes = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.GMCP
		};
		gmcpBytes.AddRange(encoding.GetBytes(package));
		gmcpBytes.AddRange(encoding.GetBytes(" "));
		gmcpBytes.AddRange(encoding.GetBytes(message));
		gmcpBytes.Add((byte)Trigger.IAC);
		gmcpBytes.Add((byte)Trigger.SE);

		// Act
		await client_ti.InterpretByteArrayAsync(gmcpBytes.ToArray());
		await client_ti.WaitForProcessingAsync();
		
		// Poll until callback fires
		var gotMessage = await PollUntilAsync(() => receivedGMCP != null);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for GMCP message callback. receivedGMCP is null");
		}

		// Assert
		await Assert.That(receivedGMCP).IsNotNull();
		await Assert.That(receivedGMCP.Value.Package).IsEqualTo(package);
		await Assert.That(receivedGMCP.Value.Info).IsEqualTo(message);

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task GMCPMessageWithComplexJSON()
	{
		// Arrange
		byte[] negotiationOutput = null;
		(string Package, string Info)? receivedGMCP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToGMCP((string Package, string Info) tuple)
		{
			receivedGMCP = tuple;
			logger.LogInformation("Received GMCP: Package={Package}, Info={Info}", tuple.Package, tuple.Info);
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(WriteBackToGMCP)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = server_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "My Telnet Negotiated Server",
			UTF_8 = true,
			Gameplay = ["ABC", "DEF"],
			Extended = new Dictionary<string, dynamic>
			{
				{ "Foo", "Bar"},
				{ "Baz", (string[]) ["Moo", "Meow"] }
			}
		});

		// Complete GMCP negotiation first
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP });
		await server_ti.WaitForProcessingAsync();
		receivedGMCP = null;

		var package = "Room.Info";
		var message = "{\"num\":12345,\"name\":\"A dark room\",\"area\":\"The Dungeon\",\"exits\":{\"n\":12346,\"s\":12344}}";
		var encoding = Encoding.ASCII;
		
		var gmcpBytes = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.GMCP
		};
		gmcpBytes.AddRange(encoding.GetBytes(package));
		gmcpBytes.AddRange(encoding.GetBytes(" "));
		gmcpBytes.AddRange(encoding.GetBytes(message));
		gmcpBytes.Add((byte)Trigger.IAC);
		gmcpBytes.Add((byte)Trigger.SE);

		// Act
		await server_ti.InterpretByteArrayAsync(gmcpBytes.ToArray());
		await server_ti.WaitForProcessingAsync();
		
		// Poll until callback fires
		var gotMessage = await PollUntilAsync(() => receivedGMCP != null);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for GMCP message callback. receivedGMCP is null");
		}

		// Assert
		await Assert.That(receivedGMCP).IsNotNull();
		await Assert.That(receivedGMCP.Value.Package).IsEqualTo(package);
		await Assert.That(receivedGMCP.Value.Info).IsEqualTo(message);

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task GMCPNegotiationClientWillRespond()
	{
		// Arrange
		byte[] negotiationOutput = null;
		(string Package, string Info)? receivedGMCP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToGMCP((string Package, string Info) tuple)
		{
			receivedGMCP = tuple;
			logger.LogInformation("Received GMCP: Package={Package}, Info={Info}", tuple.Package, tuple.Info);
			return ValueTask.CompletedTask;
		}

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(WriteBackToGMCP)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var clientMssp = client_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		clientMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "My Telnet Negotiated Client",
			UTF_8 = true,
			Gameplay = ["ABC", "DEF"],
			Extended = new Dictionary<string, dynamic>
			{
				{ "Foo", "Bar"},
				{ "Baz", (string[]) ["Moo", "Meow"] }
			}
		});

		// Act
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
		await client_ti.WaitForProcessingAsync();

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP });

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task GMCPNegotiationServerWillAnnounce()
	{
		// Arrange
		byte[] negotiationOutput = null;
		(string Package, string Info)? receivedGMCP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToGMCP((string Package, string Info) tuple)
		{
			receivedGMCP = tuple;
			logger.LogInformation("Received GMCP: Package={Package}, Info={Info}", tuple.Package, tuple.Info);
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(WriteBackToGMCP)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = server_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "My Telnet Negotiated Server",
			UTF_8 = true,
			Gameplay = ["ABC", "DEF"],
			Extended = new Dictionary<string, dynamic>
			{
				{ "Foo", "Bar"},
				{ "Baz", (string[]) ["Moo", "Meow"] }
			}
		});

		// Assert
		// The server should announce WILL GMCP during initialization
		// This is done in the SetupGMCPNegotiation method
		// We can verify the negotiation output was set during build
		await Assert.That(negotiationOutput).IsNotNull();

		await server_ti.DisposeAsync();
	}

	/// <summary>
	/// Wraps a package and payload in a GMCP subnegotiation: IAC SB GMCP package ' ' payload IAC SE.
	/// </summary>
	private static byte[] GMCPFrame(string package, string payload)
	{
		var encoding = Encoding.ASCII;
		var frame = new List<byte>(package.Length + payload.Length + 6)
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.GMCP
		};
		frame.AddRange(encoding.GetBytes(package));
		frame.Add((byte)' ');
		frame.AddRange(encoding.GetBytes(payload));
		frame.Add((byte)Trigger.IAC);
		frame.Add((byte)Trigger.SE);
		return frame.ToArray();
	}

	/// <summary>
	/// Builds a client-mode interpreter that has already agreed to GMCP, delivering every
	/// received message into <paramref name="received"/>.
	/// </summary>
	private static async Task<TelnetInterpreter> BuildNegotiatedGMCPClientAsync(
		List<(string Package, string Info)> received)
	{
		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(message =>
				{
					received.Add(message);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
		await client_ti.WaitForProcessingAsync();
		received.Clear();

		return client_ti;
	}

	/// <summary>
	/// A GMCP payload of any size the peer chooses to send must arrive intact. Neither the GMCP
	/// specification nor the Aardwolf/IRE variants define a maximum message size, and packages such
	/// as Char.Items.List or a room player list routinely run past 8KB. Truncating one produces
	/// invalid JSON that the consumer cannot tell from a malformed server.
	/// </summary>
	[Test]
	public async Task LargeGMCPMessageArrivesWhole()
	{
		// Arrange
		var received = new List<(string Package, string Info)>();
		var client_ti = await BuildNegotiatedGMCPClientAsync(received);

		var package = "Char.Items.List";
		var message = "{\"location\":\"inv\",\"items\":\"" + new string('x', 32 * 1024) + "\"}";

		// Act
		await client_ti.InterpretByteArrayAsync(GMCPFrame(package, message));
		await client_ti.WaitForProcessingAsync(maxWaitMs: 30000);

		var gotMessage = await PollUntilAsync(() => received.Count > 0, timeoutMs: 30000);
		if (!gotMessage)
		{
			throw new Exception("Timeout waiting for GMCP message callback. Nothing was delivered.");
		}

		// Assert
		await Assert.That(received[0].Package).IsEqualTo(package);
		await Assert.That(received[0].Info.Length).IsEqualTo(message.Length);
		await Assert.That(received[0].Info).IsEqualTo(message);

		await client_ti.DisposeAsync();
	}

	/// <summary>
	/// The boundary: package, separator and payload together used to be capped at 8192 bytes, so a
	/// two-character package name plus 8190 bytes of payload lost its last byte - silently.
	/// </summary>
	[Test]
	public async Task GMCPMessageOneByteOverTheOldEightKilobyteLimitArrivesWhole()
	{
		// Arrange
		var received = new List<(string Package, string Info)>();
		var client_ti = await BuildNegotiatedGMCPClientAsync(received);

		// "Ab" + ' ' + 8190 == 8193 bytes of subnegotiation payload.
		var package = "Ab";
		var message = new string('x', 8190);

		// Act
		await client_ti.InterpretByteArrayAsync(GMCPFrame(package, message));
		await client_ti.WaitForProcessingAsync(maxWaitMs: 30000);

		var gotMessage = await PollUntilAsync(() => received.Count > 0, timeoutMs: 30000);
		if (!gotMessage)
		{
			throw new Exception("Timeout waiting for GMCP message callback. Nothing was delivered.");
		}

		// Assert
		await Assert.That(received[0].Package).IsEqualTo(package);
		await Assert.That(received[0].Info.Length).IsEqualTo(8190);

		await client_ti.DisposeAsync();
	}

	/// <summary>
	/// Small messages are the overwhelmingly common case, and one arriving after an oversized one
	/// proves the accumulator is reset rather than left holding the previous message's bytes.
	/// </summary>
	[Test]
	public async Task SmallGMCPMessageAfterALargeOneStillArrivesWhole()
	{
		// Arrange
		var received = new List<(string Package, string Info)>();
		var client_ti = await BuildNegotiatedGMCPClientAsync(received);

		// Act
		await client_ti.InterpretByteArrayAsync(GMCPFrame("Char.Items.List", new string('x', 20000)));
		await client_ti.WaitForProcessingAsync(maxWaitMs: 30000);
		await PollUntilAsync(() => received.Count > 0, timeoutMs: 30000);

		await client_ti.InterpretByteArrayAsync(GMCPFrame("Char.Vitals", "{\"hp\":1000}"));
		await client_ti.WaitForProcessingAsync(maxWaitMs: 30000);

		var gotSecond = await PollUntilAsync(() => received.Count > 1, timeoutMs: 30000);
		if (!gotSecond)
		{
			throw new Exception($"Timeout waiting for the second GMCP message. Delivered: {received.Count}");
		}

		// Assert
		await Assert.That(received[^1].Package).IsEqualTo("Char.Vitals");
		await Assert.That(received[^1].Info).IsEqualTo("{\"hp\":1000}");

		await client_ti.DisposeAsync();
	}

	/// <summary>
	/// A bound has to remain - GMCP arrives on the read loop from an untrusted peer - but reaching
	/// it must be reported rather than passed off as a complete message. The connection also has to
	/// survive it: the next message arrives normally.
	/// </summary>
	[Test]
	public async Task GMCPMessageBeyondTheConfiguredCeilingIsDroppedAndReported()
	{
		// Arrange
		var received = new List<(string Package, string Info)>();
		(string Package, long ReceivedBytes, int MaxMessageSize)? tooLarge = null;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(message =>
				{
					received.Add(message);
					return ValueTask.CompletedTask;
				})
				.WithMaxMessageSize(4096)
				.OnGMCPMessageTooLarge(overflow =>
				{
					tooLarge = overflow;
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
		await client_ti.WaitForProcessingAsync();
		received.Clear();

		// Act: "Char.Items.List" + ' ' + 8000 == 8016 bytes against a 4096 byte ceiling.
		await client_ti.InterpretByteArrayAsync(GMCPFrame("Char.Items.List", new string('x', 8000)));
		await client_ti.WaitForProcessingAsync(maxWaitMs: 30000);

		var reported = await PollUntilAsync(() => tooLarge != null, timeoutMs: 30000);
		if (!reported)
		{
			throw new Exception("Timeout waiting for the oversized-message callback.");
		}

		// Assert: dropped, not truncated, and the consumer was told why.
		await Assert.That(received.Count).IsEqualTo(0);
		await Assert.That(tooLarge!.Value.Package).IsEqualTo("Char.Items.List");
		await Assert.That(tooLarge!.Value.MaxMessageSize).IsEqualTo(4096);
		await Assert.That(tooLarge!.Value.ReceivedBytes).IsEqualTo(8016);

		// The connection keeps working.
		await client_ti.InterpretByteArrayAsync(GMCPFrame("Char.Vitals", "{\"hp\":1000}"));
		await client_ti.WaitForProcessingAsync(maxWaitMs: 30000);

		var gotNext = await PollUntilAsync(() => received.Count > 0, timeoutMs: 30000);
		if (!gotNext)
		{
			throw new Exception("Timeout waiting for the GMCP message following an oversized one.");
		}

		await Assert.That(received[0].Package).IsEqualTo("Char.Vitals");
		await Assert.That(received[0].Info).IsEqualTo("{\"hp\":1000}");

		await client_ti.DisposeAsync();
	}

	/// <summary>
	/// The ceiling is a byte count, so it must reject a value that cannot be one.
	/// </summary>
	[Test]
	public async Task GMCPMaxMessageSizeRejectsNonPositiveValues()
	{
		var gmcp = new GMCPProtocol();

		await Assert.That(gmcp.MaxMessageSize).IsEqualTo(1024 * 1024);
		await Assert.That(() => gmcp.WithMaxMessageSize(0)).Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => gmcp.WithMaxMessageSize(-1)).Throws<ArgumentOutOfRangeException>();
	}
}
