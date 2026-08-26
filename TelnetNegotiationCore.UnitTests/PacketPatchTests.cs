using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

public class PacketPatchTests : BaseTest
{
	// 400ms, not the 120ms elsewhere in this file's history: InterpretAndWaitAsync's
	// WaitForProcessingAsync sleeps 100ms after the channel drains, so a 120ms hold left only 20ms
	// of margin and AFragmentSplitAcrossTwoReadsIsNotFiredTwice could fire between its two feeds.
	private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(400);

	private static Task<TelnetInterpreter> ClientAsync(
		Func<ValueTask> onPrompt, List<string> lines) =>
		BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit((data, _, _) => { lines.Add(Encoding.ASCII.GetString(data)); return ValueTask.CompletedTask; })
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<PacketPatchProtocol>()
				.WithHoldTime(Hold)
				.OnPrompt(onPrompt));

	[Test]
	public async Task AnUnterminatedFragmentBecomesAPromptAfterTheHoldTime()
	{
		var lines = new List<string>();
		var prompts = 0;
		var client = await ClientAsync(() => { prompts++; return ValueTask.CompletedTask; }, lines);

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("What's your name, freejack?"));

		await Assert.That(await PollUntilAsync(() => prompts == 1)).IsTrue();
		await Assert.That(Encoding.ASCII.GetString(client.LastPromptBytes.Span))
			.IsEqualTo("What's your name, freejack?");
		await Assert.That(lines.Count).IsEqualTo(0);

		await client.DisposeAsync();
	}

	[Test]
	public async Task ACompleteLineIsNeverHeldAndNeverBecomesAPrompt()
	{
		var lines = new List<string>();
		var prompts = 0;
		var client = await ClientAsync(() => { prompts++; return ValueTask.CompletedTask; }, lines);

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("You wave.\r\n"));

		await Assert.That(lines.Count).IsEqualTo(1);
		await Assert.That(lines[0]).IsEqualTo("You wave.");

		await Task.Delay(Hold * 3);
		await Assert.That(prompts).IsEqualTo(0);

		await client.DisposeAsync();
	}

	[Test]
	public async Task AFragmentSplitAcrossTwoReadsIsNotFiredTwice()
	{
		var lines = new List<string>();
		var prompts = 0;
		var client = await ClientAsync(() => { prompts++; return ValueTask.CompletedTask; }, lines);

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("What's your "));
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("name, freejack?"));

		await Assert.That(await PollUntilAsync(() => prompts == 1)).IsTrue();
		await Task.Delay(Hold * 3);
		await Assert.That(prompts).IsEqualTo(1);
		await Assert.That(Encoding.ASCII.GetString(client.LastPromptBytes.Span))
			.IsEqualTo("What's your name, freejack?");

		await client.DisposeAsync();
	}

	[Test]
	public async Task AMarkedPromptRetiresTheHeuristicForTheRestOfTheConnection()
	{
		var lines = new List<string>();
		var prompts = 0;

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit((data, _, _) => { lines.Add(Encoding.ASCII.GetString(data)); return ValueTask.CompletedTask; })
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<SuppressGoAheadProtocol>()
				.OnPrompt(() => { prompts++; return ValueTask.CompletedTask; })
				.AddPlugin<PacketPatchProtocol>()
				.WithHoldTime(Hold)
				.OnPrompt(() => { prompts++; return ValueTask.CompletedTask; }));

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("HP:100>"));
		await InterpretAndWaitAsync(client, new byte[] { 255, 249 });
		await Assert.That(prompts).IsEqualTo(1);

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("HP:99>"));
		await Task.Delay(Hold * 3);
		await Assert.That(prompts).IsEqualTo(1);

		await client.DisposeAsync();
	}

	[Test]
	public async Task AHoldTimeOutsideItsRangeIsRejectedRatherThanClamped()
	{
		await Assert.That(() => new PacketPatchProtocol().WithHoldTime(TimeSpan.FromSeconds(11)))
			.Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => new PacketPatchProtocol().WithHoldTime(TimeSpan.FromMilliseconds(-1)))
			.Throws<ArgumentOutOfRangeException>();
	}

	[Test]
	public async Task TheDefaultHoldTimeIsFiveHundredMilliseconds()
	{
		await Assert.That(new PacketPatchProtocol().HoldTime).IsEqualTo(TimeSpan.FromMilliseconds(500));
	}
}
