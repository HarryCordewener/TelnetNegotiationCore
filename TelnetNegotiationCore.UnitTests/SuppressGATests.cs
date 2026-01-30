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

		var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		// Act - Client receives WILL SUPPRESSGOAHEAD from server
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

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

		var server_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		negotiationOutput = null;

		// Act - Server receives DO SUPPRESSGOAHEAD from client
		await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

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

		var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		// Act - Client receives WILL SUPPRESSGOAHEAD from server
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

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

		var server_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		negotiationOutput = null;

		// Act - Server receives DONT SUPPRESSGOAHEAD from client
		await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });

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

		var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		// Act - Client receives WONT SUPPRESSGOAHEAD from server
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.SUPPRESSGOAHEAD });

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

		// This test verifies the complete negotiation sequence
		var testClient = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		// Step 1: Server sends WILL SUPPRESSGOAHEAD
		await InterpretAndWaitAsync(testClient, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		
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

		// This test verifies server-side negotiation
		var testServer = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		negotiationOutput = null;

		// Client sends DO SUPPRESSGOAHEAD
		await InterpretAndWaitAsync(testServer, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });
		
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

		var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		// Test client initiating SUPPRESSGOAHEAD
		// Act - Client receives WILL SUPPRESSGOAHEAD
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

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

		var server_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		negotiationOutput = null;

		// Test server handling client's DONT
		await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });
		
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

		var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		// Test client handling server's WONT
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.SUPPRESSGOAHEAD });
		
		// Client should handle WONT gracefully and record that GA is not suppressed (no error thrown)
		await Assert.That(negotiationOutput).IsNull();

		// Cleanup
		await client_ti.DisposeAsync();
	}
}
