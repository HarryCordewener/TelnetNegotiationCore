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

		// Nothing marked has arrived yet, so this first prompt can only come from Packet Patch's own
		// hold-time timer. Pinning it here -- rather than jumping straight to the GA -- is what tells
		// this test apart from one where Packet Patch never fires at all: without it, prompts == 1
		// after the GA below would be just as consistent with a completely inert plugin, since
		// Suppress Go-Ahead alone produces that count.
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("HP:100>"));
		await Assert.That(await PollUntilAsync(() => prompts == 1)).IsTrue();

		// A genuine marker latches the heuristic off, and still reports its own prompt.
		await InterpretAndWaitAsync(client, new byte[] { 255, 249 });
		await Assert.That(prompts).IsEqualTo(2);

		// A new fragment must never again reach Packet Patch's timer, now that the connection has
		// proven it marks its own prompts.
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

		// Pins the hold as an actual duration rather than a lower bound: every other assertion in this
		// file polls for prompts == 1, which a 1ms hold would also satisfy. InterpretAndWaitAsync has
		// already spent ~100ms of the 400ms hold settling, and this delay spends 200ms more -- ~300ms
		// total, so the margin against the 400ms boundary is ~100ms, not symmetric on both sides of it.
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

		// Disabling well before the hold time elapses (only ~100ms of it has passed, see
		// InterpretAndWaitAsync) is deliberate: it proves OnProtocolDisabledAsync actually disarms
		// the already-armed timer, not merely that disabling happened to race ahead of a fire that
		// was going to lose anyway. Without that disarm, the timer set by the fragment above would
		// still be pending and would still enqueue a sentinel around the 400ms mark regardless of
		// when disable ran.
		await client.PluginManager!.DisablePluginAsync<PacketPatchProtocol>();

		// Waited out here, past the pre-disable arm's own deadline, rather than only around the
		// disable itself: this is what makes the re-enable check below actually exercise the
		// scenario in the finding. A callback dispatched under that arm and only now getting its
		// turn to run would, without OnProtocolDisabledAsync resetting _armDeadline, find "now" past
		// its (stale) deadline and pass OnTimerElapsed's guard.
		await Task.Delay(Hold * 2);
		await Assert.That(prompts).IsEqualTo(0);

		// Re-enabling must not resurrect the pre-disable arm. OnProtocolDisabledAsync resets
		// _armDeadline to long.MaxValue alongside disarming, specifically so a callback dispatched
		// before disable ran -- or one the thread pool only gets around to running after re-enable has
		// already re-registered fresh handlers -- still finds a deadline in the far future and drops
		// itself. OnProtocolEnabledAsync re-registers the handlers but never touches the timer or the
		// deadline, so nothing here re-arms it; invoked directly (OnTimerElapsed is internal for
		// exactly this) because nothing can make the thread pool actually run a stale callback late
		// enough to land after a real disable/enable pair on demand.
		await client.PluginManager!.EnablePluginAsync<PacketPatchProtocol>();
		plugin.OnTimerElapsed(null);
		await client.WaitForProcessingAsync();
		await Assert.That(prompts).IsEqualTo(0);

		await client.DisposeAsync();
	}

	[Test]
	public async Task AStaleTimerCallbackDropsItselfAfterARearm()
	{
		// Timer.Change cannot cancel a callback the runtime has already dispatched to the thread pool
		// -- only reprogram when the timer next fires. Nothing can make the pool actually stall a
		// queued callback on demand to reproduce that race for real, so this drives the mechanism
		// that closes it (PacketPatchProtocol._armDeadline) directly instead: hold a fragment, extend
		// it (a genuine re-arm, moving the deadline later), then invoke the elapsed path immediately
		// -- exactly modelling a callback that was queued under the first arm and is only now getting
		// its turn to run, after the second arm already moved the goalposts.
		var lines = new List<string>();
		var prompts = 0;
		var client = await ClientAsync(() => { prompts++; return ValueTask.CompletedTask; }, lines);
		var plugin = client.PluginManager!.GetPlugin<PacketPatchProtocol>()!;

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("What's your "));
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("name, "));

		// The stale fire: invoked well before the second arm's own deadline, it must drop itself
		// rather than reporting the still-growing fragment as a prompt.
		plugin.OnTimerElapsed(null);
		await Assert.That(prompts).IsEqualTo(0);

		// The rest of the fragment arrives after the simulated stale fire, same as it would have if
		// the real race had played out -- and the genuinely pending, re-armed timer still reports the
		// whole thing once its own hold time actually elapses.
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
}
