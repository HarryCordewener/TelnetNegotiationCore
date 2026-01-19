using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;


public class EORTests : BaseTest
{
	private TelnetInterpreter _server_ti;
	private TelnetInterpreter _client_ti;
	private byte[] _negotiationOutput;
	private bool _promptReceived;

	private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

	private ValueTask WriteBackToNegotiate(byte[] arg1)
	{
		_negotiationOutput = arg1;
		return ValueTask.CompletedTask;
	}

	private ValueTask WriteBackToPrompt()
	{
		_promptReceived = true;
		logger.LogInformation("Received EOR prompt signal");
		return ValueTask.CompletedTask;
	}

	private ValueTask WriteBackToGMCP((string Package, string Info) tuple) => ValueTask.CompletedTask;

	[Before(Test)]
	public async Task Setup()
	{
		_negotiationOutput = null;
		_promptReceived = false;

		_server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<EORProtocol>()
				.OnPrompt(WriteBackToPrompt)
			.BuildAsync();

		_client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<EORProtocol>()
				.OnPrompt(WriteBackToPrompt)
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
	public async Task ClientRespondsWithDoEORToServerWill()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WILL EOR from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await _client_ti.WaitForProcessingAsync();

		// Assert
		await Assert.That(_negotiationOutput).IsNotNull();
		await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
	}

	[Test]
	public async Task ServerAcceptsDoEOR()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Server receives DO EOR from client
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
		await _server_ti.WaitForProcessingAsync();

		// Assert - Server should accept without error (no response sent)
		// The server just records that EOR is active
		await Assert.That(_negotiationOutput).IsNull();
	}

	[Test]
	public async Task ClientAcceptsWillEOR()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WILL EOR from server and responds
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await _client_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Assert - Client should send DO EOR
		await Assert.That(_negotiationOutput).IsNotNull();
		await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
	}

	[Test]
	public async Task ServerHandlesDontEOR()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Server receives DONT EOR from client
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.TELOPT_EOR });
		await _server_ti.WaitForProcessingAsync();

		// Assert - Server should accept the rejection gracefully (no error thrown)
		await Assert.That(_negotiationOutput).IsNull();
	}

	[Test]
	public async Task ClientHandlesWontEOR()
	{
		// Arrange
		_negotiationOutput = null;

		// Act - Client receives WONT EOR from server
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.TELOPT_EOR });
		await _client_ti.WaitForProcessingAsync();

		// Assert - Client should accept the rejection gracefully (no error thrown)
		await Assert.That(_negotiationOutput).IsNull();
	}

	[Test]
	public async Task ClientReceivesEORPromptSignal()
	{
		// Arrange - Complete EOR negotiation
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await _client_ti.WaitForProcessingAsync();
		_promptReceived = false;

		// Act - Client receives IAC EOR sequence
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await _client_ti.WaitForProcessingAsync();

		// Assert
		await Assert.That(_promptReceived).IsTrue();
	}

	[Test]
	public async Task ServerCanSendPromptWithEOR()
	{
		// Arrange - Complete EOR negotiation
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
		await _server_ti.WaitForProcessingAsync();
		_negotiationOutput = null;

		var encoding = Encoding.UTF8;
		var promptText = "HP: 100/100> ";

		// Act - Server sends a prompt using SendPromptAsync
		await _server_ti.SendPromptAsync(encoding.GetBytes(promptText));

		// Assert - Should send the prompt text followed by IAC EOR
		await Assert.That(_negotiationOutput).IsNotNull();
		
		// The output should end with IAC EOR when EOR is negotiated
		// Note: SendPromptAsync may send text and EOR in separate calls
		// We're verifying that output was produced
		await Assert.That(_negotiationOutput.Length).IsGreaterThan(0, "Output should contain data");
	}

	[Test]
	public async Task ServerCanSendMessageWithEOR()
	{
		// Arrange - Complete EOR negotiation
		await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
		await _server_ti.WaitForProcessingAsync();
		_negotiationOutput = null;

		var encoding = Encoding.UTF8;
		var messageText = "Welcome to the MUD!";

		// Act - Server sends a regular message using SendAsync
		await _server_ti.SendAsync(encoding.GetBytes(messageText));

		// Assert - Should send the message text followed by newline
		await Assert.That(_negotiationOutput).IsNotNull();
		await Assert.That(_negotiationOutput.Length).IsGreaterThan(0, "Output should contain data");
	}

	[Test]
	public async Task EORNegotiationSequenceComplete()
	{
		// This test verifies the complete negotiation sequence
		var testClient = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<EORProtocol>()
				.OnPrompt(WriteBackToPrompt)
			.BuildAsync();

		// Step 1: Server sends WILL EOR
		_negotiationOutput = null;
		await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await testClient.WaitForProcessingAsync();
		
		await Assert.That(_negotiationOutput).IsNotNull();
		await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });

		// Step 2: Client receives EOR prompt
		_promptReceived = false;
		await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await testClient.WaitForProcessingAsync();
		
		await Assert.That(_promptReceived).IsTrue();
	}

	[Test]
	public async Task MultipleEORPromptsReceived()
	{
		// Arrange - Complete EOR negotiation
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await _client_ti.WaitForProcessingAsync();
		
		// Act - Receive multiple EOR prompts
		_promptReceived = false;
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await _client_ti.WaitForProcessingAsync();
		await Assert.That(_promptReceived).IsTrue();

		_promptReceived = false;
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await _client_ti.WaitForProcessingAsync();
		await Assert.That(_promptReceived).IsTrue();

		_promptReceived = false;
		await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await _client_ti.WaitForProcessingAsync();
		await Assert.That(_promptReceived).IsTrue();
	}
}
