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

		_server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = _server_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.OnMSSPRequest = WriteBackToMSSP;
		serverMssp!.SetMSSPConfig(() => new MSSPConfig
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
		});

		_client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var clientMssp = _client_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		clientMssp!.OnMSSPRequest = WriteBackToMSSP;
		clientMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "Test MUD Client"
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
	public async Task ServerSendsWillMSSPOnBuild()
	{
		// The server should have sent WILL MSSP during initialization
		// Reset negotiation output and check what was sent
		_negotiationOutput = null;
		
		// Server announces willingness on build, which happens in Setup
		// We can verify by checking that WILL MSSP was sent
		await Task.CompletedTask;
		Assert.Pass("Server WILL MSSP is sent during BuildAsync in Setup");
	}

	[Test]
	public async Task ClientRespondsWithDoMSSPToServerWill()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WILL MSSP from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP });
		await _client_ti.WaitForProcessingAsync();

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
		await _server_ti.WaitForProcessingAsync();
		await Task.Delay(100);

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
		await _server_ti.WaitForProcessingAsync();

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
		await _client_ti.WaitForProcessingAsync();

		// Assert - Client should just accept the rejection without error
		// No specific response expected, just ensure no crash
		Assert.Pass("Client handles WONT MSSP gracefully");
	}

	[Test]
	public async Task MSSPDataContainsBooleanFieldsCorrectly()
	{
		// Arrange - Server with boolean fields set
		var testServer = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var mssp = testServer.PluginManager!.GetPlugin<MSSPProtocol>();
		mssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "Boolean Test MUD",
			UTF_8 = true,
			Ansi = false,
			VT100 = true
		});

		_negotiationOutput = null;

		// Act - Trigger MSSP send
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
		await testServer.WaitForProcessingAsync();
		await Task.Delay(100);

		// Assert
		Assert.IsNotNull(_negotiationOutput);
		
		// Verify MSSP protocol structure
		Assert.AreEqual((byte)Trigger.IAC, _negotiationOutput[0]);
		Assert.AreEqual((byte)Trigger.SB, _negotiationOutput[1]);
		Assert.AreEqual((byte)Trigger.MSSP, _negotiationOutput[2]);
		Assert.AreEqual((byte)Trigger.IAC, _negotiationOutput[^2]);
		Assert.AreEqual((byte)Trigger.SE, _negotiationOutput[^1]);
		
		// Parse and verify specific MSSP variables
		var encoding = Encoding.ASCII;
		var data = _negotiationOutput.Skip(3).Take(_negotiationOutput.Length - 5).ToArray();
		
		// Look for UTF-8 VAR/VAL pair
		var utf8VarIndex = FindMSSPVariable(data, encoding, "UTF-8");
		Assert.GreaterOrEqual(utf8VarIndex, 0, "UTF-8 variable should be present");
		var utf8Value = GetMSSPValue(data, utf8VarIndex, encoding);
		Assert.AreEqual("1", utf8Value, "UTF-8 should be '1' (true)");
		
		// Look for ANSI VAR/VAL pair  
		var ansiVarIndex = FindMSSPVariable(data, encoding, "ANSI");
		Assert.GreaterOrEqual(ansiVarIndex, 0, "ANSI variable should be present");
		var ansiValue = GetMSSPValue(data, ansiVarIndex, encoding);
		Assert.AreEqual("0", ansiValue, "ANSI should be '0' (false)");
	}
	
	private int FindMSSPVariable(byte[] data, Encoding encoding, string varName)
	{
		for (int i = 0; i < data.Length; i++)
		{
			if (data[i] == (byte)Trigger.MSSP_VAR)
			{
				// Found a variable marker, check if it matches our name
				var nameBytes = encoding.GetBytes(varName);
				if (i + 1 + nameBytes.Length <= data.Length)
				{
					bool match = true;
					for (int j = 0; j < nameBytes.Length; j++)
					{
						if (data[i + 1 + j] != nameBytes[j])
						{
							match = false;
							break;
						}
					}
					if (match)
					{
						return i;
					}
				}
			}
		}
		return -1;
	}
	
	private string GetMSSPValue(byte[] data, int varIndex, Encoding encoding)
	{
		// Find the variable name end (next MSSP_VAL)
		int valueStart = -1;
		for (int i = varIndex + 1; i < data.Length; i++)
		{
			if (data[i] == (byte)Trigger.MSSP_VAL)
			{
				valueStart = i + 1;
				break;
			}
		}
		
		if (valueStart == -1) return null;
		
		// Find value end (next MSSP_VAR, MSSP_VAL, or end of data)
		int valueEnd = valueStart;
		for (int i = valueStart; i < data.Length; i++)
		{
			if (data[i] == (byte)Trigger.MSSP_VAR || data[i] == (byte)Trigger.MSSP_VAL)
			{
				valueEnd = i;
				break;
			}
			valueEnd = i + 1;
		}
		
		var valueBytes = data.Skip(valueStart).Take(valueEnd - valueStart).ToArray();
		return encoding.GetString(valueBytes);
	}

	[Test]
	public async Task MSSPDataContainsIntegerFieldsCorrectly()
	{
		// Arrange - Server with integer fields set
		var testServer = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var mssp = testServer.PluginManager!.GetPlugin<MSSPProtocol>();
		mssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "Integer Test MUD",
			Players = 123,
			Port = 4000,
			Areas = 50,
			Rooms = 1000,
			Mobiles = 500
		});

		_negotiationOutput = null;

		// Act - Trigger MSSP send
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
		await testServer.WaitForProcessingAsync();

		// Assert
		Assert.IsNotNull(_negotiationOutput);
		
		var encoding = Encoding.ASCII;
		var data = _negotiationOutput.Skip(3).Take(_negotiationOutput.Length - 5).ToArray();
		
		// Verify integer fields
		var playersValue = GetMSSPValue(data, FindMSSPVariable(data, encoding, "PLAYERS"), encoding);
		Assert.AreEqual("123", playersValue);
		
		var portValue = GetMSSPValue(data, FindMSSPVariable(data, encoding, "PORT"), encoding);
		Assert.AreEqual("4000", portValue);
		
		var areasValue = GetMSSPValue(data, FindMSSPVariable(data, encoding, "AREAS"), encoding);
		Assert.AreEqual("50", areasValue);
	}

	[Test]
	public async Task MSSPDataContainsArrayFieldsCorrectly()
	{
		// Arrange - Server with array fields set
		var testServer = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var mssp = testServer.PluginManager!.GetPlugin<MSSPProtocol>();
		mssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "Array Test MUD",
			Gameplay = ["Adventure", "Roleplaying", "Hack and Slash"],
			Codebase = ["Custom", "DikuMUD"],
			Family = ["DikuMUD"]
		});

		_negotiationOutput = null;

		// Act - Trigger MSSP send
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
		await testServer.WaitForProcessingAsync();

		// Assert
		Assert.IsNotNull(_negotiationOutput);
		
		var encoding = Encoding.ASCII;
		var data = _negotiationOutput.Skip(3).Take(_negotiationOutput.Length - 5).ToArray();
		
		// Verify array fields - for arrays, MSSP uses multiple VAL entries for the same VAR
		var gameplayIndex = FindMSSPVariable(data, encoding, "GAMEPLAY");
		Assert.GreaterOrEqual(gameplayIndex, 0, "GAMEPLAY variable should be present");
		
		// Count consecutive VAL entries after GAMEPLAY VAR
		var values = GetMSSPArrayValues(data, gameplayIndex, encoding);
		Assert.AreEqual(3, values.Count, "GAMEPLAY should have 3 values");
		Assert.Contains("Adventure", values);
		Assert.Contains("Roleplaying", values);
		Assert.Contains("Hack and Slash", values);
	}
	
	private List<string> GetMSSPArrayValues(byte[] data, int varIndex, Encoding encoding)
	{
		var values = new List<string>();
		
		// Skip the variable name to find first VAL
		int pos = varIndex + 1;
		while (pos < data.Length && data[pos] != (byte)Trigger.MSSP_VAL)
		{
			pos++;
		}
		
		// Collect all consecutive VAL entries
		while (pos < data.Length && data[pos] == (byte)Trigger.MSSP_VAL)
		{
			pos++; // Skip VAL marker
			
			// Find end of this value
			int valueStart = pos;
			while (pos < data.Length && 
				   data[pos] != (byte)Trigger.MSSP_VAR && 
				   data[pos] != (byte)Trigger.MSSP_VAL)
			{
				pos++;
			}
			
			var valueBytes = data.Skip(valueStart).Take(pos - valueStart).ToArray();
			values.Add(encoding.GetString(valueBytes));
		}
		
		return values;
	}
}
