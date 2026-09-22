using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// MXP's capability exchange: a server asks with <c>&lt;SUPPORT&gt;</c> and <c>&lt;VERSION&gt;</c>, and the
/// client answers with lines the server must read as protocol rather than as something a player typed.
/// </summary>
/// <remarks>
/// Syntax from the <see href="https://www.zuggsoft.com/zmud/mxp.htm">MXP specification</see>: a query is
/// <c>&lt;SUPPORT&gt;</c>, <c>&lt;SUPPORT image frame&gt;</c> or <c>&lt;SUPPORT "color.*"&gt;</c>, and the
/// reply lists each entry as <c>+tag</c>, <c>-tag</c> or <c>+tag.attribute</c>.
/// </remarks>
public class MxpCapabilityTests : BaseTest
{
	private const string Secure = "\u001b[1z";

	private sealed class Peer
	{
		public TelnetInterpreter Interpreter { get; set; } = null!;
		public MXPProtocol Mxp => Interpreter.PluginManager!.GetPlugin<MXPProtocol>()!;
		public List<string> Submitted { get; } = [];
		public List<MxpSupport> Supports { get; } = [];
		public List<MxpVersion> Versions { get; } = [];
		public List<IReadOnlyList<string>> SupportAsked { get; } = [];
		public int VersionAsked;
		private readonly StringBuilder _wired = new();

		public string Wired
		{
			get { lock (_wired) return _wired.ToString(); }
		}

		public void Write(ReadOnlyMemory<byte> data)
		{
			lock (_wired) _wired.Append(Encoding.ASCII.GetString(data.Span));
		}

		public async Task FeedAsync(string text)
		{
			await Interpreter.InterpretByteArrayAsync(Encoding.ASCII.GetBytes(text));
			await Interpreter.WaitForProcessingAsync();
		}
	}

	/// <summary>An interpreter with MXP mode already started, which is where the exchange lives.</summary>
	private static async Task<Peer> PeerAsync(
		TelnetInterpreter.TelnetMode mode = TelnetInterpreter.TelnetMode.Server,
		string[] queryOnStart = null)
	{
		var peer = new Peer();
		var mxp = new TelnetInterpreterBuilder()
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
			.AddPlugin<MXPProtocol>()
			.OnMxpSupports(report =>
			{
				lock (peer.Supports) peer.Supports.Add(report);
				return ValueTask.CompletedTask;
			})
			.OnMxpVersion(version =>
			{
				lock (peer.Versions) peer.Versions.Add(version);
				return ValueTask.CompletedTask;
			})
			.OnMxpSupportRequested(asked =>
			{
				lock (peer.SupportAsked) peer.SupportAsked.Add(asked);
				return ValueTask.CompletedTask;
			})
			.OnMxpVersionRequested(() =>
			{
				System.Threading.Interlocked.Increment(ref peer.VersionAsked);
				return ValueTask.CompletedTask;
			});

		if (queryOnStart is not null) mxp = mxp.QuerySupportOnStart(queryOnStart);

		peer.Interpreter = await mxp.BuildAsync();

		// Settle the option and start MXP mode, which is what the exchange needs.
		byte[] settle = mode == TelnetInterpreter.TelnetMode.Server
			? [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP]
			: [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP];
		await peer.Interpreter.InterpretByteArrayAsync(settle);
		await peer.Interpreter.WaitForProcessingAsync();

		if (mode == TelnetInterpreter.TelnetMode.Client)
		{
			byte[] start = [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MXP, (byte)Trigger.IAC, (byte)Trigger.SE];
			await peer.Interpreter.InterpretByteArrayAsync(start);
			await peer.Interpreter.WaitForProcessingAsync();
		}

		await PollUntilAsync(() => peer.Mxp.IsMxpModeStarted, timeoutMs: 5000);
		return peer;
	}

	// ── Asking ──────────────────────────────────────────────────────────────────

	[Test]
	public async Task AQueryGoesOutOnASecureLine()
	{
		var peer = await PeerAsync();

		await peer.Mxp.RequestSupportAsync();
		await peer.Mxp.RequestSupportAsync("image", "frame");
		await peer.Mxp.RequestVersionAsync();

		await Assert.That(peer.Wired).Contains($"{Secure}<SUPPORT>\r\n")
			.Because("tags are only read on a secure line, so the mode goes with the tag");
		await Assert.That(peer.Wired).Contains($"{Secure}<SUPPORT image frame>\r\n");
		await Assert.That(peer.Wired).Contains($"{Secure}<VERSION>\r\n");
	}

	[Test]
	public async Task AQueryBeforeMxpModeStartsIsRefused()
	{
		var peer = new Peer();
		peer.Interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((_, _, _) => ValueTask.CompletedTask)
			.OnNegotiation(data => { peer.Write(data); return ValueTask.CompletedTask; })
			.AddPlugin<MXPProtocol>()
			.BuildAsync();

		await Assert.That(async () => await peer.Mxp.RequestSupportAsync()).Throws<InvalidOperationException>()
			.Because("a tag sent before the start marker reaches the peer as text");
	}

