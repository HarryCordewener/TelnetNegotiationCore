using System;
using System.Collections.Generic;
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

	private static async Task<Peer> PeerAsync(TelnetInterpreter.TelnetMode mode = TelnetInterpreter.TelnetMode.Server)
	{
		var peer = new Peer();
		peer.Interpreter = await new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				lock (peer.Submitted) peer.Submitted.Add(encoding.GetString(data));
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
			})
			.BuildAsync();
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
		await Assert.That(peer.Reports.Count).IsEqualTo(1);
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
	/// A terminal answers as soon as it is asked, but over telnet the answer travels with the user's next
	/// line. What the user typed reaches the application without it.
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
	public async Task AClientDoesNotProbe()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await Assert.That(async () => await peer.Queries.ProbeAsync()).Throws<InvalidOperationException>();
	}
}
