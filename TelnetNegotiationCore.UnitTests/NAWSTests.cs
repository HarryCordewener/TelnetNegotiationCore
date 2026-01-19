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
	private TelnetInterpreter _server_ti;
	private TelnetInterpreter _client_ti;
	private byte[] _negotiationOutput;
	private int _receivedHeight;
	private int _receivedWidth;

	private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

	private ValueTask WriteBackToNegotiate(byte[] arg1)
	{
		_negotiationOutput = arg1;
		return ValueTask.CompletedTask;
	}

	private ValueTask WriteBackToNAWS(int height, int width)
	{
		_receivedHeight = height;
		_receivedWidth = width;
		logger.LogInformation("Received NAWS: Height={Height}, Width={Width}", height, width);
		return ValueTask.CompletedTask;
	}

	private ValueTask WriteBackToGMCP((string Package, string Info) tuple) => ValueTask.CompletedTask;

	[Before(Test)]
	public async Task Setup()
	{
		_negotiationOutput = null;
		_receivedHeight = 0;
		_receivedWidth = 0;

		_server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(WriteBackToNAWS)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var serverMssp = _server_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		serverMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "Test Server"
		});

		_client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(WriteBackToNAWS)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		var clientMssp = _client_ti.PluginManager!.GetPlugin<MSSPProtocol>();
		clientMssp!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "Test Client"
		});
	}

	[After(Test)]
	public async Task TearDown()
	{
		if (_server_ti != null)
			await _server_ti.DisposeAsync();
		if (_client_ti != null)
			await _client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerReceivesNAWSData()
	{
		// Arrange - Complete NAWS negotiation
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
		await _server_ti.WaitForProcessingAsync();
		_receivedWidth = 0;
		_receivedHeight = 0;

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
		await _server_ti.InterpretByteArrayAsync(nawsData);
		await _server_ti.WaitForProcessingAsync();

		// Assert
		await Assert.That(_receivedWidth).IsEqualTo(80);
		await Assert.That(_receivedHeight).IsEqualTo(24);
	}

	[Test]
	public async Task ServerReceivesNAWSDataWithDifferentSizes()
	{
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
			await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
			await _server_ti.WaitForProcessingAsync();
			_receivedWidth = 0;
			_receivedHeight = 0;

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
			await _server_ti.InterpretByteArrayAsync(nawsData);
			await _server_ti.WaitForProcessingAsync();

			// Assert
			await Assert.That(_receivedWidth, $"Width should be {testCase.width}").IsEqualTo(testCase.width);
			await Assert.That(_receivedHeight, $"Height should be {testCase.height}").IsEqualTo(testCase.height);
		}
	}

	[Test]
	public async Task ClientCanSendNAWSData()
	{
		// Arrange - Complete NAWS negotiation
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		await _client_ti.WaitForProcessingAsync();
		_negotiationOutput = null;

		short width = 100;
		short height = 40;

		// Act
		await _client_ti.SendNAWS(width, height);

		// Assert
		await Assert.That(_negotiationOutput).IsNotNull();
		await Assert.That(_negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
		await Assert.That(_negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
		await Assert.That(_negotiationOutput[2]).IsEqualTo((byte)Trigger.NAWS);
		await Assert.That(_negotiationOutput[^2]).IsEqualTo((byte)Trigger.IAC);
		await Assert.That(_negotiationOutput[^1]).IsEqualTo((byte)Trigger.SE);
	}

	[Test]
	public async Task ServerHandlesDontNAWS()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Server receives DONT NAWS from client
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.NAWS });
		await _server_ti.WaitForProcessingAsync();

		// Assert - Server should accept the rejection gracefully
		// Test passed: "Server handles DONT NAWS gracefully"
	}

	[Test]
	public async Task ClientHandlesWontNAWS()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WONT NAWS from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NAWS });
		await _client_ti.WaitForProcessingAsync();

		// Assert - Client should accept the rejection gracefully
		// Test passed: "Client handles WONT NAWS gracefully"
	}

	[Test]
	public async Task ServerStoresClientDimensions()
	{
		// Arrange
		var testServer = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<NAWSProtocol>()
				.OnNAWS(WriteBackToNAWS)
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
	}

	[Test]
	public async Task MultipleNAWSUpdates()
	{
		// Test that window can be resized multiple times
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
		await _server_ti.WaitForProcessingAsync();

		// First size: 80x24
		var sizes = new[] { (80, 24), (120, 40), (100, 30), (132, 43) };

		foreach (var (width, height) in sizes)
		{
			_receivedWidth = 0;
			_receivedHeight = 0;

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

			await _server_ti.InterpretByteArrayAsync(nawsData);
			await _server_ti.WaitForProcessingAsync();

			await Assert.That(_receivedWidth, $"Width should be {width}").IsEqualTo(width);
			await Assert.That(_receivedHeight, $"Height should be {height}").IsEqualTo(height);
			await Assert.That(_server_ti.ClientWidth, $"Stored width should be {width}").IsEqualTo(width);
			await Assert.That(_server_ti.ClientHeight, $"Stored height should be {height}").IsEqualTo(height);
		}
	}

	[Test]
	public async Task ServerWontNAWSIfAskedToDo()
	{
		// Server should refuse DO NAWS (servers don't send NAWS data, only clients do)
		_negotiationOutput = null;

		// Act - Server receives DO NAWS (client asking server to send NAWS)
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		await _server_ti.WaitForProcessingAsync();

		// Assert - Server should send WONT NAWS
		await Assert.That(_negotiationOutput).IsNotNull();
		await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NAWS });
	}
}
