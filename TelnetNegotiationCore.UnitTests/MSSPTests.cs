using Microsoft.Extensions.Logging;
using TUnit.Core;
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


public class MSSPTests : BaseTest
{
	[Test]
	public async Task ClientRespondsWithDoMSSPToServerWill()
	{
		// Arrange
		byte[] negotiationOutput = null;
		MSSPConfig receivedMSSP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToMSSP(MSSPConfig config)
		{
			receivedMSSP = config;
			logger.LogInformation("Received MSSP: {@MSSP}", config);
			return ValueTask.CompletedTask;
		}

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MSSPProtocol>()
				.OnMSSP(WriteBackToMSSP)
			.BuildAsync();

		var clientMssp = client_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		clientMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "Test MUD Client"
		});

		// Act - Client receives WILL MSSP from server
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP });
		await client_ti.WaitForProcessingAsync();

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerSendsMSSPDataAfterClientDo()
	{
		// Arrange
		byte[] negotiationOutput = null;
		MSSPConfig receivedMSSP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToMSSP(MSSPConfig config)
		{
			receivedMSSP = config;
			logger.LogInformation("Received MSSP: {@MSSP}", config);
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MSSPProtocol>()
				.OnMSSP(WriteBackToMSSP)
			.BuildAsync();

		var serverMssp = server_ti.PluginManager!.GetPlugin<MSSPProtocol>();
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

		// Act - Server receives DO MSSP from client
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
		await server_ti.WaitForProcessingAsync();
		await Task.Delay(100);

		// Assert - Server should send MSSP subnegotiation with data
		await Assert.That(negotiationOutput).IsNotNull();
		await Assert.That(negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
		await Assert.That(negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
		await Assert.That(negotiationOutput[2]).IsEqualTo((byte)Trigger.MSSP);
		
		// Should contain NAME variable
		var encoding = Encoding.ASCII;
		var responseString = encoding.GetString(negotiationOutput);
		await Assert.That(responseString).Contains("NAME");
		await Assert.That(responseString).Contains("Test MUD Server");
		
		// Should end with IAC SE
		await Assert.That(negotiationOutput[^2]).IsEqualTo((byte)Trigger.IAC);
		await Assert.That(negotiationOutput[^1]).IsEqualTo((byte)Trigger.SE);

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerRejectsDontMSSP()
	{
		// Arrange
		byte[] negotiationOutput = null;
		MSSPConfig receivedMSSP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToMSSP(MSSPConfig config)
		{
			receivedMSSP = config;
			logger.LogInformation("Received MSSP: {@MSSP}", config);
			return ValueTask.CompletedTask;
		}

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MSSPProtocol>()
				.OnMSSP(WriteBackToMSSP)
			.BuildAsync();

		var serverMssp = server_ti.PluginManager!.GetPlugin<MSSPProtocol>();
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

		negotiationOutput = null;

		// Act - Server receives DONT MSSP from client
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MSSP });
		await server_ti.WaitForProcessingAsync();

		// Assert - Server should just accept the rejection without error
		// No specific response expected, just ensure no crash
		await Assert.That(negotiationOutput).IsNull();

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientRejectsWontMSSP()
	{
		// Arrange
		byte[] negotiationOutput = null;
		MSSPConfig receivedMSSP = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToMSSP(MSSPConfig config)
		{
			receivedMSSP = config;
			logger.LogInformation("Received MSSP: {@MSSP}", config);
			return ValueTask.CompletedTask;
		}

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<MSSPProtocol>()
				.OnMSSP(WriteBackToMSSP)
			.BuildAsync();

		var clientMssp = client_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		clientMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "Test MUD Client"
		});

		negotiationOutput = null;

		// Act - Client receives WONT MSSP from server
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MSSP });
		await client_ti.WaitForProcessingAsync();

		// Assert - Client should just accept the rejection without error
		// No specific response expected, just ensure no crash
		await Assert.That(negotiationOutput).IsNull();

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task MSSPDataContainsBooleanFieldsCorrectly()
	{
		// Arrange - Server with boolean fields set
		byte[] negotiationOutput = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
			return ValueTask.CompletedTask;
		}

		static int FindMSSPVariable(byte[] data, Encoding encoding, string varName)
		{
			for (int i = 0; i < data.Length; i++)
			{
				if (data[i] == (byte)Trigger.MSSP_VAR)
				{
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

		static string GetMSSPValue(byte[] data, int varIndex, Encoding encoding)
		{
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

		// Act - Trigger MSSP send
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
		await testServer.WaitForProcessingAsync();
		await Task.Delay(100);

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		
		// Verify MSSP protocol structure
		await Assert.That(negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
		await Assert.That(negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
		await Assert.That(negotiationOutput[2]).IsEqualTo((byte)Trigger.MSSP);
		await Assert.That(negotiationOutput[^2]).IsEqualTo((byte)Trigger.IAC);
		await Assert.That(negotiationOutput[^1]).IsEqualTo((byte)Trigger.SE);
		
		// Parse and verify specific MSSP variables
		var encoding = Encoding.ASCII;
		var data = negotiationOutput.Skip(3).Take(negotiationOutput.Length - 5).ToArray();
		
		// Look for UTF-8 VAR/VAL pair
		var utf8VarIndex = FindMSSPVariable(data, encoding, "UTF-8");
		await Assert.That(utf8VarIndex).IsGreaterThanOrEqualTo(0);
		var utf8Value = GetMSSPValue(data, utf8VarIndex, encoding);
		await Assert.That(utf8Value).IsEqualTo("1");
		
		// Look for ANSI VAR/VAL pair  
		var ansiVarIndex = FindMSSPVariable(data, encoding, "ANSI");
		await Assert.That(ansiVarIndex).IsGreaterThanOrEqualTo(0);
		var ansiValue = GetMSSPValue(data, ansiVarIndex, encoding);
		await Assert.That(ansiValue).IsEqualTo("0");

		await testServer.DisposeAsync();
	}
	[Test]
	public async Task MSSPDataContainsIntegerFieldsCorrectly()
	{
		// Arrange - Server with integer fields set
		byte[] negotiationOutput = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
			return ValueTask.CompletedTask;
		}

		static int FindMSSPVariable(byte[] data, Encoding encoding, string varName)
		{
			for (int i = 0; i < data.Length; i++)
			{
				if (data[i] == (byte)Trigger.MSSP_VAR)
				{
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

		static string GetMSSPValue(byte[] data, int varIndex, Encoding encoding)
		{
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

		// Act - Trigger MSSP send
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
		await testServer.WaitForProcessingAsync();

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		
		var encoding = Encoding.ASCII;
		var data = negotiationOutput.Skip(3).Take(negotiationOutput.Length - 5).ToArray();
		
		// Verify integer fields
		var playersValue = GetMSSPValue(data, FindMSSPVariable(data, encoding, "PLAYERS"), encoding);
		await Assert.That(playersValue).IsEqualTo("123");
		
		var portValue = GetMSSPValue(data, FindMSSPVariable(data, encoding, "PORT"), encoding);
		await Assert.That(portValue).IsEqualTo("4000");
		
		var areasValue = GetMSSPValue(data, FindMSSPVariable(data, encoding, "AREAS"), encoding);
		await Assert.That(areasValue).IsEqualTo("50");

		await testServer.DisposeAsync();
	}

	[Test]
	public async Task MSSPDataContainsArrayFieldsCorrectly()
	{
		// Arrange - Server with array fields set
		byte[] negotiationOutput = null;

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		ValueTask WriteBackToNegotiate(byte[] arg1)
		{
			negotiationOutput = arg1;
			return ValueTask.CompletedTask;
		}

		static int FindMSSPVariable(byte[] data, Encoding encoding, string varName)
		{
			for (int i = 0; i < data.Length; i++)
			{
				if (data[i] == (byte)Trigger.MSSP_VAR)
				{
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

		static List<string> GetMSSPArrayValues(byte[] data, int varIndex, Encoding encoding)
		{
			var values = new List<string>();
			
			int pos = varIndex + 1;
			while (pos < data.Length && data[pos] != (byte)Trigger.MSSP_VAL)
			{
				pos++;
			}
			
			while (pos < data.Length && data[pos] == (byte)Trigger.MSSP_VAL)
			{
				pos++;
				
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

		// Act - Trigger MSSP send
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
		await testServer.WaitForProcessingAsync();

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		
		var encoding = Encoding.ASCII;
		var data = negotiationOutput.Skip(3).Take(negotiationOutput.Length - 5).ToArray();
		
		// Verify array fields - for arrays, MSSP uses multiple VAL entries for the same VAR
		var gameplayIndex = FindMSSPVariable(data, encoding, "GAMEPLAY");
		await Assert.That(gameplayIndex).IsGreaterThanOrEqualTo(0);
		
		// Count consecutive VAL entries after GAMEPLAY VAR
		var values = GetMSSPArrayValues(data, gameplayIndex, encoding);
		await Assert.That(values.Count).IsEqualTo(3);
		await Assert.That(values).Contains("Adventure");
		await Assert.That(values).Contains("Roleplaying");
		await Assert.That(values).Contains("Hack and Slash");

		await testServer.DisposeAsync();
	}
}
