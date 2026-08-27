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


public class SuppressGATests : BaseTest
{

	[Test]
	public async Task ClientRespondsWithDoSuppressGAToServerWill()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
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

		// Assert - a client accepts suppression by default (RFC 1123 §3.2.2 requires it)
		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		// Cleanup
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerAcceptsDoSuppressGA()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
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

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
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
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		// Cleanup
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task ServerHandlesDontSuppressGA()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
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

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
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

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
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
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		// Cleanup
		await testClient.DisposeAsync();
	}

	[Test]
	public async Task ServerSuppressGANegotiationWithClient()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
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

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
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
		await AssertByteArraysEqual(negotiationOutput, expectedResponse);

		// Cleanup
		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task SuppressGAWithDontResponse()
	{
		// Arrange
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
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

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
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

	// The following pin RFC 854 §3(b) in client mode: a server's DO/DONT SUPPRESS-GO-AHEAD asks this
	// client to suppress its *own* outbound Go-Ahead, a direction independent of the server's WILL/WONT
	// (RFC 858 §5), and §3(b) requires a response to a genuine change of mode but silence otherwise.

	[Test]
	public async Task AClientAnswersAnInboundDoSuppressGoAhead()
	{
		byte[] negotiationOutput = null;

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiationOutput = data.ToArray();
			return ValueTask.CompletedTask;
		}

		var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		// Act - server asks this client to suppress its own outbound Go-Ahead
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		// Assert - RFC 1123 §3.2.2 requires accepting; the answer is truthful since this client never sends GA anyway
		await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task AClientAnswersAnInboundDontSuppressGoAheadOnlyWhenItIsAChange()
	{
		var negotiations = new List<byte[]>();

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiations.Add(data.ToArray());
			return ValueTask.CompletedTask;
		}

		var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		// DO changes us into suppressing -> WILL
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });
		await Assert.That(negotiations.Count).IsEqualTo(1);
		await AssertByteArraysEqual(negotiations[0], new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

		// DONT changes us back to not suppressing -> WONT
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await Assert.That(negotiations.Count).IsEqualTo(2);
		await AssertByteArraysEqual(negotiations[1], new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.SUPPRESSGOAHEAD });

		// A second DONT is a request for the mode we're already in -- RFC 854 §3(b) says stay silent
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });
		await Assert.That(negotiations.Count).IsEqualTo(2);

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task AnInboundDoSuppressGoAheadIsNotAnUnhandledTrigger()
	{
		var negotiations = new List<byte[]>();

		ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
		{
			negotiations.Add(data.ToArray());
			return ValueTask.CompletedTask;
		}

		var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(CaptureNegotiation)
			.AddPlugin<SuppressGoAheadProtocol>());

		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		// A byte arriving right after would be mis-parsed if the interpreter had instead recovered
		// through Trigger.Error: an ordinary WILL for an unrelated, unregistered option would not
		// get its normal (generic-refusal) answer.
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });

		await Assert.That(negotiations.Count).IsEqualTo(2);
		await AssertByteArraysEqual(negotiations[0], new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await AssertByteArraysEqual(negotiations[1], new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.ECHO });

		await client_ti.DisposeAsync();
	}

	[Test]
	public async Task AnsweringAnInboundDoSuppressGoAheadDoesNotChangeHowGoAheadIsTreated()
	{
		var prompts = 0;

		var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<SuppressGoAheadProtocol>()
				.OnPrompt(() => { prompts++; return ValueTask.CompletedTask; }));

		// This client agrees to suppress its own outbound GA...
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		// ...which says nothing about whether the server's own GA still marks a prompt: that is
		// governed only by the server's WILL/WONT, which never happened here.
		await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.GA });

		await Assert.That(prompts).IsEqualTo(1);

		await client_ti.DisposeAsync();
	}
}
