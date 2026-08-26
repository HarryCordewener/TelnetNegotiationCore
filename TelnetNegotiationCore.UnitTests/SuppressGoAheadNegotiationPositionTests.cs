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
/// A client's negotiation position on a server's <c>WILL SUPPRESS-GO-AHEAD</c>: RFC 1123 §3.2.2
/// requires accepting it, so acceptance is the default, and refusing is an opt-in that costs this
/// client its RFC 854 prompt marker.
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

	[Test]
	public async Task AClientThatOptsOutRefusesSuppressGoAhead()
	{
		byte[]? negotiation = null;

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
				.AddPlugin<SuppressGoAheadProtocol>()
				.RefuseSuppression());

		await InterpretAndWaitAsync(client, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

		await AssertByteArraysEqual(negotiation, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });

		await client.DisposeAsync();
	}

	[Test]
	public async Task ARefusedSuppressionLeavesGoAheadMeaningAPrompt()
	{
		var prompts = 0;

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<SuppressGoAheadProtocol>()
				.RefuseSuppression()
				.OnPrompt(() => { prompts++; return ValueTask.CompletedTask; }));

		await InterpretAndWaitAsync(client, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.GA });

		await Assert.That(prompts).IsEqualTo(1);

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
}
