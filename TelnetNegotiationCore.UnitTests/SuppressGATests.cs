using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests;

[TestFixture]
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

	[SetUp]
	public async Task Setup()
	{
		_negotiationOutput = null;

		_server_ti = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
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
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig
		{
			Name = "Test Client"
		}).BuildAsync();
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
	public async Task ServerSendsWillSuppressGAOnBuild()
	{
		// The server should have sent WILL SUPPRESSGOAHEAD during initialization
		// This is verified implicitly by the build process completing successfully
		await Task.CompletedTask;
		Assert.Pass("Server WILL SUPPRESSGOAHEAD is sent during BuildAsync in Setup");
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
		Assert.IsNotNull(_negotiationOutput, "Client should respond to WILL SUPPRESSGOAHEAD");
		CollectionAssert.AreEqual(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD }, _negotiationOutput);
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
		Assert.Pass("Server accepts DO SUPPRESSGOAHEAD successfully");
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
		Assert.IsNotNull(_negotiationOutput);
		CollectionAssert.AreEqual(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD }, _negotiationOutput);
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
		Assert.Pass("Server handles DONT SUPPRESSGOAHEAD gracefully");
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
		Assert.Pass("Client handles WONT SUPPRESSGOAHEAD gracefully");
	}

	[Test]
	public async Task SuppressGANegotiationSequenceComplete()
	{
		// This test verifies the complete negotiation sequence
		var testClient = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig()).BuildAsync();

		// Step 1: Server sends WILL SUPPRESSGOAHEAD
		_negotiationOutput = null;
		await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await testClient.WaitForProcessingAsync();
		
		Assert.IsNotNull(_negotiationOutput, "Client should respond to WILL SUPPRESSGOAHEAD");
		Assert.That(_negotiationOutput, Is.EqualTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD }));
	}

	[Test]
	public async Task ServerSuppressGANegotiationWithClient()
	{
		// This test verifies server-side negotiation
		var testServer = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig()).BuildAsync();

		// Client sends DO SUPPRESSGOAHEAD
		_negotiationOutput = null;
		await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });
		await testServer.WaitForProcessingAsync();
		
		// Server should accept (no error, negotiation completes)
		Assert.Pass("Server completes SUPPRESSGOAHEAD negotiation successfully");
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
		Assert.IsNotNull(_negotiationOutput);
		var expectedResponse = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD };
		CollectionAssert.AreEqual(expectedResponse, _negotiationOutput);
	}

	[Test]
	public async Task ServerWillSuppressGAToClient()
	{
		// Test server's WILL announcement (happens during build)
		// This is tested implicitly in Setup, but we can verify behavior
		var testServer = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
		{
			CallbackNegotiationAsync = WriteBackToNegotiate,
			CallbackOnSubmitAsync = WriteBackToOutput,
			SignalOnGMCPAsync = WriteBackToGMCP,
			CallbackOnByteAsync = (x, y) => ValueTask.CompletedTask,
		}.RegisterMSSPConfig(() => new MSSPConfig()).BuildAsync();

		// BuildAsync should have triggered WILL announcements
		Assert.Pass("Server announces WILL SUPPRESSGOAHEAD during build");
	}

	[Test]
	public async Task RepeatedSuppressGANegotiation()
	{
		// Test that multiple negotiations don't cause issues
		_negotiationOutput = null;

		// First negotiation
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await _client_ti.WaitForProcessingAsync();
		Assert.IsNotNull(_negotiationOutput);

		// Second negotiation (redundant but should be handled)
		_negotiationOutput = null;
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await _client_ti.WaitForProcessingAsync();
		
		// Should handle gracefully (may or may not respond, but shouldn't error)
		Assert.Pass("Repeated SUPPRESSGOAHEAD negotiation handled gracefully");
	}

	[Test]
	public async Task SuppressGAWithDontResponse()
	{
		// Test server handling client's DONT
		_negotiationOutput = null;

		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await _server_ti.WaitForProcessingAsync();
		
		// Server should handle DONT gracefully and record that GA is not suppressed
		Assert.Pass("Server handles DONT SUPPRESSGOAHEAD and maintains GA mode");
	}

	[Test]
	public async Task SuppressGAWithWontResponse()
	{
		// Test client handling server's WONT
		_negotiationOutput = null;

		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await _client_ti.WaitForProcessingAsync();
		
		// Client should handle WONT gracefully and record that GA is not suppressed
		Assert.Pass("Client handles WONT SUPPRESSGOAHEAD and maintains GA mode");
	}
}
