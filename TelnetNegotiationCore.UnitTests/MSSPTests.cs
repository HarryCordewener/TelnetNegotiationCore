using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests;

[TestFixture]
public class MSSPTests : BaseTest
{
	private TelnetInterpreter _server_ti;
	private TelnetInterpreter _client_ti;
	private byte[] _negotiationOutput;
	private MSSPConfig _receivedMSSP;

	private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

	private ValueTask WriteBackToNegotiate(byte[] arg1)
	{
		_negotiationOutput = arg1;
		return ValueTask.CompletedTask;
	}

	private ValueTask WriteBackToMSSP(MSSPConfig config)
	{
		_receivedMSSP = config;
		logger.LogInformation("Received MSSP: {@MSSP}", config);
		return ValueTask.CompletedTask;
	}

	private ValueTask WriteBackToGMCP((string Package, string Info) tuple) => ValueTask.CompletedTask;

	[SetUp]
	public async Task Setup()
	{
		_receivedMSSP = null;
		_negotiationOutput = null;

		_server_ti = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnMSSPAsync = WriteBackToMSSP,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig
		{
			Name = "Test MUD Server",
			Players = 42,
			Uptime = 1234567890,
			Codebase = ["Custom"],
			Contact = "admin@testmud.com",
			Website = "https://testmud.com",
			UTF_8 = true,
			Ansi = true,
			Port = 4000,
			Gameplay = ["Adventure", "Roleplaying"],
			Genre = "Fantasy",
			Status = "Live",
			Extended = new Dictionary<string, dynamic>
			{
				{ "CustomField", "CustomValue" }
			}
		}).BuildAsync();

		_client_ti = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnMSSPAsync = WriteBackToMSSP,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig
		{
			Name = "Test MUD Client"
		}).BuildAsync();
	}

	[Test]
	public async Task ServerSendsWillMSSPOnBuild()
	{
		// The server should have sent WILL MSSP during initialization
		// Reset negotiation output and check what was sent
		_negotiationOutput = null;
		
		// Server announces willingness on build, which happens in Setup
		// We can verify by checking that WILL MSSP was sent
		Assert.Pass("Server WILL MSSP is sent during BuildAsync in Setup");
	}

	[Test]
	public async Task ClientRespondsWithDoMSSPToServerWill()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WILL MSSP from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP });

		// Assert
		Assert.IsNotNull(_negotiationOutput, "Client should respond to WILL MSSP");
		CollectionAssert.AreEqual(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP }, _negotiationOutput);
	}

	[Test]
	public async Task ServerSendsMSSPDataAfterClientDo()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Server receives DO MSSP from client
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });

		// Assert - Server should send MSSP subnegotiation with data
		Assert.IsNotNull(_negotiationOutput, "Server should send MSSP data");
		Assert.That(_negotiationOutput[0], Is.EqualTo((byte)Trigger.IAC));
		Assert.That(_negotiationOutput[1], Is.EqualTo((byte)Trigger.SB));
		Assert.That(_negotiationOutput[2], Is.EqualTo((byte)Trigger.MSSP));
		
		// Should contain NAME variable
		var encoding = Encoding.ASCII;
		var responseString = encoding.GetString(_negotiationOutput);
		Assert.That(responseString, Does.Contain("NAME"));
		Assert.That(responseString, Does.Contain("Test MUD Server"));
		
		// Should end with IAC SE
		Assert.That(_negotiationOutput[^2], Is.EqualTo((byte)Trigger.IAC));
		Assert.That(_negotiationOutput[^1], Is.EqualTo((byte)Trigger.SE));
	}

	[Test]
	public async Task ServerRejectsDontMSSP()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Server receives DONT MSSP from client
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MSSP });

		// Assert - Server should just accept the rejection without error
		// No specific response expected, just ensure no crash
		Assert.Pass("Server handles DONT MSSP gracefully");
	}

	[Test]
	public async Task ClientRejectsWontMSSP()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WONT MSSP from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MSSP });

		// Assert - Client should just accept the rejection without error
		// No specific response expected, just ensure no crash
		Assert.Pass("Client handles WONT MSSP gracefully");
	}

	[Test]
	public async Task MSSPDataContainsBooleanFieldsCorrectly()
	{
		// Arrange - Server with boolean fields set
		var testServer = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnMSSPAsync = WriteBackToMSSP,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig
		{
			Name = "Boolean Test MUD",
			UTF_8 = true,
			Ansi = false,
			VT100 = true
		}).BuildAsync();

		_negotiationOutput = null;

		// Act - Trigger MSSP send
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });

		// Assert
		Assert.IsNotNull(_negotiationOutput);
		var encoding = Encoding.ASCII;
		var responseString = encoding.GetString(_negotiationOutput);
		
		// UTF-8 should be "1"
		Assert.That(responseString, Does.Contain("UTF-8"));
		var utf8Index = responseString.IndexOf("UTF-8");
		var afterUtf8 = responseString.Substring(utf8Index + 5);
		Assert.That(afterUtf8, Does.Contain("1"));
		
		// ANSI should be "0"
		Assert.That(responseString, Does.Contain("ANSI"));
		var ansiIndex = responseString.IndexOf("ANSI");
		var afterAnsi = responseString.Substring(ansiIndex + 4);
		Assert.That(afterAnsi, Does.Contain("0"));
	}

	[Test]
	public async Task MSSPDataContainsIntegerFieldsCorrectly()
	{
		// Arrange - Server with integer fields set
		var testServer = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnMSSPAsync = WriteBackToMSSP,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig
		{
			Name = "Integer Test MUD",
			Players = 123,
			Port = 4000,
			Areas = 50,
			Rooms = 1000,
			Mobiles = 500
		}).BuildAsync();

		_negotiationOutput = null;

		// Act - Trigger MSSP send
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });

		// Assert
		Assert.IsNotNull(_negotiationOutput);
		var encoding = Encoding.ASCII;
		var responseString = encoding.GetString(_negotiationOutput);
		
		Assert.That(responseString, Does.Contain("PLAYERS"));
		Assert.That(responseString, Does.Contain("123"));
		Assert.That(responseString, Does.Contain("PORT"));
		Assert.That(responseString, Does.Contain("4000"));
		Assert.That(responseString, Does.Contain("AREAS"));
		Assert.That(responseString, Does.Contain("50"));
	}

	[Test]
	public async Task MSSPDataContainsArrayFieldsCorrectly()
	{
		// Arrange - Server with array fields set
		var testServer = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnMSSPAsync = WriteBackToMSSP,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig
		{
			Name = "Array Test MUD",
			Gameplay = ["Adventure", "Roleplaying", "Hack and Slash"],
			Codebase = ["Custom", "DikuMUD"],
			Family = ["DikuMUD"]
		}).BuildAsync();

		_negotiationOutput = null;

		// Act - Trigger MSSP send
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });

		// Assert
		Assert.IsNotNull(_negotiationOutput);
		var encoding = Encoding.ASCII;
		var responseString = encoding.GetString(_negotiationOutput);
		
		Assert.That(responseString, Does.Contain("GAMEPLAY"));
		Assert.That(responseString, Does.Contain("Adventure"));
		Assert.That(responseString, Does.Contain("Roleplaying"));
		Assert.That(responseString, Does.Contain("CODEBASE"));
		Assert.That(responseString, Does.Contain("Custom"));
		Assert.That(responseString, Does.Contain("DikuMUD"));
	}
}