	[Test]
	public async Task TheQueryCanGoOutAsSoonAsMxpModeStarts()
	{
		var peer = await PeerAsync(queryOnStart: ["image"]);

		await Assert.That(await PollUntilAsync(() => peer.Wired.Contains("<SUPPORT image>"), timeoutMs: 5000)).IsTrue();
	}

	// ── Reading the answers ─────────────────────────────────────────────────────

	[Test]
	public async Task ASupportsReplyIsReadAndConsumed()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync("<SUPPORTS +b +i -image +color.fore>\r\n");

		await Assert.That(peer.Submitted).IsEmpty()
			.Because("the reply is the peer answering a question, not something a player typed");
		await Assert.That(peer.Mxp.Support.Supports("b")).IsTrue();
		await Assert.That(peer.Mxp.Support.Supports("COLOR.FORE")).IsTrue().Because("entries are matched ignoring case");
		await Assert.That(peer.Mxp.Support.Refuses("image")).IsTrue();
		await Assert.That(peer.Mxp.Support.Supports("frame")).IsFalse().Because("nothing is assumed about what was never asked");
		await Assert.That(peer.Supports).Count().IsEqualTo(1);
	}

	[Test]
	public async Task ASecondReplyRevisesTheFirst()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync("<SUPPORTS +image -frame>\r\n");
		await peer.FeedAsync("<SUPPORTS -image>\r\n");

		await Assert.That(peer.Mxp.Support.Refuses("image")).IsTrue();
		await Assert.That(peer.Mxp.Support.Supports("image")).IsFalse();
		await Assert.That(peer.Mxp.Support.Refuses("frame")).IsTrue().Because("the earlier answer still stands");
	}

	[Test]
	public async Task AReplyOnASecureLineIsReadTheSame()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync($"{Secure}<SUPPORTS +image>\r\n");

		await Assert.That(peer.Mxp.Support.Supports("image")).IsTrue();
		await Assert.That(peer.Submitted).IsEmpty();
	}

	[Test]
	public async Task AVersionReplyIsReadAndConsumed()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync("<VERSION MXP=0.4 STYLE=1 CLIENT=zmud VERSION=\"6.07\" REGISTERED=yes>\r\n");

		await Assert.That(peer.Mxp.PeerVersion).IsEqualTo(new MxpVersion("0.4", "1", "zmud", "6.07", true));
		await Assert.That(peer.Submitted).IsEmpty();
		await Assert.That(peer.Versions).Count().IsEqualTo(1);
	}

	[Test]
	[Arguments("<SUPPORTSX +image>")]
	[Arguments("say <SUPPORTS +image>")]
	[Arguments("<SUPPORTS +image")]
	[Arguments("look")]
	public async Task AnythingElseReachesTheApplication(string line)
	{
		var peer = await PeerAsync();

		await peer.FeedAsync(line + "\r\n");

		await Assert.That(peer.Submitted).Contains(line);
	}

	// ── The client half ─────────────────────────────────────────────────────────

	[Test]
	public async Task AClientIsToldWhatWasAsked_AndAnswersOnlyWhenItDecides()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync($"{Secure}<SUPPORT image frame>\r\n");

		await Assert.That(peer.SupportAsked).Count().IsEqualTo(1);
		await Assert.That(peer.SupportAsked[0]).IsEquivalentTo(new[] { "image", "frame" });
		await Assert.That(peer.Submitted).IsEmpty().Because("the query is protocol, not content");
		await Assert.That(peer.Wired).DoesNotContain("<SUPPORTS");

		await peer.Mxp.SendSupportsAsync(["image"], ["frame"]);

		await Assert.That(peer.Wired).Contains($"{Secure}<SUPPORTS +image -frame>\r\n");
	}

	[Test]
	public async Task ABareSupportAsksForEverything()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync("<SUPPORT>\r\n");

		await Assert.That(peer.SupportAsked).Count().IsEqualTo(1);
		await Assert.That(peer.SupportAsked[0]).IsEmpty();
	}

	[Test]
	public async Task AClientAnswersAVersionQueryWhenItDecides()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync("<VERSION>\r\n");

		await Assert.That(peer.VersionAsked).IsEqualTo(1);
		await Assert.That(peer.Submitted).IsEmpty();

		await peer.Mxp.SendVersionAsync(new MxpVersion("0.4", null, "SharpMUSH", "1.0", false));

		await Assert.That(peer.Wired).Contains("<VERSION MXP=0.4 CLIENT=SharpMUSH VERSION=1.0 REGISTERED=no>");
	}

	// ── Parsing, without a connection ───────────────────────────────────────────

	[Test]
	public async Task AnEntryWithoutASignSaysNothing()
	{
		var report = MXPProtocol.ParseSupports("SUPPORTS image +frame");

		await Assert.That(report.Supports("frame")).IsTrue();
		await Assert.That(report.Supports("image")).IsFalse();
		await Assert.That(report.Refuses("image")).IsFalse();
	}

	[Test]
	public async Task AVersionKeepsOnlyTheFieldsItWasGiven()
	{
		var version = MXPProtocol.ParseVersion("VERSION CLIENT=mushclient COLOUR=blue");

		await Assert.That(version).IsEqualTo(new MxpVersion(null, null, "mushclient", null, null));
		await Assert.That(version.ToString()).IsEqualTo("<VERSION CLIENT=mushclient>");
	}
}
