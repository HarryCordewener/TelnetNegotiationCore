using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Asking the terminal behind a connection what it can draw: the questions go out only when asked for,
/// and the answers, which come back inside the user's input, are taken out of it and reported.
/// </summary>
public class TerminalQueryProtocolTests : BaseTest
{
	private const string Esc = "\u001b";

	private sealed class Peer
	{
		public TelnetInterpreter Interpreter { get; set; } = null!;
		public TerminalQueryProtocol Queries => Interpreter.PluginManager!.GetPlugin<TerminalQueryProtocol>()!;
		public List<string> Submitted { get; } = [];
		public List<TerminalReport> Reports { get; } = [];
		public List<byte> Bytes { get; } = [];
		private readonly StringBuilder _wired = new();

		public void Write(ReadOnlyMemory<byte> data)
		{
			lock (_wired) _wired.Append(Encoding.ASCII.GetString(data.Span));
		}

		public string Wired
		{
			get { lock (_wired) return _wired.ToString(); }
		}

		public async Task FeedAsync(string text)
		{
			await Interpreter.InterpretByteArrayAsync(Encoding.UTF8.GetBytes(text));
			await Interpreter.WaitForProcessingAsync();
		}
	}

	private static async Task<Peer> PeerAsync(TelnetInterpreter.TelnetMode mode = TelnetInterpreter.TelnetMode.Server, bool captureBytes = false, int maxBufferSize = TelnetInterpreter.DefaultMaxBufferSize, bool markPrompts = false, Action onSubmit = null)
	{
		var peer = new Peer();
		var builder = new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(logger)
			.WithMaxBufferSize(maxBufferSize)
			.OnSubmit((data, encoding, _) =>
			{
				lock (peer.Submitted)
				{
					peer.Submitted.Add(encoding.GetString(data));
					onSubmit?.Invoke();
				}
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(data =>
			{
				peer.Write(data);
				return ValueTask.CompletedTask;
			})
			.AddPlugin<TerminalQueryProtocol>()
			.OnTerminalReport(report =>
			{
				lock (peer.Reports) peer.Reports.Add(report);
				return ValueTask.CompletedTask;
			});
		if (captureBytes)
			builder.AddPlugin(new EchoProtocol().WithEchoHandler((b, _) =>
			{
				peer.Bytes.Add(b);
				return ValueTask.CompletedTask;
			}));
		if (markPrompts)
		{
			builder.AddPlugin(new SuppressGoAheadProtocol());
			builder.AddPlugin(new EORProtocol());
		}
		peer.Interpreter = await builder.BuildAsync();
		return peer;
	}

	[Test]
	public async Task NothingIsAskedUntilAProbe()
	{
		var peer = await PeerAsync();

		await Assert.That(peer.Wired).DoesNotContain(Esc);
	}

	[Test]
	public async Task AProbeAsksEachQuestionThenForDeviceAttributesLast()
	{
		var peer = await PeerAsync();

		await peer.Queries.ProbeAsync();

		await Assert.That(peer.Wired).EndsWith(
			TerminalQueryProtocol.KittyGraphicsQuery + TerminalQueryProtocol.CellSizeQuery
			+ TerminalQueryProtocol.VersionQuery + TerminalQueryProtocol.DeviceAttributesQuery);
	}

	[Test]
	public async Task AProbeAsksOnlyWhatItIsGiven()
	{
		var peer = await PeerAsync();

		await peer.Queries.ProbeAsync(TerminalQueries.CellSize);

		await Assert.That(peer.Wired).EndsWith(TerminalQueryProtocol.CellSizeQuery + TerminalQueryProtocol.DeviceAttributesQuery);
		await Assert.That(peer.Wired).DoesNotContain("a=q");
	}

	/// <summary>Kitty's answer, then the device attributes: the terminal draws Kitty graphics.</summary>
	[Test]
	public async Task AKittyAnswerBeforeTheDeviceAttributesMeansKittyGraphics()
	{
		var peer = await PeerAsync();
		await peer.Queries.ProbeAsync();

		await peer.FeedAsync($"{Esc}_Gi=31;OK{Esc}\\{Esc}[6;20;10t{Esc}P>|kitty(0.38.1){Esc}\\{Esc}[?62;22c\r\n");

		var report = peer.Queries.Report;
		await Assert.That(report.KittyGraphics).IsTrue();
		await Assert.That(report.CellWidth).IsEqualTo(10);
		await Assert.That(report.CellHeight).IsEqualTo(20);
		await Assert.That(report.Version).IsEqualTo("kitty(0.38.1)");
		await Assert.That(report.Sixel).IsFalse();
		await Assert.That(peer.Reports.Count).IsEqualTo(4);
		await Assert.That(peer.Submitted).IsEmpty();
	}

	/// <summary>Only the device attributes, with 4 among them: no Kitty graphics, but sixel.</summary>
	[Test]
	public async Task DeviceAttributesAloneMeanNoKittyGraphics()
	{
		var peer = await PeerAsync();
		await peer.Queries.ProbeAsync();

		await peer.FeedAsync($"{Esc}[?62;4;22c\r\n");

		await Assert.That(peer.Queries.Report.KittyGraphics).IsFalse();
		await Assert.That(peer.Queries.Report.Sixel).IsTrue();
	}

	/// <summary>
	/// A line-mode client may buffer answers until Enter. What the user typed reaches the application
	/// without any replies, just as when they arrive separately in character mode.
	/// </summary>
	[Test]
	public async Task AnswersAreTakenOutOfTheLineTheUserTyped()
	{
		var peer = await PeerAsync();
		await peer.Queries.ProbeAsync();

		await peer.FeedAsync($"{Esc}_Gi=31;OK{Esc}\\{Esc}[?62c" + "connect Guest\r\n");

		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "connect Guest" });
		await Assert.That(peer.Queries.Report.KittyGraphics).IsTrue();
	}

