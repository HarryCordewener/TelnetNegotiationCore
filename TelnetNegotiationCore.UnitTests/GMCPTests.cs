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
	/// <summary>
	/// Polls for a condition with timeout, useful for async callback assertions
	/// </summary>
	private static async Task<bool> PollUntilAsync(Func<bool> condition, int timeoutMs = 2000, int pollIntervalMs = 10)
	{
		var waitedMs = 0;
		while (!condition() && waitedMs < timeoutMs)
		{
			await Task.Delay(pollIntervalMs);
			waitedMs += pollIntervalMs;
		}
		return condition();
	}

	[Test]
	public async Task ServerCanSendGMCPMessage()
	{
		// Arrange
		byte[] negotiationOutput = null;
		(string Package, string Info)? receivedGMCP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
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

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
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

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
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

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
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

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
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

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
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

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
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
}
