using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

[TestFixture]
public class GMCPTests : BaseTest
{
	private TelnetInterpreter _server_ti;
	private TelnetInterpreter _client_ti;
	private byte[] _negotiationOutput;
	private (string Package, string Info)? _receivedGMCP;

	private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

	private ValueTask WriteBackToNegotiate(byte[] arg1)
	{
		_negotiationOutput = arg1;
		return ValueTask.CompletedTask;
	}

	private ValueTask WriteBackToGMCP((string Package, string Info) tuple)
	{
		_receivedGMCP = tuple;
		logger.LogInformation("Received GMCP: Package={Package}, Info={Info}", tuple.Package, tuple.Info);
		return ValueTask.CompletedTask;
	}

	[SetUp]
	public async Task Setup()
	{
		_receivedGMCP = null;
		_negotiationOutput = null;

		_server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverGmcp = _server_ti.PluginManager!.GetPlugin<GMCPProtocol>();
		serverGmcp!.OnGMCPReceived = WriteBackToGMCP;

		var serverMssp = _server_ti.PluginManager!.GetPlugin<MSSPProtocol>();
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

		_client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var clientGmcp = _client_ti.PluginManager!.GetPlugin<GMCPProtocol>();
		clientGmcp!.OnGMCPReceived = WriteBackToGMCP;

		var clientMssp = _client_ti.PluginManager!.GetPlugin<MSSPProtocol>();
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
	}

	[TearDown]
	public async Task TearDown()
	{
		if (_server_ti != null)
			await _server_ti.DisposeAsync();
		if (_client_ti != null)
			await _client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerCanSendGMCPMessage()
	{
		// Arrange
		var package = "Core.Hello";
		var message = "{\"client\":\"TestClient\",\"version\":\"1.0\"}";

		// Act
		await _server_ti.SendGMCPCommand(package, message);

		// Assert
		Assert.IsNotNull(_negotiationOutput, "Negotiation output should not be null");
		
		// Verify the message format: IAC SB GMCP <package> <space> <message> IAC SE
		var encoding = _server_ti.CurrentEncoding;
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

		CollectionAssert.AreEqual(expectedBytes, _negotiationOutput);
	}

	[Test]
	public async Task ClientCanSendGMCPMessage()
	{
		// Arrange
		var package = "Core.Supports.Set";
		var message = "[\"Char 1\",\"Char.Skills 1\",\"Char.Items 1\"]";

		// Act
		await _client_ti.SendGMCPCommand(package, message);

		// Assert
		Assert.IsNotNull(_negotiationOutput, "Negotiation output should not be null");
		
		// Verify the message format
		var encoding = _client_ti.CurrentEncoding;
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

		CollectionAssert.AreEqual(expectedBytes, _negotiationOutput);
	}

	[Test]
	public async Task ServerCanReceiveGMCPMessage()
	{
		// Arrange - Complete GMCP negotiation first
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP });
		await _server_ti.WaitForProcessingAsync();
		_receivedGMCP = null; // Reset after negotiation

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
		await _server_ti.InterpretByteArrayAsync(gmcpBytes.ToArray());
		await _server_ti.WaitForProcessingAsync();

		// Assert
		Assert.IsNotNull(_receivedGMCP, "Should have received GMCP message");
		Assert.AreEqual(package, _receivedGMCP.Value.Package, "Package name should match");
		Assert.AreEqual(message, _receivedGMCP.Value.Info, "Message content should match");
	}

	[Test]
	public async Task ClientCanReceiveGMCPMessage()
	{
		// Arrange - Complete GMCP negotiation first
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
		await _client_ti.WaitForProcessingAsync();
		_receivedGMCP = null; // Reset after negotiation

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
		await _client_ti.InterpretByteArrayAsync(gmcpBytes.ToArray());
		await _client_ti.WaitForProcessingAsync();

		// Assert
		Assert.IsNotNull(_receivedGMCP, "Should have received GMCP message");
		Assert.AreEqual(package, _receivedGMCP.Value.Package, "Package name should match");
		Assert.AreEqual(message, _receivedGMCP.Value.Info, "Message content should match");
	}

	[Test]
	public async Task GMCPMessageWithComplexJSON()
	{
		// Arrange - Complete GMCP negotiation first
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP });
		await _server_ti.WaitForProcessingAsync();
		_receivedGMCP = null;

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
		await _server_ti.InterpretByteArrayAsync(gmcpBytes.ToArray());
		await _server_ti.WaitForProcessingAsync();

		// Assert
		Assert.IsNotNull(_receivedGMCP, "Should have received GMCP message");
		Assert.AreEqual(package, _receivedGMCP.Value.Package, "Package name should match");
		Assert.AreEqual(message, _receivedGMCP.Value.Info, "Message content should match");
	}

	[Test]
	public async Task GMCPNegotiationClientWillRespond()
	{
		// Act
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
		await _client_ti.WaitForProcessingAsync();

		// Assert
		Assert.IsNotNull(_negotiationOutput);
		CollectionAssert.AreEqual(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP }, _negotiationOutput);
	}

	[Test]
	public void GMCPNegotiationServerWillAnnounce()
	{
		// The server should announce WILL GMCP during initialization
		// This is done in the SetupGMCPNegotiation method
		// We can verify the negotiation output was set during build
		Assert.IsNotNull(_negotiationOutput, "Server should have sent GMCP negotiation during build");
	}
}
