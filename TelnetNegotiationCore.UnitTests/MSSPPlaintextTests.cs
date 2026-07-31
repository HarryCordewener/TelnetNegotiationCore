using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// MSSP's second transport: the plaintext <c>MSSP-REQUEST</c> a client types at the login screen and
/// the <c>MSSP-REPLY-START</c> / <c>MSSP-REPLY-END</c> block a server answers with.
/// </summary>
/// <remarks>
/// <para>
/// The framing is SmaugFUSS's, which is the widely copied implementation: <c>src/comm.c</c> matches
/// the request with <c>str_cmp</c> (case-insensitive) in the login handler, and <c>src/mssp.c</c>
/// writes <c>"\r\nMSSP-REPLY-START\r\n"</c>, one <c>"%s\t%s\r\n"</c> per field, then
/// <c>"MSSP-REPLY-END\r\n"</c>.
/// </para>
/// <para>
/// Everything here is driven by a scripted peer rather than a network: no live host is contacted,
/// and none was verified to answer this form.
/// </para>
/// </remarks>
public class MSSPPlaintextTests : BaseTest
{
	/// <summary>Byte-for-character, so a wire dump containing negotiation bytes stays readable.</summary>
	private static readonly Encoding Wire = Encoding.GetEncoding("iso-8859-1");

