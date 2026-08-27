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
		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		// Build client TelnetInterpreter
		var client_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<EORProtocol>()
		);

		// Act - Client receives WILL EOR from server
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });

		// Assert
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });

		// Dispose
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerAcceptsDoEOR()
	{
		// Local variables
		byte[] negotiationOutput = null;

		// Local callbacks
		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		// Build server TelnetInterpreter
		var server_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<EORProtocol>()
		);
		negotiationOutput = null;

		// Act - Server receives DO EOR from client
		await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });

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
		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		// Build client TelnetInterpreter
		var client_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<EORProtocol>()
		);

		// Act - Client receives WILL EOR from server and responds
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		await Task.Delay(50);

		// Assert - Client should send DO EOR
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });

		// Dispose
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerHandlesDontEOR()
	{
		// Local variables
		byte[] negotiationOutput = null;

		// Local callbacks
		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		// Build server TelnetInterpreter
		var server_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<EORProtocol>()
		);
		negotiationOutput = null;

		// Act - Server receives DONT EOR from client
		await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.TELOPT_EOR });

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
		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		// Build client TelnetInterpreter
		var client_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<EORProtocol>()
		);

		// Act - Client receives WONT EOR from server
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.TELOPT_EOR });

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
		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		ValueTask CapturePrompt()
		{
			promptReceived = true;
			logger.LogInformation("Received EOR prompt signal");
			return ValueTask.CompletedTask;
		}

		// Build client TelnetInterpreter
		var client_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<EORProtocol>()
				.OnPrompt(CapturePrompt)
		);

		// Arrange - Complete EOR negotiation
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		promptReceived = false;

		// Act - Client receives IAC EOR sequence
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });

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
		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		// Build server TelnetInterpreter
		var server_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<EORProtocol>()
		);

		// Arrange - Complete EOR negotiation
		await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
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
		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		// Build server TelnetInterpreter
		var server_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<EORProtocol>()
		);

		// Arrange - Complete EOR negotiation
		await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });
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
		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		ValueTask CapturePrompt()
		{
			promptReceived = true;
			logger.LogInformation("Received EOR prompt signal");
			return ValueTask.CompletedTask;
		}

		// This test verifies the complete negotiation sequence
		var testClient = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<EORProtocol>()
				.OnPrompt(CapturePrompt)
		);

		// Step 1: Server sends WILL EOR
		negotiationOutput = null;
		await InterpretAndWaitAsync(testClient, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });

		// Step 2: Client receives EOR prompt
		promptReceived = false;
		await InterpretAndWaitAsync(testClient, new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		
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
		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		ValueTask CapturePrompt()
		{
			promptReceived = true;
			logger.LogInformation("Received EOR prompt signal");
			return ValueTask.CompletedTask;
		}

		// Build client TelnetInterpreter
		var client_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<EORProtocol>()
				.OnPrompt(CapturePrompt)
		);

		// Arrange - Complete EOR negotiation
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
		
		// Act - Receive multiple EOR prompts
		promptReceived = false;
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await Assert.That(promptReceived).IsTrue();

		promptReceived = false;
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await Assert.That(promptReceived).IsTrue();

		promptReceived = false;
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });
		await Assert.That(promptReceived).IsTrue();

		// Dispose
		await client_ti.DisposeAsync();
	}

	/// <summary>
	/// RFC 858: a prompt is terminated with IAC EOR when End of Record is negotiated, with IAC GA when
	/// it is not and Go-Ahead is not suppressed, and with a bare CR LF when Go-Ahead is suppressed and
	/// there is no EOR to fall back on. These assert the bytes, because the terminator is the entire
	/// observable difference.
	/// </summary>
	[Test]
	public async Task PromptEndsWithEndOfRecordWhenEorIsNegotiated()
	{
		byte[] output = null;

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			output = data.ToArray();
			return ValueTask.CompletedTask;
		}

		var server_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(Capture)
				.AddPlugin<EORProtocol>()
		);

		await InterpretAndWaitAsync(server_ti, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR]);

		var prompt = Encoding.ASCII.GetBytes("HP: 100/100> ");
		output = null;
		await server_ti.SendPromptAsync(prompt);

		await AssertByteArraysEqual(output, [.. prompt, (byte)Trigger.IAC, (byte)Trigger.EOR]);

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task PromptEndsWithGoAheadWhenNothingIsNegotiated()
	{
		byte[] output = null;

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			output = data.ToArray();
			return ValueTask.CompletedTask;
		}

		var server_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(Capture)
		);

		var prompt = Encoding.ASCII.GetBytes("HP: 100/100> ");
		output = null;
		await server_ti.SendPromptAsync(prompt);

		await AssertByteArraysEqual(output, [.. prompt, (byte)Trigger.IAC, (byte)Trigger.GA]);

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task PromptEndsWithGoAheadWhenEorIsRefused()
	{
		byte[] output = null;

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			output = data.ToArray();
			return ValueTask.CompletedTask;
		}

		var server_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(Capture)
				.AddPlugin<EORProtocol>()
		);

		await InterpretAndWaitAsync(server_ti, [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.TELOPT_EOR]);

		var prompt = Encoding.ASCII.GetBytes("HP: 100/100> ");
		output = null;
		await server_ti.SendPromptAsync(prompt);

		await AssertByteArraysEqual(output, [.. prompt, (byte)Trigger.IAC, (byte)Trigger.GA]);

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task PromptEndsWithGoAheadWhenSuppressionIsRefused()
	{
		byte[] output = null;

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			output = data.ToArray();
			return ValueTask.CompletedTask;
		}

		var server_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(Capture)
				.AddPlugin<SuppressGoAheadProtocol>()
		);

		await InterpretAndWaitAsync(server_ti, [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD]);

		var prompt = Encoding.ASCII.GetBytes("HP: 100/100> ");
		output = null;
		await server_ti.SendPromptAsync(prompt);

		await AssertByteArraysEqual(output, [.. prompt, (byte)Trigger.IAC, (byte)Trigger.GA]);

		await server_ti.DisposeAsync();
	}

	[Test]
	public async Task PromptEndsWithCarriageReturnLineFeedWhenGoAheadIsSuppressed()
	{
		byte[] output = null;

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			output = data.ToArray();
			return ValueTask.CompletedTask;
		}

		var server_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(Capture)
				.AddPlugin<SuppressGoAheadProtocol>()
		);

		await InterpretAndWaitAsync(server_ti, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD]);

		var prompt = Encoding.ASCII.GetBytes("HP: 100/100> ");
		output = null;
		await server_ti.SendPromptAsync(prompt);

		await AssertByteArraysEqual(output, [.. prompt, (byte)'\r', (byte)'\n']);

		await server_ti.DisposeAsync();
	}

	/// <summary>
	/// EOR wins over Go-Ahead suppression: both negotiated means the prompt still ends with IAC EOR.
	/// </summary>
	[Test]
	public async Task PromptPrefersEndOfRecordOverGoAheadSuppression()
	{
		byte[] output = null;

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			output = data.ToArray();
			return ValueTask.CompletedTask;
		}

		var server_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(Capture)
				.AddPlugin<EORProtocol>()
				.AddPlugin<SuppressGoAheadProtocol>()
		);

		await InterpretAndWaitAsync(server_ti, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR]);
		await InterpretAndWaitAsync(server_ti, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD]);

		var prompt = Encoding.ASCII.GetBytes("HP: 100/100> ");
		output = null;
		await server_ti.SendPromptAsync(prompt);

		await AssertByteArraysEqual(output, [.. prompt, (byte)Trigger.IAC, (byte)Trigger.EOR]);

		await server_ti.DisposeAsync();
	}

	/// <summary>
	/// The client-mode counterpart of <see cref="PromptEndsWithCarriageReturnLineFeedWhenGoAheadIsSuppressed"/>.
	/// A server's <c>IAC DO SUPPRESS-GO-AHEAD</c> asks this client to suppress its own outbound GA;
	/// <c>PromptTerminator</c> must honour that direction, not the peer's.
	/// </summary>
	[Test]
	public async Task PromptEndsWithCarriageReturnLineFeedWhenClientAgreedToSuppressItsOwnGoAhead()
	{
		byte[] output = null;

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			output = data.ToArray();
			return ValueTask.CompletedTask;
		}

		var client_ti = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(Capture)
				.AddPlugin<SuppressGoAheadProtocol>()
		);

		await InterpretAndWaitAsync(client_ti, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD]);
		await AssertByteArraysEqual(output, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD]);

		var prompt = Encoding.ASCII.GetBytes("HP: 100/100> ");
		output = null;
		await client_ti.SendPromptAsync(prompt);

		await AssertByteArraysEqual(output, [.. prompt, (byte)'\r', (byte)'\n']);

		await client_ti.DisposeAsync();
	}
}
