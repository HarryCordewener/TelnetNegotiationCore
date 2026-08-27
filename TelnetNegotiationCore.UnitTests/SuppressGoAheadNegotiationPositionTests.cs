#nullable enable
using System;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Negotiation position on a peer's <c>WILL SUPPRESS-GO-AHEAD</c>, in both modes: RFC 1123 §3.2.2
/// requires accepting it, unconditionally.
/// </summary>
public class SuppressGoAheadNegotiationPositionTests : BaseTest
{
	[Test]
	public async Task AClientAcceptsSuppressGoAheadBecauseRfc1123RequiresIt()
	{
		byte[]? negotiation = null;

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
				.AddPlugin<SuppressGoAheadProtocol>());

		await InterpretAndWaitAsync(client, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

		await AssertByteArraysEqual(negotiation, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		await client.DisposeAsync();
	}

	/// <summary>RFC 858 §5: once suppression is accepted, "the IAC GA command should be treated as a NOP".</summary>
	/// <remarks>
	/// Unlike <c>PromptMarkerTests.AGoAheadIsANopOnceSuppressGoAheadIsInEffect</c>, this fixture
	/// registers only <c>SuppressGoAheadProtocol</c> -- pinning the NOP with no EOR plugin present at
	/// all, not merely one left un-negotiated.
	/// </remarks>
	[Test]
	public async Task AnAcceptedSuppressionMakesGoAheadANop()
	{
		var prompts = 0;

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<SuppressGoAheadProtocol>()
				.OnPrompt(() => { prompts++; return ValueTask.CompletedTask; }));

		await InterpretAndWaitAsync(client, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.GA });

		await Assert.That(prompts).IsEqualTo(0);

		await client.DisposeAsync();
	}

	/// <summary>
	/// A server's negotiation position on a peer's <c>WILL SUPPRESS-GO-AHEAD</c>: RFC 1123 §3.2.2
	/// names Server Telnet explicitly, so this must be accepted the same as the client side above.
	/// </summary>
	[Test]
	public async Task AServerAcceptsPeersSuppressGoAheadBecauseRfc1123RequiresIt()
	{
		byte[]? negotiation = null;

		var server = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
				.AddPlugin<SuppressGoAheadProtocol>());

		// The server's own opening WILL SUPPRESS-GO-AHEAD, unrelated to the peer's direction under test.
		negotiation = null;

		await InterpretAndWaitAsync(server, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

		await AssertByteArraysEqual(negotiation, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

		await server.DisposeAsync();
	}

	/// <summary>A peer resuming its own Go-Ahead (<c>IAC WONT SUPPRESS-GO-AHEAD</c>) after having suppressed it.</summary>
	[Test]
	public async Task AServerAcknowledgesAPeerResumingGoAhead()
	{
		byte[]? negotiation = null;

		var server = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
				.AddPlugin<SuppressGoAheadProtocol>());

		await InterpretAndWaitAsync(server, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		negotiation = null;

		await InterpretAndWaitAsync(server, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.SUPPRESSGOAHEAD });

		await AssertByteArraysEqual(negotiation, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });

		await server.DisposeAsync();
	}

	/// <summary>RFC 854 §3(b): a request for a mode already in effect must not be acknowledged.</summary>
	[Test]
	public async Task AServerStaysSilentOnARepeatedSuppressGoAheadRequest()
	{
		byte[]? negotiation = null;

		var server = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
				.AddPlugin<SuppressGoAheadProtocol>());

		await InterpretAndWaitAsync(server, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		negotiation = null;

		await InterpretAndWaitAsync(server, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

		await Assert.That(negotiation).IsNull();

		await server.DisposeAsync();
	}
}