	private static readonly byte[] WillMssp = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP];

	private static byte[] Text(string text) => Wire.GetBytes(text);

	/// <summary>
	/// A well-formed plaintext reply, framed exactly as <c>send_mssp_data</c> frames it.
	/// </summary>
	private static string Reply(params string[] fields) =>
		"\r\nMSSP-REPLY-START\r\n" + string.Concat(fields.Select(f => f + "\r\n")) + "MSSP-REPLY-END\r\n";

	/// <summary>
	/// One interpreter plus everything it told us about: the reports it delivered, the lines it passed
	/// through to the host application, and every byte it put on the wire.
	/// </summary>
	private sealed class Peer
	{
		public TelnetInterpreter Interpreter { get; set; } = null!;
		public List<MSSPConfig> Received { get; } = [];
		public List<string> Submitted { get; } = [];
		private readonly List<byte> _written = [];
		public (long ReceivedBytes, int MaxMessageSize)? TooLarge { get; set; }
		public int Timeouts;

		public void Write(ReadOnlyMemory<byte> data)
		{
			lock (_written) _written.AddRange(data.ToArray());
		}

		public string Wired
		{
			get { lock (_written) return Wire.GetString(_written.ToArray()); }
		}

		public async Task FeedAsync(string text)
		{
			await Interpreter.InterpretByteArrayAsync(Text(text));
			await Interpreter.WaitForProcessingAsync();
		}
	}

	private static async Task<Peer> PeerAsync(
		TelnetInterpreter.TelnetMode mode,
		Action<PluginConfigurationContext<MSSPProtocol>> configure = null)
	{
		var peer = new Peer();

		var mssp = new TelnetInterpreterBuilder()
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
			.AddPlugin<MSSPProtocol>()
			.OnMSSP(config =>
			{
				lock (peer.Received) peer.Received.Add(config);
				return ValueTask.CompletedTask;
			});

		configure?.Invoke(mssp);

		peer.Interpreter = await ((TelnetInterpreterBuilder)mssp).BuildAsync();
		return peer;
	}

	/// <summary>
	/// A client with the fallback on and both timers wound down to test speed, already told that the
	/// server will do MSSP over the telnet option so that both transports are genuinely in play.
	/// </summary>
	private static async Task<Peer> CrawlerAsync(
		Action<PluginConfigurationContext<MSSPProtocol>> configure = null,
		int requestDelayMs = 50,
		int replyTimeoutMs = 5000)
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client, mssp =>
		{
			mssp.WithPlaintextFallback()
				.WithPlaintextRequestDelay(TimeSpan.FromMilliseconds(requestDelayMs))
				.WithPlaintextReplyTimeout(TimeSpan.FromMilliseconds(replyTimeoutMs));
			configure?.Invoke(mssp);
		});

		return peer;
	}

	private static byte[] Subnegotiation(string name, string value) =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSSP,
		(byte)Trigger.MSSP_VAR, .. Encoding.ASCII.GetBytes(name),
		(byte)Trigger.MSSP_VAL, .. Encoding.ASCII.GetBytes(value),
		(byte)Trigger.IAC, (byte)Trigger.SE
	];

	#region Parsing a reply

	/// <summary>
	/// The whole point: a reply framed the way SmaugFUSS frames it becomes the same
	/// <see cref="MSSPConfig"/> the telnet option would have produced.
	/// </summary>
	[Test]
	public async Task AWellFormedReplyIsParsedIntoAConfig()
	{
		var peer = await CrawlerAsync();

		await peer.FeedAsync(Reply(
			"NAME\tSome MUD",
			"PLAYERS\t4",
			"CODEBASE\tSMAUG 1.8",
			"PORT\t4000",
			"CONTACT\tadmin@example.org"));

		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		await Assert.That(peer.Received.Count).IsEqualTo(1);
		var config = peer.Received[0];
		await Assert.That(config.Name).IsEqualTo("Some MUD");
		await Assert.That(config.Players).IsEqualTo(4);
		await Assert.That(config.Codebase).IsEquivalentTo(new[] { "SMAUG 1.8" });
		await Assert.That(config.Port).IsEqualTo(4000);
		await Assert.That(config.Contact).IsEqualTo("admin@example.org");

		// The reply is protocol, not output: none of it is handed to the host application.
		await Assert.That(peer.Submitted.Any(line => line.Contains("MSSP-REPLY"))).IsFalse();
		await Assert.That(peer.Submitted.Any(line => line.Contains("Some MUD"))).IsFalse();

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// TCP does not respect the framing. A reply cut into arbitrary pieces — mid-marker, mid-name,
	/// mid-value — is the same reply.
	/// </summary>
	[Test]
	public async Task AReplySplitAcrossReadsIsAssembled()
	{
		var peer = await CrawlerAsync();

		var reply = Reply("NAME\tSplit Brain", "PLAYERS\t12", "WEBSITE\thttps://example.org");

		// Deliberately awkward cuts: 7 bytes at a time lands inside the start marker, inside a
		// variable name and inside a value.
		for (var i = 0; i < reply.Length; i += 7)
		{
			await peer.FeedAsync(reply.Substring(i, Math.Min(7, reply.Length - i)));
		}

		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		await Assert.That(peer.Received.Count).IsEqualTo(1);
		await Assert.That(peer.Received[0].Name).IsEqualTo("Split Brain");
		await Assert.That(peer.Received[0].Players).IsEqualTo(12);
		await Assert.That(peer.Received[0].Website).IsEqualTo("https://example.org");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The official vocabulary contains names with embedded spaces, and the field separator is a tab,
	/// so the split is on the first tab and never on whitespace. A value may contain spaces too.
	/// </summary>
	[Test]
	public async Task MultiWordVariableNamesSurviveTheTabSplit()
	{
		var peer = await CrawlerAsync();

		await peer.FeedAsync(Reply(
			"MINIMUM AGE\t18",
			"PAY TO PLAY\t0",
			"XTERM 256 COLORS\t1",
			"CRAWL DELAY\t-1",
			"GENRE\tScience Fiction"));

		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		var config = peer.Received[0];
		await Assert.That(config.Minimum_Age).IsEqualTo("18");
		await Assert.That(config.Pay_To_Play).IsFalse();
		await Assert.That(config.XTerm_256_Colors).IsTrue();
		await Assert.That(config.Crawl_Delay).IsEqualTo(-1);
		await Assert.That(config.Genre).IsEqualTo("Science Fiction");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A variable repeated across lines is an array, exactly as it is when the same variable is
	/// repeated in a subnegotiation.
	/// </summary>
	[Test]
	public async Task ARepeatedVariableIsAnArray()
	{
		var peer = await CrawlerAsync();

		await peer.FeedAsync(Reply(
			"REFERRAL\tone.example.org 4000",
			"REFERRAL\ttwo.example.org 4001",
			"PORT\t23",
			"PORT\t4000"));

		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		var config = peer.Received[0];
		await Assert.That(config.Referral).IsEquivalentTo(new[] { "one.example.org 4000", "two.example.org 4001" });
		await Assert.That(config.Variables["PORT"]).IsEquivalentTo(new[] { "23", "4000" });

		// The specification's rule for a scalar: the last value reported is the default.
		await Assert.That(config.Port).IsEqualTo(4000);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A value can legitimately be empty ("The value can be an empty string"), which on this transport
	/// is a line that ends at the tab.
	/// </summary>
	[Test]
	public async Task AFieldWithAnEmptyValueIsStillReported()
	{
		var peer = await CrawlerAsync();

		await peer.FeedAsync(Reply("NAME\tQuiet", "INTERMUD\t"));

		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		await Assert.That(peer.Received[0].Variables.ContainsKey("INTERMUD")).IsTrue();
		await Assert.That(peer.Received[0].Variables["INTERMUD"]).IsEquivalentTo(new[] { string.Empty });

		await peer.Interpreter.DisposeAsync();
	}

	#endregion

	#region Provenance

	/// <summary>
	/// The two transports can disagree, so which one answered is part of the value. A consumer reading
	/// <c>OnMSSP</c> needs no new wiring to find out, because the discriminator rides on the report
	/// rather than on the callback — it survives being queued, stored or handed on.
	/// </summary>
	[Test]
	public async Task AReportSaysWhichTransportDeliveredIt()
	{
		var plaintext = await CrawlerAsync();
		await plaintext.FeedAsync(Reply("NAME\tPlaintext"));
		await PollUntilAsync(() => plaintext.Received.Count > 0, timeoutMs: 10000);
		await Assert.That(plaintext.Received[0].Source).IsEqualTo(MSSPSource.Plaintext);
		await plaintext.Interpreter.DisposeAsync();

		var option = await PeerAsync(TelnetInterpreter.TelnetMode.Client);
		await InterpretAndWaitAsync(option.Interpreter, WillMssp);
		await InterpretAndWaitAsync(option.Interpreter, Subnegotiation("NAME", "Telnet"));
		await PollUntilAsync(() => option.Received.Count > 0, timeoutMs: 10000);
		await Assert.That(option.Received[0].Source).IsEqualTo(MSSPSource.TelnetOption);
		await option.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A configuration built by hand has no transport, and must not claim one.
	/// </summary>
	[Test]
	public async Task AConfigBuiltByHandHasNoTransport()
	{
		await Assert.That(new MSSPConfig().Source).IsEqualTo(MSSPSource.Unspecified);
	}

	#endregion

	#region Opt-in

	/// <summary>
	/// Unlike <c>IAC DO 70</c>, which a server that does not implement MSSP ignores, this puts real
	/// text on the wire: a server without the plaintext form treats <c>MSSP-REQUEST</c> as input at its
	/// login prompt. So nothing may go out unless the caller asked for it — the timers are wound right
	/// down here, and the wire still has to stay clean.
	/// </summary>
	[Test]
	public async Task NothingIsSentUnlessTheFallbackIsEnabled()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client, mssp => mssp
			.WithPlaintextRequestDelay(TimeSpan.FromMilliseconds(20))
			.WithPlaintextReplyTimeout(TimeSpan.FromMilliseconds(20)));

		await InterpretAndWaitAsync(peer.Interpreter, WillMssp);
		await Task.Delay(500);

		await Assert.That(peer.Wired.Contains("MSSP-REQUEST", StringComparison.OrdinalIgnoreCase)).IsFalse();

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// With the fallback off, a client is also not listening: a line that happens to be the start
	/// marker is ordinary text and reaches the host application untouched.
	/// </summary>
	[Test]
	public async Task WithTheFallbackOffAReplyIsJustText()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync(Reply("NAME\tIgnored"));
		await Task.Delay(200);

		await Assert.That(peer.Received.Count).IsEqualTo(0);
		await Assert.That(peer.Submitted).Contains("MSSP-REPLY-START");
		await Assert.That(peer.Submitted).Contains("MSSP-REPLY-END");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// Once enabled, the request goes out after the configured delay, terminated as telnet terminates
	/// a line (RFC 854 CR LF).
	/// </summary>
	[Test]
	public async Task TheRequestGoesOutAfterTheDelay()
	{
		var peer = await CrawlerAsync(requestDelayMs: 50);

		var sent = await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);
		await Assert.That(sent).IsTrue();
		await Assert.That(peer.Wired).Contains("MSSP-REQUEST\r\n");

		await peer.Interpreter.DisposeAsync();
	}

	#endregion

	#region Answering a request

	/// <summary>
	/// SMAUG matches the request with <c>str_cmp</c>, which is case-insensitive, and Grapevine's
	/// crawler sends it lower case. A server that only answered the upper-case spelling would be
	/// invisible to the most widely deployed client of this transport.
	/// </summary>
	[Test]
	[Arguments("MSSP-REQUEST")]
	[Arguments("mssp-request")]
	[Arguments("Mssp-Request")]
	public async Task AServerAnswersTheRequestWhateverItsCase(string request)
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server, mssp => mssp
			.WithPlaintextFallback()
			.WithMSSPConfig(() => new MSSPConfig { Name = "Some MUD", Players = 4, Uptime = 1234567890 }));

		await peer.FeedAsync(request + "\r\n");
		await PollUntilAsync(() => peer.Wired.Contains("MSSP-REPLY-END"), timeoutMs: 10000);

		var reply = peer.Wired;
		await Assert.That(reply).Contains("\r\nMSSP-REPLY-START\r\n");
		await Assert.That(reply).Contains("NAME\tSome MUD\r\n");
		await Assert.That(reply).Contains("PLAYERS\t4\r\n");
		await Assert.That(reply).Contains("UPTIME\t1234567890\r\n");
		await Assert.That(reply).EndsWith("MSSP-REPLY-END\r\n");

		// The request is consumed: it is not a login name.
		await Assert.That(peer.Submitted.Count).IsEqualTo(0);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A server that has not opted in treats the word as what it is on that server: input. Answering
	/// it would silently make <c>MSSP-REQUEST</c> unusable as a login name on every existing consumer.
	/// </summary>
	[Test]
	public async Task AServerDoesNotAnswerUnlessTheFallbackIsEnabled()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server, mssp => mssp
			.WithMSSPConfig(() => new MSSPConfig { Name = "Some MUD" }));

		await peer.FeedAsync("MSSP-REQUEST\r\n");
		await Task.Delay(200);

		await Assert.That(peer.Wired.Contains("MSSP-REPLY-START")).IsFalse();
		await Assert.That(peer.Submitted).Contains("MSSP-REQUEST");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A round trip through both halves of this file's implementation: the reply one of these servers
	/// writes is a reply one of these clients reads.
	/// </summary>
	[Test]
	public async Task AServersReplyIsReadByAClient()
	{
		var server = await PeerAsync(TelnetInterpreter.TelnetMode.Server, mssp => mssp
			.WithPlaintextFallback()
			.WithMSSPConfig(() => new MSSPConfig
			{
				Name = "Round Trip",
				Minimum_Age = "13",
				Referral = ["one.example.org 4000", "two.example.org 4001"]
			}));

		await server.FeedAsync("MSSP-REQUEST\r\n");
		await PollUntilAsync(() => server.Wired.Contains("MSSP-REPLY-END"), timeoutMs: 10000);
		await server.Interpreter.DisposeAsync();

		var client = await CrawlerAsync();
		await client.FeedAsync(server.Wired);
		await PollUntilAsync(() => client.Received.Count > 0, timeoutMs: 10000);

		var config = client.Received[0];
		await Assert.That(config.Name).IsEqualTo("Round Trip");
		await Assert.That(config.Minimum_Age).IsEqualTo("13");
		await Assert.That(config.Referral).IsEquivalentTo(new[] { "one.example.org 4000", "two.example.org 4001" });

		await client.Interpreter.DisposeAsync();
	}

	#endregion

	#region Bounds

	/// <summary>
	/// A reply is unbounded text terminated only by a marker the server may never send, so the ceiling
	/// matters here more than it does on the telnet path. At the ceiling the report is dropped rather
	/// than truncated, for the reason the subnegotiation path drops its own: a report missing an
	/// unknown number of its variables cannot be told apart from a server that never sent them.
	/// </summary>
	[Test]
	public async Task AReplyBeyondTheCeilingIsDroppedAndReported()
	{
		(long ReceivedBytes, int MaxMessageSize)? tooLarge = null;

		var peer = await CrawlerAsync(mssp => mssp
			.WithMaxMessageSize(1024)
			.OnMSSPMessageTooLarge(overflow =>
			{
				tooLarge = overflow;
				return ValueTask.CompletedTask;
			}));

		// 20 field lines of exactly 100 bytes each: "VARnn" + tab + 94 x's == 2000 bytes against a
		// 1024 byte ceiling. Markers and line endings are framing and are not counted.
		var fields = Enumerable.Range(0, 20).Select(i => $"VAR{i:D2}\t{new string('x', 94)}").ToArray();
		await peer.FeedAsync(Reply(fields));

		var reported = await PollUntilAsync(() => tooLarge != null, timeoutMs: 10000);
		await Assert.That(reported).IsTrue();

		await Assert.That(peer.Received.Count).IsEqualTo(0);
		await Assert.That(tooLarge!.Value.MaxMessageSize).IsEqualTo(1024);
		await Assert.That(tooLarge!.Value.ReceivedBytes).IsEqualTo(2000);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A reply that fits is unaffected by the ceiling being there.
	/// </summary>
	[Test]
	public async Task AReplyUnderTheCeilingIsDelivered()
	{
		var peer = await CrawlerAsync(mssp => mssp.WithMaxMessageSize(1024));

		await peer.FeedAsync(Reply($"NAME\t{new string('x', 100)}"));
		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		await Assert.That(peer.Received[0].Name!.Length).IsEqualTo(100);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A server that ignores the request never terminates the read, and one that starts a reply is not
	/// obliged to finish it. The attempt is therefore bounded in time as well as in bytes: the buffer
	/// is released, the consumer is told, and the half-collected report is never delivered.
	/// </summary>
	[Test]
	public async Task AReplyThatNeverEndsTimesOutAndIsDropped()
	{
		var peer = await CrawlerAsync(requestDelayMs: 20, replyTimeoutMs: 300);
		peer.Timeouts = 0;

		var mssp = peer.Interpreter.PluginManager!.GetPlugin<MSSPProtocol>()!;
		mssp.OnPlaintextMSSPTimeout(() =>
		{
			peer.Timeouts++;
			return ValueTask.CompletedTask;
		});

		// A reply that starts and then stops: no MSSP-REPLY-END, ever.
		await peer.FeedAsync("\r\nMSSP-REPLY-START\r\nNAME\tNever Finished\r\nPLAYERS\t3\r\n");

		var timedOut = await PollUntilAsync(() => peer.Timeouts > 0, timeoutMs: 10000);
		await Assert.That(timedOut).IsTrue();
		await Assert.That(peer.Received.Count).IsEqualTo(0);

		// The buffer went with it: a late end marker cannot resurrect the partial report.
		await peer.FeedAsync("MSSP-REPLY-END\r\n");
		await Task.Delay(200);
		await Assert.That(peer.Received.Count).IsEqualTo(0);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A reply that does arrive cancels the give-up timer rather than leaving it to fire behind it.
	/// </summary>
	[Test]
	public async Task ACompletedReplyStopsTheTimeout()
	{
		var peer = await CrawlerAsync(requestDelayMs: 20, replyTimeoutMs: 250);
		peer.Timeouts = 0;

		var mssp = peer.Interpreter.PluginManager!.GetPlugin<MSSPProtocol>()!;
		mssp.OnPlaintextMSSPTimeout(() =>
		{
			peer.Timeouts++;
			return ValueTask.CompletedTask;
		});

		await peer.FeedAsync(Reply("NAME\tPrompt"));
		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		await Task.Delay(600);
		await Assert.That(peer.Timeouts).IsEqualTo(0);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The timers are durations, so they have to reject values that cannot be one.
	/// </summary>
	[Test]
	public async Task ThePlaintextTimersRejectNonPositiveDurations()
	{
		var mssp = new MSSPProtocol();

		await Assert.That(mssp.PlaintextFallback).IsFalse();
		await Assert.That(mssp.PlaintextRequestDelay).IsEqualTo(MSSPProtocol.DefaultPlaintextRequestDelay);
		await Assert.That(mssp.PlaintextReplyTimeout).IsEqualTo(MSSPProtocol.DefaultPlaintextReplyTimeout);

		await Assert.That(() => mssp.WithPlaintextRequestDelay(TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => mssp.WithPlaintextRequestDelay(TimeSpan.FromSeconds(-1))).Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => mssp.WithPlaintextReplyTimeout(TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => mssp.WithPlaintextReplyTimeout(TimeSpan.FromSeconds(-1))).Throws<ArgumentOutOfRangeException>();
	}

	#endregion

	#region Living beside the telnet option

	/// <summary>
	/// Grapevine's shape, and the right one: the request only goes out if the option has not already
	/// answered. Sending text to a server that has just told us it speaks the real thing would be
	/// noise at its login prompt for no gain.
	/// </summary>
	[Test]
	public async Task TheTelnetOptionWinsWhenItAnswersFirst()
	{
		var peer = await CrawlerAsync(requestDelayMs: 400);

		await InterpretAndWaitAsync(peer.Interpreter, WillMssp);
		await InterpretAndWaitAsync(peer.Interpreter, Subnegotiation("NAME", "Option First"));
		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		await Assert.That(peer.Received[0].Name).IsEqualTo("Option First");
		await Assert.That(peer.Received[0].Source).IsEqualTo(MSSPSource.TelnetOption);

		// Well past the request delay, and the wire is still free of it.
		await Task.Delay(800);
		await Assert.That(peer.Wired.Contains("MSSP-REQUEST", StringComparison.OrdinalIgnoreCase)).IsFalse();
		await Assert.That(peer.Received.Count).IsEqualTo(1);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// With the fallback enabled, a telnet-option report is still an ordinary telnet-option report.
	/// </summary>
	[Test]
	public async Task TheTelnetOptionStillWorksWithTheFallbackEnabled()
	{
		var peer = await CrawlerAsync(requestDelayMs: 30);

		await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);

		await InterpretAndWaitAsync(peer.Interpreter, WillMssp);
		await InterpretAndWaitAsync(peer.Interpreter, Subnegotiation("NAME", "Both In Play"));
		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		await Assert.That(peer.Received[0].Name).IsEqualTo("Both In Play");
		await Assert.That(peer.Received[0].Source).IsEqualTo(MSSPSource.TelnetOption);

		await peer.Interpreter.DisposeAsync();
	}

	#endregion

	#region Text that is not a reply

	/// <summary>
	/// The markers are lines, not substrings. Grapevine detects a reply with
	/// <c>string =~ "MSSP-REPLY-START"</c> over its whole receive buffer, which a MUD can trip by
	/// saying the words — and a MUD is a place where people type things on purpose.
	/// </summary>
	[Test]
	public async Task TextThatMerelyMentionsTheMarkersIsNotAReply()
	{
		var peer = await CrawlerAsync();

		await peer.FeedAsync("The herald cries \"MSSP-REPLY-START\" and everyone laughs.\r\n");
		await peer.FeedAsync("Nothing here is MSSP-REPLY-END, either.\r\n");
		await Task.Delay(200);

		await Assert.That(peer.Received.Count).IsEqualTo(0);
		await Assert.That(peer.Submitted.Count).IsEqualTo(2);
		await Assert.That(peer.Submitted[0]).Contains("The herald cries");

		// And a genuine reply after all that still parses.
		await peer.FeedAsync(Reply("NAME\tStill Fine"));
		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);
		await Assert.That(peer.Received[0].Name).IsEqualTo("Still Fine");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A line inside a reply that carries no tab is not a field. SMAUG never writes one, but a server
	/// that decorates its reply must not turn a separator into a variable.
	/// </summary>
	[Test]
	public async Task ALineWithoutATabInsideAReplyIsNotAVariable()
	{
		var peer = await CrawlerAsync();

		await peer.FeedAsync("\r\nMSSP-REPLY-START\r\n" +
		                     "NAME\tTabless\r\n" +
		                     "---------------\r\n" +
		                     "PLAYERS\t2\r\n" +
		                     "MSSP-REPLY-END\r\n");

		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		var config = peer.Received[0];
		await Assert.That(config.Name).IsEqualTo("Tabless");
		await Assert.That(config.Players).IsEqualTo(2);
		await Assert.That(config.Variables.ContainsKey("---------------")).IsFalse();

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The login banner in front of the reply is ordinary output and stays ordinary output. Only what
	/// lies between the markers belongs to MSSP.
	/// </summary>
	[Test]
	public async Task TextBeforeTheReplyReachesTheHostApplication()
	{
		var peer = await CrawlerAsync();

		await peer.FeedAsync("Welcome to Some MUD!\r\n");
		await peer.FeedAsync(Reply("NAME\tSome MUD"));
		await peer.FeedAsync("By what name do you wish to be known?\r\n");

		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		await Assert.That(peer.Submitted).Contains("Welcome to Some MUD!");
		await Assert.That(peer.Submitted).Contains("By what name do you wish to be known?");
		await Assert.That(peer.Submitted.Count).IsEqualTo(2);

		await peer.Interpreter.DisposeAsync();
	}

	#endregion
}
