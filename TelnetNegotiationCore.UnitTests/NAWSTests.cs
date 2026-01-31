using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;


public class NAWSTests : BaseTest
{

	[Test]
	public async Task ServerReceivesNAWSData()
	{
		byte[] negotiationOutput = null;
		int receivedHeight = 0;
		int receivedWidth = 0;

		ValueTask CaptureNegotiation(byte[] data) { negotiationOutput = data; return ValueTask.CompletedTask; }
		ValueTask CaptureNAWS(int height, int width) 
		{ 
			receivedHeight = height; 
			receivedWidth = width; 
			logger.LogInformation("Received NAWS: Height={Height}, Width={Width}", height, width);
			return ValueTask.CompletedTask; 
		}
		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = server.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig { Name = "Test Server" });

		// Arrange - Complete NAWS negotiation
		await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
		await server.WaitForProcessingAsync();
		receivedWidth = 0;
		receivedHeight = 0;

		// Prepare NAWS data for 80x24 terminal
		short width = 80;
		short height = 24;
		
		var widthBytes = BitConverter.GetBytes(width);
		var heightBytes = BitConverter.GetBytes(height);
		
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(widthBytes);
			Array.Reverse(heightBytes);
		}

		var nawsData = new byte[]
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.NAWS,
			widthBytes[0],
			widthBytes[1],
			heightBytes[0],
			heightBytes[1],
			(byte)Trigger.IAC,
			(byte)Trigger.SE
		};

		// Act
		await server.InterpretByteArrayAsync(nawsData);
		await server.WaitForProcessingAsync();

		// Assert
		await Assert.That(receivedWidth).IsEqualTo(80);
		await Assert.That(receivedHeight).IsEqualTo(24);

		await server.DisposeAsync();
	}

	[Test]
	public async Task ServerReceivesNAWSDataWithDifferentSizes()
	{
		byte[] negotiationOutput = null;
		int receivedHeight = 0;
		int receivedWidth = 0;

		ValueTask CaptureNegotiation(byte[] data) { negotiationOutput = data; return ValueTask.CompletedTask; }
		ValueTask CaptureNAWS(int height, int width) 
		{ 
			receivedHeight = height; 
			receivedWidth = width; 
			logger.LogInformation("Received NAWS: Height={Height}, Width={Width}", height, width);
			return ValueTask.CompletedTask; 
		}
		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = server.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig { Name = "Test Server" });

		// Test various window sizes
		var testCases = new[]
		{
			(width: (short)120, height: (short)40),
			(width: (short)132, height: (short)43),
			(width: (short)100, height: (short)30),
			(width: (short)78, height: (short)24)
		};

		foreach (var testCase in testCases)
		{
			// Arrange
			await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
			await server.WaitForProcessingAsync();
			receivedWidth = 0;
			receivedHeight = 0;

			var widthBytes = BitConverter.GetBytes(testCase.width);
			var heightBytes = BitConverter.GetBytes(testCase.height);
			
			if (BitConverter.IsLittleEndian)
			{
				Array.Reverse(widthBytes);
				Array.Reverse(heightBytes);
			}

			var nawsData = new byte[]
			{
				(byte)Trigger.IAC,
				(byte)Trigger.SB,
				(byte)Trigger.NAWS,
				widthBytes[0],
				widthBytes[1],
				heightBytes[0],
				heightBytes[1],
				(byte)Trigger.IAC,
				(byte)Trigger.SE
			};

			// Act
			await server.InterpretByteArrayAsync(nawsData);
			await server.WaitForProcessingAsync();

			// Assert
			await Assert.That(receivedWidth, $"Width should be {testCase.width}").IsEqualTo(testCase.width);
			await Assert.That(receivedHeight, $"Height should be {testCase.height}").IsEqualTo(testCase.height);
		}

		await server.DisposeAsync();
	}

	[Test]
	public async Task ClientCanSendNAWSData()
	{
		byte[] negotiationOutput = null;
		int receivedHeight = 0;
		int receivedWidth = 0;

		ValueTask CaptureNegotiation(byte[] data) { negotiationOutput = data; return ValueTask.CompletedTask; }
		ValueTask CaptureNAWS(int height, int width) 
		{ 
			receivedHeight = height; 
			receivedWidth = width; 
			logger.LogInformation("Received NAWS: Height={Height}, Width={Width}", height, width);
			return ValueTask.CompletedTask; 
		}
		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var clientMssp = client.PluginManager!.GetPlugin<MSSPProtocol>();
		clientMssp!.SetMSSPConfig(() => new MSSPConfig { Name = "Test Client" });

		// Arrange - Complete NAWS negotiation
		await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		await client.WaitForProcessingAsync();
		negotiationOutput = null;

		short width = 100;
		short height = 40;

		// Act
		await client.SendNAWS(width, height);

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		await Assert.That(negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
		await Assert.That(negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
		await Assert.That(negotiationOutput[2]).IsEqualTo((byte)Trigger.NAWS);
		await Assert.That(negotiationOutput[^2]).IsEqualTo((byte)Trigger.IAC);
		await Assert.That(negotiationOutput[^1]).IsEqualTo((byte)Trigger.SE);

		await client.DisposeAsync();
	}

	[Test]
	public async Task ServerHandlesDontNAWS()
	{
		byte[] negotiationOutput = null;
		int receivedHeight = 0;
		int receivedWidth = 0;

		ValueTask CaptureNegotiation(byte[] data) { negotiationOutput = data; return ValueTask.CompletedTask; }
		ValueTask CaptureNAWS(int height, int width) 
		{ 
			receivedHeight = height; 
			receivedWidth = width; 
			logger.LogInformation("Received NAWS: Height={Height}, Width={Width}", height, width);
			return ValueTask.CompletedTask; 
		}
		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = server.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig { Name = "Test Server" });

		// Arrange
		negotiationOutput = null;

		// Act - Server receives DONT NAWS from client
		await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.NAWS });
		await server.WaitForProcessingAsync();

		// Assert - Server should accept the rejection gracefully (no error thrown)
		await Assert.That(negotiationOutput).IsNull();

		await server.DisposeAsync();
	}

	[Test]
	public async Task ClientHandlesWontNAWS()
	{
		byte[] negotiationOutput = null;
		int receivedHeight = 0;
		int receivedWidth = 0;

		ValueTask CaptureNegotiation(byte[] data) { negotiationOutput = data; return ValueTask.CompletedTask; }
		ValueTask CaptureNAWS(int height, int width) 
		{ 
			receivedHeight = height; 
			receivedWidth = width; 
			logger.LogInformation("Received NAWS: Height={Height}, Width={Width}", height, width);
			return ValueTask.CompletedTask; 
		}
		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var clientMssp = client.PluginManager!.GetPlugin<MSSPProtocol>();
		clientMssp!.SetMSSPConfig(() => new MSSPConfig { Name = "Test Client" });

		// Arrange
		negotiationOutput = null;

		// Act - Client receives WONT NAWS from server
		await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NAWS });
		await client.WaitForProcessingAsync();

		// Assert - Client should accept the rejection gracefully (no error thrown)
		await Assert.That(negotiationOutput).IsNull();

		await client.DisposeAsync();
	}

	[Test]
	public async Task ServerStoresClientDimensions()
	{
		byte[] negotiationOutput = null;
		int receivedHeight = 0;
		int receivedWidth = 0;

		ValueTask CaptureNegotiation(byte[] data) { negotiationOutput = data; return ValueTask.CompletedTask; }
		ValueTask CaptureNAWS(int height, int width) 
		{ 
			receivedHeight = height; 
			receivedWidth = width; 
			logger.LogInformation("Received NAWS: Height={Height}, Width={Width}", height, width);
			return ValueTask.CompletedTask; 
		}
		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// Arrange
		var testServer = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = testServer.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig());

		// Default values
		await Assert.That(testServer.ClientWidth).IsEqualTo(78);
		await Assert.That(testServer.ClientHeight).IsEqualTo(24);

		// Send NAWS data
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
		await testServer.WaitForProcessingAsync();

		short width = 120;
		short height = 40;
		
		var widthBytes = BitConverter.GetBytes(width);
		var heightBytes = BitConverter.GetBytes(height);
		
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(widthBytes);
			Array.Reverse(heightBytes);
		}

		var nawsData = new byte[]
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.NAWS,
			widthBytes[0],
			widthBytes[1],
			heightBytes[0],
			heightBytes[1],
			(byte)Trigger.IAC,
			(byte)Trigger.SE
		};

		await testServer.InterpretByteArrayAsync(nawsData);
		await testServer.WaitForProcessingAsync();

		// Assert updated values
		await Assert.That(testServer.ClientWidth).IsEqualTo(120);
		await Assert.That(testServer.ClientHeight).IsEqualTo(40);

		await testServer.DisposeAsync();
	}

	[Test]
	public async Task MultipleNAWSUpdates()
	{
		byte[] negotiationOutput = null;
		int receivedHeight = 0;
		int receivedWidth = 0;

		ValueTask CaptureNegotiation(byte[] data) { negotiationOutput = data; return ValueTask.CompletedTask; }
		ValueTask CaptureNAWS(int height, int width) 
		{ 
			receivedHeight = height; 
			receivedWidth = width; 
			logger.LogInformation("Received NAWS: Height={Height}, Width={Width}", height, width);
			return ValueTask.CompletedTask; 
		}
		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = server.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig { Name = "Test Server" });

		// Test that window can be resized multiple times
		await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
		await server.WaitForProcessingAsync();

		// First size: 80x24
		var sizes = new[] { (80, 24), (120, 40), (100, 30), (132, 43) };

		foreach (var (width, height) in sizes)
		{
			receivedWidth = 0;
			receivedHeight = 0;

			var widthBytes = BitConverter.GetBytes((short)width);
			var heightBytes = BitConverter.GetBytes((short)height);
			
			if (BitConverter.IsLittleEndian)
			{
				Array.Reverse(widthBytes);
				Array.Reverse(heightBytes);
			}

			var nawsData = new byte[]
			{
				(byte)Trigger.IAC,
				(byte)Trigger.SB,
				(byte)Trigger.NAWS,
				widthBytes[0],
				widthBytes[1],
				heightBytes[0],
				heightBytes[1],
				(byte)Trigger.IAC,
				(byte)Trigger.SE
			};

			await server.InterpretByteArrayAsync(nawsData);
			await server.WaitForProcessingAsync();

			await Assert.That(receivedWidth, $"Width should be {width}").IsEqualTo(width);
			await Assert.That(receivedHeight, $"Height should be {height}").IsEqualTo(height);
			await Assert.That(server.ClientWidth, $"Stored width should be {width}").IsEqualTo(width);
			await Assert.That(server.ClientHeight, $"Stored height should be {height}").IsEqualTo(height);
		}

		await server.DisposeAsync();
	}

	[Test]
	public async Task ServerWontNAWSIfAskedToDo()
	{
		byte[] negotiationOutput = null;
		int receivedHeight = 0;
		int receivedWidth = 0;

		ValueTask CaptureNegotiation(byte[] data) { negotiationOutput = data; return ValueTask.CompletedTask; }
		ValueTask CaptureNAWS(int height, int width) 
		{ 
			receivedHeight = height; 
			receivedWidth = width; 
			logger.LogInformation("Received NAWS: Height={Height}, Width={Width}", height, width);
			return ValueTask.CompletedTask; 
		}
		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(CaptureNAWS)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = server.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig { Name = "Test Server" });

		// Server should refuse DO NAWS (servers don't send NAWS data, only clients do)
		negotiationOutput = null;

		// Act - Server receives DO NAWS (client asking server to send NAWS)
		await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		await server.WaitForProcessingAsync();

		// Assert - Server should send WONT NAWS
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NAWS });

		await server.DisposeAsync();
	}
}
