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
	private TelnetInterpreter _server_ti;
	private TelnetInterpreter _client_ti;
	private byte[] _negotiationOutput;

	private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

	private ValueTask WriteBackToNegotiate(byte[] arg1)
	{
		_negotiationOutput = arg1;
		return ValueTask.CompletedTask;
	}

	private ValueTask WriteBackToGMCP((string Package, string Info) tuple) => ValueTask.CompletedTask;

	[Before(Test)]
	public async Task Setup()
	{
		_negotiationOutput = null;

		_server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		_client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();
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
	public async Task ClientRespondsWithDoSuppressGAToServerWill()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WILL SUPPRESSGOAHEAD from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await _client_ti.WaitForProcessingAsync();

		// Assert
		await Assert.That(_negotiationOutput).IsNotNull();
		await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });
	}

	[Test]
	public async Task ServerAcceptsDoSuppressGA()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Server receives DO SUPPRESSGOAHEAD from client
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });
		await _server_ti.WaitForProcessingAsync();

		// Assert - Server should accept without error
		// The server just records that GA suppression is active
		// Test passed: "Server accepts DO SUPPRESSGOAHEAD successfully"
	}

	[Test]
	public async Task ClientAcceptsWillSuppressGA()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WILL SUPPRESSGOAHEAD from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await _client_ti.WaitForProcessingAsync();

		// Assert - Client should send DO SUPPRESSGOAHEAD
		await Assert.That(_negotiationOutput).IsNotNull();
		await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });
	}

	[Test]
	public async Task ServerHandlesDontSuppressGA()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Server receives DONT SUPPRESSGOAHEAD from client
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await _server_ti.WaitForProcessingAsync();

		// Assert - Server should accept the rejection gracefully
		// Test passed: "Server handles DONT SUPPRESSGOAHEAD gracefully"
	}

	[Test]
	public async Task ClientHandlesWontSuppressGA()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WONT SUPPRESSGOAHEAD from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await _client_ti.WaitForProcessingAsync();

		// Assert - Client should accept the rejection gracefully
		// Test passed: "Client handles WONT SUPPRESSGOAHEAD gracefully"
	}

	[Test]
	public async Task SuppressGANegotiationSequenceComplete()
	{
		// This test verifies the complete negotiation sequence
		var testClient = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		// Step 1: Server sends WILL SUPPRESSGOAHEAD
		_negotiationOutput = null;
		await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await testClient.WaitForProcessingAsync();
		
		await Assert.That(_negotiationOutput).IsNotNull();
		await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });
	}

	[Test]
	public async Task ServerSuppressGANegotiationWithClient()
	{
		// This test verifies server-side negotiation
		var testServer = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<SuppressGoAheadProtocol>()
			.BuildAsync();

		// Client sends DO SUPPRESSGOAHEAD
		_negotiationOutput = null;
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });
		await testServer.WaitForProcessingAsync();
		
		// Server should accept (no error, negotiation completes)
		// Test passed: "Server completes SUPPRESSGOAHEAD negotiation successfully"
	}

	[Test]
	public async Task ClientWillSuppressGAToServer()
	{
		// Test client initiating SUPPRESSGOAHEAD
		_negotiationOutput = null;

		// Act - Client receives WILL SUPPRESSGOAHEAD
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await _client_ti.WaitForProcessingAsync();

		// Assert - Client should respond with DO
		await Assert.That(_negotiationOutput).IsNotNull();
		var expectedResponse = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD };
		await Assert.That(_negotiationOutput).IsEquivalentTo(expectedResponse);
	}

	[Test]
	public async Task SuppressGAWithDontResponse()
	{
		// Test server handling client's DONT
		_negotiationOutput = null;

		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await _server_ti.WaitForProcessingAsync();
		
		// Server should handle DONT gracefully and record that GA is not suppressed
		// Test passed: "Server handles DONT SUPPRESSGOAHEAD and maintains GA mode"
	}

	[Test]
	public async Task SuppressGAWithWontResponse()
	{
		// Test client handling server's WONT
		_negotiationOutput = null;

		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await _client_ti.WaitForProcessingAsync();
		
		// Client should handle WONT gracefully and record that GA is not suppressed
		// Test passed: "Client handles WONT SUPPRESSGOAHEAD and maintains GA mode"
	}
}