	[Test]
	public async Task OtherEscapeSequencesAreTheUsers()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync($"say {Esc}[31mred\r\n");

		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { $"say {Esc}[31mred" });
		await Assert.That(peer.Reports).IsEmpty();
	}

	[Test]
	public async Task AnUnfinishedAnswerIsLeftInTheLine()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync($"{Esc}_Gi=31;OK\r\n");

		await Assert.That(peer.Reports).IsEmpty();
		await Assert.That(peer.Submitted.Count).IsEqualTo(1);
	}

	/// <summary>An answer-shaped sequence inside another control string is part of that string, not an answer.</summary>
	[Test]
	public async Task AnAnswerInsideAnotherControlStringIsTheUsers()
	{
		var peer = await PeerAsync();
		var line = $"say {Esc}]0;{Esc}[?62;4c{Esc}\\ hi";

		await peer.FeedAsync(line + "\r\n");

		await Assert.That(peer.Reports).IsEmpty();
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { line });
	}

	/// <summary>A line of unclosed control strings is read once, not again from every escape in it.</summary>
	[Test]
	public async Task UnclosedControlStringsAreKeptWhole()
	{
		var peer = await PeerAsync();
		var line = string.Concat(System.Linq.Enumerable.Repeat($"{Esc}P>|", 50_000));

		await peer.FeedAsync(line + "\r\n");

		await Assert.That(peer.Reports).IsEmpty();
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { line });
	}

	/// <summary>A version string a terminal ends with BEL instead of ST is read the same way.</summary>
	[Test]
	public async Task AVersionEndedWithBellIsRead()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync($"{Esc}P>|WezTerm 20240203\u0007\r\n");

		await Assert.That(peer.Queries.Report.Version).IsEqualTo("WezTerm 20240203");
	}

	/// <summary>Without a Kitty question outstanding, device attributes say nothing about Kitty.</summary>
	[Test]
	public async Task DeviceAttributesWithoutAKittyQuestionLeaveKittyUnknown()
	{
		var peer = await PeerAsync();
		await peer.Queries.ProbeAsync(TerminalQueries.CellSize);

		await peer.FeedAsync($"{Esc}[?1;2c\r\n");

		await Assert.That(peer.Queries.Report.KittyGraphics).IsNull();
		await Assert.That(peer.Queries.Report.DeviceAttributes).IsEquivalentTo(new[] { 1, 2 });
	}

	[Test]
	public async Task DeviceAttributesArriveBeforeAnyNewlineAndNeverReachConsumerCallbacks()
	{
		var peer = await PeerAsync(captureBytes: true);
		await using var interpreter = peer.Interpreter;
		await peer.Queries.ProbeAsync(TerminalQueries.None);

		await peer.FeedAsync($"{Esc}[?1;2c");

		await Assert.That(peer.Queries.Report.DeviceAttributes).IsEquivalentTo(new[] { 1, 2 });
		await Assert.That(peer.Reports.Count).IsEqualTo(1);
		await Assert.That(peer.Submitted).IsEmpty();
		await Assert.That(peer.Bytes).IsEmpty();
		await peer.FeedAsync("\r\n\r\n");
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "" });
		await Assert.That(peer.Reports.Count).IsEqualTo(1);
	}

	[Test]
	public async Task EveryAnswerSurvivesEveryTransportSplitAndInterleavedTelnetNegotiation()
	{
		var answers = new[] { $"{Esc}[?1;2c", $"{Esc}[6;20;10t", $"{Esc}P>|termé{Esc}\\", $"{Esc}_Gi=31;OK{Esc}\\" };
		foreach (var answer in answers)
		for (var split = 1; split < Encoding.UTF8.GetByteCount(answer); split++)
		{
			var peer = await PeerAsync(captureBytes: true);
			await using var interpreter = peer.Interpreter;
			var bytes = Encoding.UTF8.GetBytes(answer);
			await interpreter.InterpretByteArrayAsync(bytes.AsMemory(0, split));
			await interpreter.WaitForProcessingAsync();
			await Assert.That(peer.Reports).IsEmpty();
			await interpreter.InterpretByteArrayAsync(new byte[] { 255, 241 }); // IAC NOP is not application data.
			await interpreter.InterpretByteArrayAsync(bytes.AsMemory(split));
			await interpreter.WaitForProcessingAsync();
			await Assert.That(peer.Reports.Count).IsEqualTo(1);
			await Assert.That(peer.Bytes).IsEmpty();
			if (answer.Contains("termé")) await Assert.That(peer.Queries.Report.Version).IsEqualTo("termé");
		}
	}

	[Test]
	public async Task SurroundingTextAndUnknownEscapesReachBothCallbacksOnceInOrder()
	{
		var peer = await PeerAsync(captureBytes: true);
		await using var interpreter = peer.Interpreter;
		var before = $"say café ؛ {Esc}[31m";
		var after = $" hi {Esc}[0m";
		await peer.FeedAsync(before + $"{Esc}[?1;2c" + after);
		await Assert.That(Encoding.UTF8.GetString(peer.Bytes.ToArray())).IsEqualTo(before + after);
		await peer.FeedAsync("\r\n");
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { before + after });
		await Assert.That(peer.Reports.Count).IsEqualTo(1);
	}

	[Test]
	public async Task IncompleteAndCancelledRepliesArePreservedAndTheNextReplyStillWorks()
	{
		foreach (var cancel in new[] { "\u0018", "\u001a" })
		{
			var peer = await PeerAsync(captureBytes: true);
			await using var interpreter = peer.Interpreter;
			var text = $"{Esc}[?1;" + cancel + $"x{Esc}_Gunfinished";
			await peer.FeedAsync(text + "\r\n");
			await Assert.That(peer.Reports).IsEmpty();
			await Assert.That(peer.Submitted).IsEquivalentTo(new[] { text });
			await Assert.That(Encoding.UTF8.GetString(peer.Bytes.ToArray())).IsEqualTo(text);
			await peer.FeedAsync($"{Esc}[?1;2c");
			await Assert.That(peer.Reports.Count).IsEqualTo(1);
		}
	}

	[Test]
	public async Task AnOversizedStringPassesThroughWithoutExtractingNestedReplies()
	{
		var peer = await PeerAsync(captureBytes: true);
		await using var interpreter = peer.Interpreter;
		var text = $"{Esc}P>|" + new string('x', 70_000) + $"{Esc}[?1;2c{Esc}\\";
		await peer.FeedAsync(text + "\r\n");
		await Assert.That(peer.Reports).IsEmpty();
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { text });
		await Assert.That(Encoding.UTF8.GetString(peer.Bytes.ToArray())).IsEqualTo(text);
		await peer.FeedAsync($"{Esc}[?1;2c");
		await Assert.That(peer.Reports.Count).IsEqualTo(1);
	}

	[Test]
	public async Task AsyncReportCallbacksFinishBeforeFollowingInput()
	{
		var finished = false;
		var submittedBeforeCallbackFinished = false;
		var peer = await PeerAsync(onSubmit: () => submittedBeforeCallbackFinished |= !finished);
		await using var interpreter = peer.Interpreter;
		peer.Queries.OnTerminalReport(async _ =>
		{
			await Task.Yield();
			finished = true;
		});
		await peer.FeedAsync($"{Esc}[?1;2ccommand\r\n");
		await Assert.That(finished).IsTrue();
		await Assert.That(submittedBeforeCallbackFinished).IsFalse();
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "command" });
	}

	[Test]
	public async Task PendingReportAwaitsOutsideSynchronousWriteBeforeFollowingBytes()
	{
		var peer = await PeerAsync(captureBytes: true);
		await using var interpreter = peer.Interpreter;
		var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		peer.Queries.OnTerminalReport(async _ =>
		{
			// Capture the invocation stack before yielding: awaiting inside Write would block its
			// synchronous processing worker until the report callback finishes.
			var insideWrite = new StackTrace().GetFrames().Any(frame =>
				frame.GetMethod()?.DeclaringType?.Name == "GeneratedContext" && frame.GetMethod()?.Name == "Write");
			entered.TrySetResult(insideWrite);
			await release.Task;
		});

		try
		{
			await interpreter.InterpretByteArrayAsync(Encoding.UTF8.GetBytes($"{Esc}[?1;2ccommand\r\n"));
			var insideWrite = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await Assert.That(insideWrite).IsFalse();
			await Assert.That(peer.Bytes).IsEmpty();
			await Assert.That(peer.Submitted).IsEmpty();
		}
		finally
		{
			release.TrySetResult(true);
		}
		await interpreter.WaitForProcessingAsync();
		await Assert.That(Encoding.UTF8.GetString(peer.Bytes.ToArray())).IsEqualTo("command");
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "command" });
	}

	[Test]
	public async Task AdjacentReportsFinishInOrderBeforeSubmission()
	{
		var events = new List<string>();
		var peer = await PeerAsync(onSubmit: () => events.Add("submit"));
		await using var interpreter = peer.Interpreter;
		peer.Queries.OnTerminalReport(async report =>
		{
			var family = report.DeviceAttributes[0];
			events.Add("start" + family);
			await Task.Yield();
			events.Add("end" + family);
		});

		await peer.FeedAsync($"{Esc}[?1c{Esc}[?62ccommand\r\n");

		await Assert.That(string.Join(",", events)).IsEqualTo("start1,end1,start62,end62,submit");
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "command" });
	}

	[Test]
	public async Task AThrowingReportCallbackDoesNotStrandDeferredInput()
	{
		var peer = await PeerAsync(captureBytes: true);
		await using var interpreter = peer.Interpreter;
		peer.Queries.OnTerminalReport(_ => throw new InvalidOperationException("Callback failure"));

		await peer.FeedAsync($"{Esc}[?1;2cfirst\r\n{Esc}[?1;2csecond\r\n");

		await Assert.That(Encoding.UTF8.GetString(peer.Bytes.ToArray())).IsEqualTo("firstsecond");
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "first", "second" });
	}

	[Test]
	public async Task DroppedAndConsumedLinesDoNotSuppressTheNextBlankLine()
	{
		var peer = await PeerAsync(maxBufferSize: 16);
		await using var interpreter = peer.Interpreter;
		interpreter.RegisterInputLineObserver((line, _) =>
			new ValueTask<byte[]>(Encoding.UTF8.GetString(line) == "consume" ? null : line));
		await peer.FeedAsync($"{Esc}[?1;2cconsume\r\n\r\n");
		await peer.FeedAsync($"{Esc}[?1;2c" + new string('x', 20) + "\r\n\r\n");
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "", "" });
	}

	[Test]
	public async Task DisablingReleasesPendingInputAndDoesNotConsumeBlankLines()
	{
		var peer = await PeerAsync(captureBytes: true);
		await using var interpreter = peer.Interpreter;
		await peer.FeedAsync($"{Esc}[?1;");
		await peer.Queries.OnDisabledAsync();
		await peer.FeedAsync("2c\r\n");
		await Assert.That(peer.Reports).IsEmpty();
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { $"{Esc}[?1;2c" });
		await peer.Queries.OnEnabledAsync();
		await peer.FeedAsync($"{Esc}[?1;2c");
		await peer.Queries.OnDisabledAsync();
		await peer.FeedAsync("\r\n");
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { $"{Esc}[?1;2c", "" });
		await peer.Queries.OnEnabledAsync();
		await peer.FeedAsync($"{Esc}[?1;2c");
		await Assert.That(peer.Reports.Count).IsEqualTo(2);
	}

	[Test]
	public async Task PromptBoundariesFlushPendingInputAndResetReplyOnlyLines()
	{
		foreach (var marker in new byte[] { 249, 239 }) // GA and EOR.
		{
			var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client, captureBytes: true, markPrompts: true);
			await using var interpreter = peer.Interpreter;
			if (marker == 239)
				await interpreter.InterpretByteArrayAsync(new byte[] { 255, 251, 25 }); // WILL EOR.
			await peer.FeedAsync($"{Esc}[?1;2c");
			await interpreter.InterpretByteArrayAsync(new byte[] { 255, marker });
			await interpreter.WaitForProcessingAsync();
			await Assert.That(interpreter.HasSeenMarkedPrompt).IsTrue();
			await peer.FeedAsync("\r\n");
			await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "" });
			var unfinished = $"{Esc}[?1;";
			await peer.FeedAsync(unfinished);
			await interpreter.InterpretByteArrayAsync(new byte[] { 255, marker });
			await interpreter.WaitForProcessingAsync();
			await Assert.That(Encoding.UTF8.GetString(interpreter.LastPromptBytes.Span)).IsEqualTo(unfinished);
			await peer.FeedAsync("2c\r\n");
			await Assert.That(peer.Reports.Count).IsEqualTo(1);
			await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "", "2c" });
		}
	}

	[Test]
	public async Task AClientDoesNotProbe()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await Assert.That(async () => await peer.Queries.ProbeAsync()).Throws<InvalidOperationException>();
	}
}

