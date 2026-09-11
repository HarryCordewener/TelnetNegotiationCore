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

	/// <summary>
	/// An arm placed before a burst does not survive into it: a deadline reached while burst bytes
	/// are still queued reports nothing, rather than a sentinel that lands mid-line.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The deadline is reached by calling <see cref="PacketPatchProtocol.OnTimerElapsed"/> directly, at
	/// a zero hold time so it is never early. That models the pre-fix code, whose arm survived the
	/// burst: its callback passed the staleness guard and enqueued a sentinel behind whatever was
	/// queued, and the loop took the line it was halfway through as a prompt.
	/// </para>
	/// <para>
	/// <b>A zero hold time makes any empty channel mid-line a real prompt</b>, so the channel must never
	/// empty mid-line. The first version fed the burst in two slices and called the deadline between
	/// them, trusting the loop not to have drained the first slice by then. On a two-core runner it
	/// often had: the loop idled at byte 10,001, which is "…description of the roo", the real timer
	/// fired as designed, and "m." arrived as a line of its own. It failed the v3.0.0 release. Four of
	/// six full-suite runs failed pinned to two cores; zero of forty failed in isolation.
	/// </para>
	/// <para>
	/// So the byte loop is parked instead, inside a callback it awaits, while the test queues. The loop
	/// can only reach a gate at a point with no partial line pending, and everything after it is queued
	/// before it resumes, so the channel is empty only after the final line terminator.
	/// </para>
	/// </remarks>
	[Test]
	public async Task ABurstSpanningTheHoldDeadlineNeverMangleAnOrdinaryLine()
	{
		const string roomSuffix = "The Great Hall of Dwarves, a long description of the room.";
		const int burstLines = 100;
		var lines = new List<string>();
		var prompts = 0;

		// One gate per point the loop is parked at: the blank line that opens the session, the prompt
		// the fragment becomes, and the first burst line. The loop awaits each callback, so while one
		// is parked no byte is read and everything queued meanwhile is waiting in the channel.
		var parked = new[] { new TaskCompletionSource(), new TaskCompletionSource(), new TaskCompletionSource() };
		var release = new[] { new TaskCompletionSource(), new TaskCompletionSource(), new TaskCompletionSource() };
		async ValueTask Park(int gate)
		{
			parked[gate].SetResult();
			await release[gate].Task;
		}

		var client = await BuildAndWaitAsync(
			new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit((data, _, _) =>
				{
					var line = Encoding.ASCII.GetString(data);
					lines.Add(line);
					return lines.Count switch
					{
						1 => Park(0),
						2 => Park(2),
						_ => ValueTask.CompletedTask,
					};
				})
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<PacketPatchProtocol>()
				.WithHoldTime(TimeSpan.Zero)
				.OnPrompt(() =>
				{
					prompts++;
					return prompts == 1 ? Park(1) : ValueTask.CompletedTask;
				}));
		var plugin = client.PluginManager!.GetPlugin<PacketPatchProtocol>()!;
		var gateTimeout = TimeSpan.FromSeconds(10);

		string Room(int i) => $"ROOM{i:0000} {roomSuffix}\r\n";

		// A lone newline cannot be split, so the loop reaches the first gate with nothing held.
		await client.InterpretByteArrayAsync("\n"u8.ToArray());
		await parked[0].Task.WaitAsync(gateTimeout);

		// "Prompt> " is wholly queued before the loop resumes, so the channel first empties after its
		// last byte: that idle byte arms, the zero hold fires, and the fragment becomes the prompt.
		await client.InterpretByteArrayAsync(Encoding.ASCII.GetBytes("Prompt> "));
		release[0].SetResult();
		await parked[1].Task.WaitAsync(gateTimeout);

		// The first burst line, whole. The loop processes it with its bytes already queued -- each one
		// a non-idle byte, which is what disarms the fix's arm -- and parks in its own submission.
		await client.InterpretByteArrayAsync(Encoding.ASCII.GetBytes(Room(0)));
		release[1].SetResult();
		await parked[2].Task.WaitAsync(gateTimeout);

		// The rest of the burst, well inside the channel's 10,000-byte bound so no write waits on the
		// parked loop. The deadline is reached with a line half-queued: a sentinel from a surviving
		// arm would land exactly there, between "…the roo" and "m.".
		var rest = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(1, burstLines - 1).Select(Room)));
		var cut = 50 * Room(0).Length - "m.\r\n".Length;
		await client.InterpretByteArrayAsync(rest.AsMemory(0, cut));
		plugin.OnTimerElapsed(null);
		await client.InterpretByteArrayAsync(rest.AsMemory(cut));
		release[2].SetResult();
		await client.WaitForProcessingAsync(maxWaitMs: 10000, additionalDelayMs: 100);

		bool IsWholeLine(string l) =>
			l.StartsWith("ROOM", StringComparison.Ordinal) && l.EndsWith(roomSuffix, StringComparison.Ordinal);

		await Assert.That(lines[0]).IsEqualTo("");
		await Assert.That(lines.Skip(1).Where(l => !IsWholeLine(l)).ToList()).IsEmpty();
		await Assert.That(lines.Count).IsEqualTo(1 + burstLines);
		await Assert.That(prompts).IsEqualTo(1);

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
