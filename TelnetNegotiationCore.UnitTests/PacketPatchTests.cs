using System;
using System.Collections.Generic;
using System.Linq;
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
	// InterpretAndWaitAsync settles for 100ms after the channel drains, so the hold needs real
	// margin above that to avoid flaking AFragmentSplitAcrossTwoReadsIsNotFiredTwice.
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

		// Pins that this prompt comes from Packet Patch itself, not Suppress Go-Ahead.
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("HP:100>"));
		await Assert.That(await PollUntilAsync(() => prompts == 1)).IsTrue();

		await InterpretAndWaitAsync(client, new byte[] { 255, 249 });
		await Assert.That(prompts).IsEqualTo(2);

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("HP:99>"));
		await Task.Delay(Hold * 3);
		await Assert.That(prompts).IsEqualTo(2);

		await client.DisposeAsync();
	}

	[Test]
	public async Task TheFragmentIsNotReportedBeforeTheHoldTimeElapses()
	{
		var lines = new List<string>();
		var prompts = 0;
		var client = await ClientAsync(() => { prompts++; return ValueTask.CompletedTask; }, lines);

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("What's your name, freejack?"));

		// Pins the hold as an actual duration, not just a lower bound a 1ms hold would also satisfy.
		await Task.Delay(Hold / 2);
		await Assert.That(prompts).IsEqualTo(0);

		await Assert.That(await PollUntilAsync(() => prompts == 1)).IsTrue();

		await client.DisposeAsync();
	}

	[Test]
	public async Task PacketPatchIsInertInServerMode()
	{
		var lines = new List<string>();
		var prompts = 0;

		var server = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit((data, _, _) => { lines.Add(Encoding.ASCII.GetString(data)); return ValueTask.CompletedTask; })
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<PacketPatchProtocol>()
				.WithHoldTime(Hold)
				.OnPrompt(() => { prompts++; return ValueTask.CompletedTask; }));

		await InterpretAndWaitAsync(server, Encoding.ASCII.GetBytes("look"));
		await Task.Delay(Hold * 3);
		await Assert.That(prompts).IsEqualTo(0);

		await server.DisposeAsync();
	}

	[Test]
	public async Task NoFurtherInferredPromptArrivesAfterTheHeuristicIsDisabledAtRuntime()
	{
		var lines = new List<string>();
		var prompts = 0;
		var client = await ClientAsync(() => { prompts++; return ValueTask.CompletedTask; }, lines);
		var plugin = client.PluginManager!.GetPlugin<PacketPatchProtocol>()!;

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("What's your name, freejack?"));

		// Disables well before the hold elapses, so this proves OnProtocolDisabledAsync itself
		// disarms the timer, not that disable happened to win a race it would have won anyway.
		await client.PluginManager!.DisablePluginAsync<PacketPatchProtocol>();

		// Waits past the pre-disable arm's own deadline before re-enabling, so a callback dispatched
		// under that arm and only now getting its turn would, without the reset, find "now" already
		// past its stale deadline and pass OnTimerElapsed's guard.
		await Task.Delay(Hold * 2);
		await Assert.That(prompts).IsEqualTo(0);

		// Re-enabling must not resurrect the pre-disable arm: OnProtocolEnabledAsync re-registers
		// the handlers but never touches the timer or the deadline. Invoked directly (OnTimerElapsed
		// is internal for exactly this) since nothing can make the thread pool run a stale callback
		// late enough to land after a real disable/enable pair on demand.
		await client.PluginManager!.EnablePluginAsync<PacketPatchProtocol>();
		plugin.OnTimerElapsed(null);
		await client.WaitForProcessingAsync();
		await Assert.That(prompts).IsEqualTo(0);

		await client.DisposeAsync();
	}

	[Test]
	public async Task AStaleTimerCallbackDropsItselfAfterARearm()
	{
		// Timer.Change cannot cancel a callback already dispatched to the thread pool. Nothing can
		// make the pool stall a queued callback on demand, so this drives _armDeadline directly:
		// hold a fragment, extend it (a genuine re-arm), then invoke the elapsed path immediately --
		// modelling a callback queued under the first arm, only now getting its turn to run.
		var lines = new List<string>();
		var prompts = 0;
		var client = await ClientAsync(() => { prompts++; return ValueTask.CompletedTask; }, lines);
		var plugin = client.PluginManager!.GetPlugin<PacketPatchProtocol>()!;

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("What's your "));
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("name, "));

		plugin.OnTimerElapsed(null);
		await Assert.That(prompts).IsEqualTo(0);

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("freejack?"));
		await Assert.That(await PollUntilAsync(() => prompts == 1)).IsTrue();
		await Assert.That(Encoding.ASCII.GetString(client.LastPromptBytes.Span))
			.IsEqualTo("What's your name, freejack?");

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

	[Test]
	public async Task ABurstSpanningTheHoldDeadlineNeverMangleAnOrdinaryLine()
	{
		// Holds "Prompt> " and lets it arm at the idle byte that follows. The first slice of the
		// burst is sized to just clear the byte channel's 10,000 bound, so InterpretByteArrayAsync's
		// own backpressure forces a real write/consume handoff: the write cannot return until the
		// loop has consumed at least one burst byte, which is what disarms the fix's byte-driven
		// arm, and the loop has not had the chance to drain the rest, so the channel is still
		// non-empty. OnTimerElapsed(null) is invoked right there, at a zero hold time so the deadline
		// is never early -- modelling the pre-fix code reaching its deadline mid-burst, with no sleep
		// and no margin to flake under load. The rest of the burst follows once that has resolved.
		const string roomSuffix = "The Great Hall of Dwarves, a long description of the room.";
		var lines = new List<string>();
		var prompts = 0;
		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit((data, _, _) => { lines.Add(Encoding.ASCII.GetString(data)); return ValueTask.CompletedTask; })
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<PacketPatchProtocol>()
				.WithHoldTime(TimeSpan.Zero)
				.OnPrompt(() => { prompts++; return ValueTask.CompletedTask; }));
		var plugin = client.PluginManager!.GetPlugin<PacketPatchProtocol>()!;

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("Prompt> "));

		var burst = new StringBuilder();
		for (var i = 0; i < 400; i++)
		{
			burst.Append($"ROOM{i:0000} {roomSuffix}\r\n");
		}

		var burstBytes = Encoding.ASCII.GetBytes(burst.ToString());
		const int channelBound = 10000;
		await client.InterpretByteArrayAsync(burstBytes.AsMemory(0, channelBound + 1));
		plugin.OnTimerElapsed(null);
		await client.InterpretByteArrayAsync(burstBytes.AsMemory(channelBound + 1));
		await client.WaitForProcessingAsync(maxWaitMs: 10000, additionalDelayMs: 500);

		bool IsWholeLine(string l) =>
			l.EndsWith(roomSuffix, StringComparison.Ordinal)
			&& (l.StartsWith("ROOM", StringComparison.Ordinal) || l.StartsWith("Prompt> ROOM", StringComparison.Ordinal));

		var badLines = lines.Where(l => !IsWholeLine(l)).ToList();
		await Assert.That(badLines).IsEmpty();
		await Assert.That(lines.Count).IsEqualTo(400);
		await Assert.That(prompts <= 1).IsTrue();

		await client.DisposeAsync();
	}

	[Test]
	public async Task AddDefaultMUDProtocolsWithNoPromptCallbackDoesNotRegisterPacketPatch()
	{
		var lines = new List<string>();
		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit((data, _, _) => { lines.Add(Encoding.ASCII.GetString(data)); return ValueTask.CompletedTask; })
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddDefaultMUDProtocols());

		await Assert.That(client.PluginManager!.GetPlugin<PacketPatchProtocol>()).IsNull();

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("Enter your name: "));
		await Task.Delay(TimeSpan.FromSeconds(1));
		await Assert.That(lines.Count).IsEqualTo(0);

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("Bob\r\n"));
		await Assert.That(lines.Count).IsEqualTo(1);
		await Assert.That(lines[0]).IsEqualTo("Enter your name: Bob");

		await client.DisposeAsync();
	}
}
