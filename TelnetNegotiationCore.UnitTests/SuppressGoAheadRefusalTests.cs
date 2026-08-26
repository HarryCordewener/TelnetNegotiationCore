#nullable enable
using System;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

public class SuppressGoAheadRefusalTests : BaseTest
{
	[Test]
	public async Task AClientRefusesSuppressGoAheadSoThatGaKeepsMarkingPrompts()
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
			{ (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });

		await client.DisposeAsync();
	}

	[Test]
	public async Task AClientThatOptsInStillAcceptsSuppressGoAhead()
	{
		byte[]? negotiation = null;

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
				.AddPlugin<SuppressGoAheadProtocol>()
				.AcceptSuppression(true));

		await InterpretAndWaitAsync(client, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

		await AssertByteArraysEqual(negotiation, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

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
				.OnPrompt(() => { prompts++; return ValueTask.CompletedTask; }));

		await InterpretAndWaitAsync(client, new byte[]
			{ (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });
		await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.GA });

		await Assert.That(prompts).IsEqualTo(1);

		await client.DisposeAsync();
	}
}
