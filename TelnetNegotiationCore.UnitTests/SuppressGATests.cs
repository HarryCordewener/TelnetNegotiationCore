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


public class SuppressGATests : BaseTest
{

	[Test]
	public async Task ClientRespondsWithDoSuppressGAToServerWill()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Act - Client receives WILL SUPPRESSGOAHEAD from server
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await client_ti.WaitForProcessingAsync();

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		// Cleanup
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerAcceptsDoSuppressGA()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await Task.Delay(100);
		negotiationOutput = null;

		// Act - Server receives DO SUPPRESSGOAHEAD from client
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });
		await server_ti.WaitForProcessingAsync();

		// Assert - Server should accept without error (no response sent)
		// The server just records that GA suppression is active
		await Assert.That(negotiationOutput).IsNull();

		// Cleanup
		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientAcceptsWillSuppressGA()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Act - Client receives WILL SUPPRESSGOAHEAD from server
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await client_ti.WaitForProcessingAsync();

		// Assert - Client should send DO SUPPRESSGOAHEAD
		await Assert.That(negotiationOutput).IsNotNull();
		await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		// Cleanup
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerHandlesDontSuppressGA()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await Task.Delay(100);
		negotiationOutput = null;

		// Act - Server receives DONT SUPPRESSGOAHEAD from client
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await server_ti.WaitForProcessingAsync();

		// Assert - Server should accept the rejection gracefully (no error thrown)
		await Assert.That(negotiationOutput).IsNull();

		// Cleanup
		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientHandlesWontSuppressGA()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Act - Client receives WONT SUPPRESSGOAHEAD from server
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await client_ti.WaitForProcessingAsync();

		// Assert - Client should accept the rejection gracefully (no error thrown)
		await Assert.That(negotiationOutput).IsNull();

		// Cleanup
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task SuppressGANegotiationSequenceComplete()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// This test verifies the complete negotiation sequence
		var testClient = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Step 1: Server sends WILL SUPPRESSGOAHEAD
		await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await testClient.WaitForProcessingAsync();
		
		await Assert.That(negotiationOutput).IsNotNull();
		await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		// Cleanup
		await testClient.DisposeAsync();
	}

	[Test]
	public async Task ServerSuppressGANegotiationWithClient()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// This test verifies server-side negotiation
		var testServer = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await Task.Delay(100);
		negotiationOutput = null;

		// Client sends DO SUPPRESSGOAHEAD
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });
		await testServer.WaitForProcessingAsync();
		
		// Server should accept (no error, negotiation completes, no response sent)
		await Assert.That(negotiationOutput).IsNull();

		// Cleanup
		await testServer.DisposeAsync();
	}

	[Test]
	public async Task ClientWillSuppressGAToServer()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Test client initiating SUPPRESSGOAHEAD
		// Act - Client receives WILL SUPPRESSGOAHEAD
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await client_ti.WaitForProcessingAsync();

		// Assert - Client should respond with DO
		await Assert.That(negotiationOutput).IsNotNull();
		var expectedResponse = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD };
		await Assert.That(negotiationOutput).IsEquivalentTo(expectedResponse);

		// Cleanup
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task SuppressGAWithDontResponse()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await Task.Delay(100);
		negotiationOutput = null;

		// Test server handling client's DONT
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await server_ti.WaitForProcessingAsync();
		
		// Server should handle DONT gracefully and record that GA is not suppressed (no error thrown)
		await Assert.That(negotiationOutput).IsNull();

		// Cleanup
		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task SuppressGAWithWontResponse()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Test client handling server's WONT
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await client_ti.WaitForProcessingAsync();
		
		// Client should handle WONT gracefully and record that GA is not suppressed (no error thrown)
		await Assert.That(negotiationOutput).IsNull();

		// Cleanup
		await client_ti.DisposeAsync();
	}
}
