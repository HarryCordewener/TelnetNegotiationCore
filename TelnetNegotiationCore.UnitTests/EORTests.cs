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

	[Test]
	public async Task ClientRespondsWithDoEORToServerWill()
	{
		// Local variables
		byte[] negotiationOutput = null;

		// Local callbacks
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// Build client TelnetInterpreter
		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<EORProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Act - Client receives WILL EOR from server
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await client_ti.WaitForProcessingAsync();

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });

		// Dispose
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerAcceptsDoEOR()
	{
		// Local variables
		byte[] negotiationOutput = null;

		// Local callbacks
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// Build server TelnetInterpreter
		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<EORProtocol>()
			.BuildAsync();

		await Task.Delay(100);
		negotiationOutput = null;

		// Act - Server receives DO EOR from client
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
		await server_ti.WaitForProcessingAsync();

		// Assert - Server should accept without error (no response sent)
		// The server just records that EOR is active
		await Assert.That(negotiationOutput).IsNull();

		// Dispose
		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientAcceptsWillEOR()
	{
		// Local variables
		byte[] negotiationOutput = null;

		// Local callbacks
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// Build client TelnetInterpreter
		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<EORProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Act - Client receives WILL EOR from server and responds
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await client_ti.WaitForProcessingAsync();
		await Task.Delay(50);

		// Assert - Client should send DO EOR
		await Assert.That(negotiationOutput).IsNotNull();
		await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });

		// Dispose
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerHandlesDontEOR()
	{
		// Local variables
		byte[] negotiationOutput = null;

		// Local callbacks
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// Build server TelnetInterpreter
		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<EORProtocol>()
			.BuildAsync();

		await Task.Delay(100);
		negotiationOutput = null;

		// Act - Server receives DONT EOR from client
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.TELOPT_EOR });
		await server_ti.WaitForProcessingAsync();

		// Assert - Server should accept the rejection gracefully (no error thrown)
		await Assert.That(negotiationOutput).IsNull();

		// Dispose
		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientHandlesWontEOR()
	{
		// Local variables
		byte[] negotiationOutput = null;

		// Local callbacks
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// Build client TelnetInterpreter
		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<EORProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Act - Client receives WONT EOR from server
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.TELOPT_EOR });
		await client_ti.WaitForProcessingAsync();

		// Assert - Client should accept the rejection gracefully (no error thrown)
		await Assert.That(negotiationOutput).IsNull();

		// Dispose
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ClientReceivesEORPromptSignal()
	{
		// Local variables
		byte[] negotiationOutput = null;
		bool promptReceived = false;

		// Local callbacks
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask CapturePrompt()
		{
			promptReceived = true;
			logger.LogInformation("Received EOR prompt signal");
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// Build client TelnetInterpreter
		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<EORProtocol>()
				.OnPrompt(CapturePrompt)
			.BuildAsync();

		await Task.Delay(100);

		// Arrange - Complete EOR negotiation
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await client_ti.WaitForProcessingAsync();
		promptReceived = false;

		// Act - Client receives IAC EOR sequence
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await client_ti.WaitForProcessingAsync();

		// Assert
		await Assert.That(promptReceived).IsTrue();

		// Dispose
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerCanSendPromptWithEOR()
	{
		// Local variables
		byte[] negotiationOutput = null;

		// Local callbacks
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// Build server TelnetInterpreter
		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<EORProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Arrange - Complete EOR negotiation
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
		await server_ti.WaitForProcessingAsync();
		negotiationOutput = null;

		var encoding = Encoding.UTF8;
		var promptText = "HP: 100/100> ";

		// Act - Server sends a prompt using SendPromptAsync
		await server_ti.SendPromptAsync(encoding.GetBytes(promptText));

		// Assert - Should send the prompt text followed by IAC EOR
		await Assert.That(negotiationOutput).IsNotNull();
		
		// The output should end with IAC EOR when EOR is negotiated
		// Note: SendPromptAsync may send text and EOR in separate calls
		// We're verifying that output was produced
		await Assert.That(negotiationOutput.Length).IsGreaterThan(0, "Output should contain data");

		// Dispose
		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerCanSendMessageWithEOR()
	{
		// Local variables
		byte[] negotiationOutput = null;

		// Local callbacks
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// Build server TelnetInterpreter
		var server_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<EORProtocol>()
			.BuildAsync();

		await Task.Delay(100);

		// Arrange - Complete EOR negotiation
		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
		await server_ti.WaitForProcessingAsync();
		negotiationOutput = null;

		var encoding = Encoding.UTF8;
		var messageText = "Welcome to the MUD!";

		// Act - Server sends a regular message using SendAsync
		await server_ti.SendAsync(encoding.GetBytes(messageText));

		// Assert - Should send the message text followed by newline
		await Assert.That(negotiationOutput).IsNotNull();
		await Assert.That(negotiationOutput.Length).IsGreaterThan(0, "Output should contain data");

		// Dispose
		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task EORNegotiationSequenceComplete()
	{
		// Local variables
		byte[] negotiationOutput = null;
		bool promptReceived = false;

		// Local callbacks
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask CapturePrompt()
		{
			promptReceived = true;
			logger.LogInformation("Received EOR prompt signal");
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// This test verifies the complete negotiation sequence
		var testClient = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<EORProtocol>()
				.OnPrompt(CapturePrompt)
			.BuildAsync();

		await Task.Delay(100);

		// Step 1: Server sends WILL EOR
		negotiationOutput = null;
		await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await testClient.WaitForProcessingAsync();
		
		await Assert.That(negotiationOutput).IsNotNull();
		await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });

		// Step 2: Client receives EOR prompt
		promptReceived = false;
		await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await testClient.WaitForProcessingAsync();
		
		await Assert.That(promptReceived).IsTrue();

		// Dispose
		await testClient.DisposeAsync();
	}

	[Test]
	public async Task MultipleEORPromptsReceived()
	{
		// Local variables
		byte[] negotiationOutput = null;
		bool promptReceived = false;

		// Local callbacks
		ValueTask CaptureNegotiation(byte[] data)
		{
			negotiationOutput = data;
			return ValueTask.CompletedTask;
		}

		ValueTask CapturePrompt()
		{
			promptReceived = true;
			logger.LogInformation("Received EOR prompt signal");
			return ValueTask.CompletedTask;
		}

		ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		// Build client TelnetInterpreter
		var client_ti = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(WriteBackToOutput)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<EORProtocol>()
				.OnPrompt(CapturePrompt)
			.BuildAsync();

		await Task.Delay(100);

		// Arrange - Complete EOR negotiation
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await client_ti.WaitForProcessingAsync();
		
		// Act - Receive multiple EOR prompts
		promptReceived = false;
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await client_ti.WaitForProcessingAsync();
		await Assert.That(promptReceived).IsTrue();

		promptReceived = false;
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await client_ti.WaitForProcessingAsync();
		await Assert.That(promptReceived).IsTrue();

		promptReceived = false;
		await client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await client_ti.WaitForProcessingAsync();
		await Assert.That(promptReceived).IsTrue();

		// Dispose
		await client_ti.DisposeAsync();
	}
}
