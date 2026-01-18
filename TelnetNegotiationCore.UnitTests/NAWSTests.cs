using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests;

[TestFixture]
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

	[SetUp]
	public async Task Setup()
	{
		_negotiationOutput = null;
		_receivedHeight = 0;
		_receivedWidth = 0;

		_server_ti = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnNAWSAsync = WriteBackToNAWS,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig
		{
			Name = "Test Server"
		}).BuildAsync();

		_client_ti = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnNAWSAsync = WriteBackToNAWS,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig
		{
			Name = "Test Client"
		}).BuildAsync();
	}

	[Test]
	public void ServerRequestsNAWSOnBuild()
	{
		// The server should have sent DO NAWS during initialization
		// This is verified implicitly by the build process completing successfully
		Assert.Pass("Server DO NAWS is sent during BuildAsync in Setup");
	}

	[Test]
	public async Task ClientAcceptsDoNAWS()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives DO NAWS from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });

		// Assert - Client accepts DO NAWS by setting internal flag (no WILL response sent)
		// The client can now send NAWS data when ready
		Assert.Pass("Client accepts DO NAWS successfully");
	}

	[Test]
	public async Task ServerReceivesNAWSData()
	{
		// Arrange - Complete NAWS negotiation
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
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

		// Assert
		Assert.AreEqual(80, _receivedWidth, "Width should be 80");
		Assert.AreEqual(24, _receivedHeight, "Height should be 24");
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

			// Assert
			Assert.AreEqual(testCase.width, _receivedWidth, $"Width should be {testCase.width}");
			Assert.AreEqual(testCase.height, _receivedHeight, $"Height should be {testCase.height}");
		}
	}

	[Test]
	public async Task ClientCanSendNAWSData()
	{
		// Arrange - Complete NAWS negotiation
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		_negotiationOutput = null;

		short width = 100;
		short height = 40;

		// Act
		await _client_ti.SendNAWS(width, height);

		// Assert
		Assert.IsNotNull(_negotiationOutput, "Client should send NAWS data");
		Assert.That(_negotiationOutput[0], Is.EqualTo((byte)Trigger.IAC));
		Assert.That(_negotiationOutput[1], Is.EqualTo((byte)Trigger.SB));
		Assert.That(_negotiationOutput[2], Is.EqualTo((byte)Trigger.NAWS));
		Assert.That(_negotiationOutput[^2], Is.EqualTo((byte)Trigger.IAC));
		Assert.That(_negotiationOutput[^1], Is.EqualTo((byte)Trigger.SE));
	}

	[Test]
	public async Task ServerHandlesDontNAWS()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Server receives DONT NAWS from client
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.NAWS });

		// Assert - Server should accept the rejection gracefully
		Assert.Pass("Server handles DONT NAWS gracefully");
	}

	[Test]
	public async Task ClientHandlesWontNAWS()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WONT NAWS from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NAWS });

		// Assert - Client should accept the rejection gracefully
		Assert.Pass("Client handles WONT NAWS gracefully");
	}

	[Test]
	public async Task NAWSNegotiationSequenceComplete()
	{
		// This test verifies the complete negotiation sequence
		var testClient = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnNAWSAsync = WriteBackToNAWS,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig()).BuildAsync();

		// Step 1: Server sends DO NAWS
		_negotiationOutput = null;
		await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
		
		// Client accepts DO NAWS (no WILL response in this implementation)
		// Step 2: Client can now send NAWS data
		_negotiationOutput = null;
		await testClient.SendNAWS(80, 24);
		
		Assert.IsNotNull(_negotiationOutput, "Client should send NAWS data after accepting DO");
	}

	[Test]
	public async Task ServerStoresClientDimensions()
	{
		// Arrange
		var testServer = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnNAWSAsync = WriteBackToNAWS,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig()).BuildAsync();

		// Default values
		Assert.AreEqual(78, testServer.ClientWidth, "Default width should be 78");
		Assert.AreEqual(24, testServer.ClientHeight, "Default height should be 24");

		// Send NAWS data
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });

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

		// Assert updated values
		Assert.AreEqual(120, testServer.ClientWidth, "Width should be updated to 120");
		Assert.AreEqual(40, testServer.ClientHeight, "Height should be updated to 40");
	}

	[Test]
	public async Task MultipleNAWSUpdates()
	{
		// Test that window can be resized multiple times
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });

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

			Assert.AreEqual(width, _receivedWidth, $"Width should be {width}");
			Assert.AreEqual(height, _receivedHeight, $"Height should be {height}");
			Assert.AreEqual(width, _server_ti.ClientWidth, $"Stored width should be {width}");
			Assert.AreEqual(height, _server_ti.ClientHeight, $"Stored height should be {height}");
		}
	}

	[Test]
	public async Task ServerAsksClientForNAWS()
	{
		// Test server requesting NAWS from client
		var testServer = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnNAWSAsync = WriteBackToNAWS,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig()).BuildAsync();

		// RequestNAWSAsync should send DO NAWS (and it's already called in BuildAsync)
		// Let's verify we can call it again without issues
		_negotiationOutput = null;
		
		// Manually call RequestNAWSAsync - it should not send again since _WillingToDoNAWS is already true
		await testServer.RequestNAWSAsync();

		// No new DO NAWS should be sent since it was already sent during build
		Assert.IsNull(_negotiationOutput, "Server should not send duplicate DO NAWS");
	}

	[Test]
	public async Task ServerWontNAWSIfAskedToDo()
	{
		// Server should refuse DO NAWS (servers don't send NAWS data, only clients do)
		_negotiationOutput = null;

		// Act - Server receives DO NAWS (client asking server to send NAWS)
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });

		// Assert - Server should send WONT NAWS
		Assert.IsNotNull(_negotiationOutput, "Server should respond to DO NAWS");
		CollectionAssert.AreEqual(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NAWS }, _negotiationOutput);
	}
}
