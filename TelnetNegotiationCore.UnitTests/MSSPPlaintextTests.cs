using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// MSSP's second transport: the plaintext <c>MSSP-REQUEST</c> a client sends at the login screen and
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
/// The client half is never automatic: the consumer calls
/// <see cref="MSSPPlaintextProtocol.RequestReportAsync"/> when it decides to. No specification gives
/// timing for this exchange, so the library does not invent any.
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
		public MSSPPlaintextProtocol Plaintext => Interpreter.PluginManager!.GetPlugin<MSSPPlaintextProtocol>()!;
		public List<MSSPConfig> Received { get; } = [];
		public List<string> Submitted { get; } = [];
		private readonly List<byte> _written = [];

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

	/// <summary>
	/// Builds an interpreter. <paramref name="withPlaintext"/> controls whether the plaintext plugin is
	/// added at all -- which is the entire opt-in.
	/// </summary>
	private static async Task<Peer> PeerAsync(
		TelnetInterpreter.TelnetMode mode,
		bool withPlaintext = true,
		Action<PluginConfigurationContext<MSSPProtocol>> configureMssp = null,
		Action<PluginConfigurationContext<MSSPPlaintextProtocol>> configurePlaintext = null)
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

		configureMssp?.Invoke(mssp);

		TelnetInterpreterBuilder builder = mssp;

		if (withPlaintext)
		{
			var plaintext = builder.AddPlugin<MSSPPlaintextProtocol>();
			configurePlaintext?.Invoke(plaintext);
			builder = plaintext;
		}

		peer.Interpreter = await builder.BuildAsync();
		return peer;
	}

	private static byte[] Subnegotiation(string name, string value) =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSSP,
		(byte)Trigger.MSSP_VAR, .. Encoding.ASCII.GetBytes(name),
		(byte)Trigger.MSSP_VAL, .. Encoding.ASCII.GetBytes(value),
		(byte)Trigger.IAC, (byte)Trigger.SE
	];

	/// <summary>
	/// Starts a request, waits for it to reach the wire, then plays <paramref name="reply"/> back.
	/// </summary>
	private static async Task<MSSPConfig> ExchangeAsync(Peer peer, string reply)
	{
		var request = peer.Plaintext.RequestReportAsync().AsTask();

		var asked = await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);
		await Assert.That(asked).IsTrue();

		await peer.FeedAsync(reply);
		return await request;
	}

	#region Parsing a reply

	/// <summary>
	/// The whole point: a reply framed the way SmaugFUSS frames it becomes the same
	/// <see cref="MSSPConfig"/> the telnet option would have produced -- returned to the caller that
	/// asked for it, and delivered to <c>OnMSSP</c> as well.
	/// </summary>
	[Test]
	public async Task AWellFormedReplyIsParsedIntoAConfig()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var config = await ExchangeAsync(peer, Reply(
			"NAME\tSome MUD",
			"PLAYERS\t4",
			"CODEBASE\tSMAUG 1.8",
			"PORT\t4000",
			"CONTACT\tadmin@example.org"));

		await Assert.That(config).IsNotNull();
		await Assert.That(config.Name).IsEqualTo("Some MUD");
		await Assert.That(config.Players).IsEqualTo(4);
		await Assert.That(config.Codebase).IsEquivalentTo(new[] { "SMAUG 1.8" });
		await Assert.That(config.Port).IsEqualTo(4000);
		await Assert.That(config.Contact).IsEqualTo("admin@example.org");

		// The callback fires too, so a consumer wired for the telnet option needs no new plumbing.
		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);
		await Assert.That(peer.Received.Count).IsEqualTo(1);
		await Assert.That(peer.Received[0].Name).IsEqualTo("Some MUD");

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
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var reply = Reply("NAME\tSplit Brain", "PLAYERS\t12", "WEBSITE\thttps://example.org");
		var request = peer.Plaintext.RequestReportAsync().AsTask();
		await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);

		// Deliberately awkward cuts: 7 bytes at a time lands inside the start marker, inside a
		// variable name and inside a value.
		for (var i = 0; i < reply.Length; i += 7)
		{
			await peer.FeedAsync(reply.Substring(i, Math.Min(7, reply.Length - i)));
		}

		var config = await request;

		await Assert.That(config).IsNotNull();
		await Assert.That(config.Name).IsEqualTo("Split Brain");
		await Assert.That(config.Players).IsEqualTo(12);
		await Assert.That(config.Website).IsEqualTo("https://example.org");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The official vocabulary contains names with embedded spaces, and the field separator is a tab,
	/// so the split is on the first tab and never on whitespace. A value may contain spaces too.
	/// </summary>
	[Test]
	public async Task MultiWordVariableNamesSurviveTheTabSplit()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var config = await ExchangeAsync(peer, Reply(
			"MINIMUM AGE\t18",
			"PAY TO PLAY\t0",
			"XTERM 256 COLORS\t1",
			"CRAWL DELAY\t-1",
			"GENRE\tScience Fiction"));

		await Assert.That(config).IsNotNull();
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
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var config = await ExchangeAsync(peer, Reply(
			"REFERRAL\tone.example.org 4000",
			"REFERRAL\ttwo.example.org 4001",
			"PORT\t23",
			"PORT\t4000"));

		await Assert.That(config).IsNotNull();
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
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var config = await ExchangeAsync(peer, Reply("NAME\tQuiet", "INTERMUD\t"));

		await Assert.That(config).IsNotNull();
		await Assert.That(config.Variables.ContainsKey("INTERMUD")).IsTrue();
		await Assert.That(config.Variables["INTERMUD"]).IsEquivalentTo(new[] { string.Empty });

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The exchange is not one-shot. A crawler that wants to ask twice may.
	/// </summary>
	[Test]
	public async Task ASecondRequestWorks()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var first = await ExchangeAsync(peer, Reply("NAME\tFirst"));
		await Assert.That(first.Name).IsEqualTo("First");

		var second = await ExchangeAsync(peer, Reply("NAME\tSecond"));
		await Assert.That(second.Name).IsEqualTo("Second");

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
		var plaintext = await PeerAsync(TelnetInterpreter.TelnetMode.Client);
		var config = await ExchangeAsync(plaintext, Reply("NAME\tPlaintext"));
		await Assert.That(config.Source).IsEqualTo(MSSPSource.Plaintext);
		await plaintext.Interpreter.DisposeAsync();

		var option = await PeerAsync(TelnetInterpreter.TelnetMode.Client, withPlaintext: false);
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
	/// Adding the plugin is the whole opt-in, and it grants nothing on its own. Sending
	/// <c>MSSP-REQUEST</c> puts real text at a stranger's login prompt, so it happens when — and only
	/// when — the consumer asks for it.
	/// </summary>
	[Test]
	public async Task NothingIsSentUntilTheConsumerAsks()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await InterpretAndWaitAsync(peer.Interpreter, WillMssp);
		await Task.Delay(500);

		await Assert.That(peer.Wired.Contains("MSSP-REQUEST", StringComparison.OrdinalIgnoreCase)).IsFalse();

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The request goes out when asked for, terminated as telnet terminates a line (RFC 854 CR LF).
	/// </summary>
	[Test]
	public async Task TheRequestGoesOutWhenTheConsumerAsks()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var request = peer.Plaintext.RequestReportAsync().AsTask();

		var asked = await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);
		await Assert.That(asked).IsTrue();
		await Assert.That(peer.Wired).Contains("MSSP-REQUEST\r\n");

		await peer.FeedAsync(Reply("NAME\tAnswered"));
		await request;

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// Without the plugin, a client is not listening: a reply is ordinary text and reaches the host
	/// application untouched.
	/// </summary>
	[Test]
	public async Task WithoutThePluginAReplyIsJustText()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client, withPlaintext: false);

		await peer.FeedAsync(Reply("NAME\tIgnored"));
		await Task.Delay(200);

		await Assert.That(peer.Received.Count).IsEqualTo(0);
		await Assert.That(peer.Submitted).Contains("MSSP-REPLY-START");
		await Assert.That(peer.Submitted).Contains("MSSP-REPLY-END");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The plugin is meaningless without the one whose vocabulary, callback and ceiling it borrows, so
	/// it says so through the dependency mechanism rather than failing later at a null.
	/// </summary>
	[Test]
	public async Task ThePluginRequiresTheMSSPProtocol()
	{
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<MSSPPlaintextProtocol>()
				.BuildAsync();
		});

		await Assert.That(ex!.Message).Contains("depends on");
		await Assert.That(ex.Message).Contains("MSSPProtocol");
		await Assert.That(ex.Message).Contains("not registered");
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
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server, configureMssp: mssp => mssp
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
	/// A server that has not added the plugin treats the word as what it is on that server: input.
	/// Answering it would silently make <c>MSSP-REQUEST</c> unusable as a login name on every existing
	/// consumer.
	/// </summary>
	[Test]
	public async Task AServerDoesNotAnswerUnlessThePluginIsAdded()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server, withPlaintext: false,
			configureMssp: mssp => mssp.WithMSSPConfig(() => new MSSPConfig { Name = "Some MUD" }));

		await peer.FeedAsync("MSSP-REQUEST\r\n");
		await Task.Delay(200);

		await Assert.That(peer.Wired.Contains("MSSP-REPLY-START")).IsFalse();
		await Assert.That(peer.Submitted).Contains("MSSP-REQUEST");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A round trip through both halves: the reply one of these servers writes is a reply one of these
	/// clients reads.
	/// </summary>
	[Test]
	public async Task AServersReplyIsReadByAClient()
	{
		var server = await PeerAsync(TelnetInterpreter.TelnetMode.Server, configureMssp: mssp => mssp
			.WithMSSPConfig(() => new MSSPConfig
			{
				Name = "Round Trip",
				Minimum_Age = "13",
				Referral = ["one.example.org 4000", "two.example.org 4001"]
			}));

		await server.FeedAsync("MSSP-REQUEST\r\n");
		await PollUntilAsync(() => server.Wired.Contains("MSSP-REPLY-END"), timeoutMs: 10000);
		var reply = server.Wired;
		await server.Interpreter.DisposeAsync();

		var client = await PeerAsync(TelnetInterpreter.TelnetMode.Client);
		var config = await ExchangeAsync(client, reply);

		await Assert.That(config).IsNotNull();
		await Assert.That(config.Name).IsEqualTo("Round Trip");
		await Assert.That(config.Minimum_Age).IsEqualTo("13");
		await Assert.That(config.Referral).IsEquivalentTo(new[] { "one.example.org 4000", "two.example.org 4001" });

		await client.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A server does not ask; it answers. Calling the client half on one is a wiring mistake worth
	/// naming rather than a request that quietly never completes.
	/// </summary>
	[Test]
	public async Task AServerCannotRequestAReport()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server);

		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await peer.Plaintext.RequestReportAsync());

		await peer.Interpreter.DisposeAsync();
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

		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client, configureMssp: mssp => mssp
			.WithMaxMessageSize(1024)
			.OnMSSPMessageTooLarge(overflow =>
			{
				tooLarge = overflow;
				return ValueTask.CompletedTask;
			}));

		// 20 field lines of exactly 100 bytes each: "VARnn" + tab + 94 x's == 2000 bytes against a
		// 1024 byte ceiling. Markers and line endings are framing and are not counted.
		var fields = Enumerable.Range(0, 20).Select(i => $"VAR{i:D2}\t{new string('x', 94)}").ToArray();
		var config = await ExchangeAsync(peer, Reply(fields));

		// Dropped, not truncated: the caller gets nothing rather than a partial report.
		await Assert.That(config).IsNull();
		await Assert.That(peer.Received.Count).IsEqualTo(0);

		var reported = await PollUntilAsync(() => tooLarge != null, timeoutMs: 10000);
		await Assert.That(reported).IsTrue();
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
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client,
			configureMssp: mssp => mssp.WithMaxMessageSize(1024));

		var config = await ExchangeAsync(peer, Reply($"NAME\t{new string('x', 100)}"));

		await Assert.That(config).IsNotNull();
		await Assert.That(config.Name!.Length).IsEqualTo(100);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A server that ignores the request never terminates the read, and one that starts a reply is not
	/// obliged to finish it. The wait is therefore bounded: the call returns the no-answer shape rather
	/// than hanging, the collected bytes are released, and the half-collected report is never
	/// delivered.
	/// </summary>
	[Test]
	public async Task AReplyThatNeverEndsTimesOutAndIsDropped()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client,
			configurePlaintext: plaintext => plaintext.WithReplyTimeout(TimeSpan.FromMilliseconds(300)));

		var request = peer.Plaintext.RequestReportAsync().AsTask();
		await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);

		// A reply that starts and then stops: no MSSP-REPLY-END, ever.
		await peer.FeedAsync("\r\nMSSP-REPLY-START\r\nNAME\tNever Finished\r\nPLAYERS\t3\r\n");

		await Assert.That(await request).IsNull();
		await Assert.That(peer.Received.Count).IsEqualTo(0);

		// The buffer went with it: a late end marker cannot resurrect the partial report.
		await peer.FeedAsync("MSSP-REPLY-END\r\n");
		await Task.Delay(200);
		await Assert.That(peer.Received.Count).IsEqualTo(0);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The case the ceiling exists for: a peer that starts a reply, overruns the ceiling, and then
	/// never terminates it. The size cap and the time cap both apply, and the one that ends the wait
	/// must not lose what the other found out — "too large" and "no answer" are different facts about
	/// the peer, and only one of them is true here.
	/// </summary>
	[Test]
	public async Task AnOversizedReplyThatNeverEndsIsStillReportedAsOversized()
	{
		(long ReceivedBytes, int MaxMessageSize)? tooLarge = null;

		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client,
			configureMssp: mssp => mssp
				.WithMaxMessageSize(1024)
				.OnMSSPMessageTooLarge(overflow =>
				{
					tooLarge = overflow;
					return ValueTask.CompletedTask;
				}),
			configurePlaintext: plaintext => plaintext.WithReplyTimeout(TimeSpan.FromMilliseconds(300)));

		var request = peer.Plaintext.RequestReportAsync().AsTask();
		await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);

		// 2000 bytes of fields against a 1024 byte ceiling, and no end marker, ever.
		var fields = Enumerable.Range(0, 20).Select(i => $"VAR{i:D2}\t{new string('x', 94)}");
		await peer.FeedAsync("\r\nMSSP-REPLY-START\r\n" + string.Concat(fields.Select(f => f + "\r\n")));

		await Assert.That(await request).IsNull();

		var reported = await PollUntilAsync(() => tooLarge != null, timeoutMs: 10000);
		await Assert.That(reported).IsTrue();
		await Assert.That(tooLarge!.Value.ReceivedBytes).IsEqualTo(2000);
		await Assert.That(tooLarge!.Value.MaxMessageSize).IsEqualTo(1024);
		await Assert.That(peer.Received.Count).IsEqualTo(0);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A server that ignores the request entirely is the ordinary case, not an error: the wait ends on
	/// its own ceiling and says "no answer". This is what the two hosts probed while this was written
	/// actually did.
	/// </summary>
	[Test]
	public async Task AServerThatNeverAnswersTimesOut()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client,
			configurePlaintext: plaintext => plaintext.WithReplyTimeout(TimeSpan.FromMilliseconds(300)));

		var request = peer.Plaintext.RequestReportAsync().AsTask();
		await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);

		await peer.FeedAsync("Illegal name, try another.\r\n");

		await Assert.That(await request).IsNull();
		await Assert.That(peer.Submitted).Contains("Illegal name, try another.");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The reply ends the wait; the caller is not held to the ceiling once the answer is in.
	/// </summary>
	[Test]
	public async Task ACompletedReplyReturnsWithoutWaitingOutTheTimeout()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client,
			configurePlaintext: plaintext => plaintext.WithReplyTimeout(TimeSpan.FromSeconds(30)));

		var started = DateTime.UtcNow;
		var config = await ExchangeAsync(peer, Reply("NAME\tPrompt"));

		await Assert.That(config.Name).IsEqualTo("Prompt");
		await Assert.That(DateTime.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(20));

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The caller's own token ends the wait too, and says so the way .NET says it.
	/// </summary>
	[Test]
	public async Task TheCallersCancellationEndsTheWait()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		using var cts = new CancellationTokenSource();
		var request = peer.Plaintext.RequestReportAsync(cts.Token).AsTask();
		await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);

		cts.Cancel();

		await Assert.ThrowsAsync<OperationCanceledException>(async () => await request);

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// One exchange at a time: two overlapping requests would race for one reply, and silently giving
	/// both callers the same report — or one of them nothing — is worse than saying so.
	/// </summary>
	[Test]
	public async Task ASecondConcurrentRequestIsRejected()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var first = peer.Plaintext.RequestReportAsync().AsTask();
		await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);

		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await peer.Plaintext.RequestReportAsync());

		await peer.FeedAsync(Reply("NAME\tOnly One"));
		await Assert.That((await first).Name).IsEqualTo("Only One");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// The timeout is a duration, so it must reject a value that cannot be one.
	/// </summary>
	[Test]
	public async Task TheReplyTimeoutRejectsNonPositiveDurations()
	{
		var plaintext = new MSSPPlaintextProtocol();

		await Assert.That(plaintext.ReplyTimeout).IsEqualTo(MSSPPlaintextProtocol.DefaultReplyTimeout);
		await Assert.That(() => plaintext.WithReplyTimeout(TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => plaintext.WithReplyTimeout(TimeSpan.FromSeconds(-1))).Throws<ArgumentOutOfRangeException>();
	}

	#endregion

	#region Living beside the telnet option

	/// <summary>
	/// Adding this plugin changes nothing about option 70. It is a second transport, not a
	/// replacement, and a report that arrives over the option is still an option report.
	/// </summary>
	[Test]
	public async Task TheTelnetOptionIsUnaffected()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await InterpretAndWaitAsync(peer.Interpreter, WillMssp);
		await InterpretAndWaitAsync(peer.Interpreter, Subnegotiation("NAME", "Option"));
		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		await Assert.That(peer.Received[0].Name).IsEqualTo("Option");
		await Assert.That(peer.Received[0].Source).IsEqualTo(MSSPSource.TelnetOption);

		// And the option answering did not put anything of ours on the wire.
		await Assert.That(peer.Wired.Contains("MSSP-REQUEST", StringComparison.OrdinalIgnoreCase)).IsFalse();

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// Both transports on one connection, which is the case a crawler actually meets. They are
	/// independent, and each report says which one it came from.
	/// </summary>
	[Test]
	public async Task BothTransportsCanAnswerOnOneConnection()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await InterpretAndWaitAsync(peer.Interpreter, WillMssp);
		await InterpretAndWaitAsync(peer.Interpreter, Subnegotiation("NAME", "From The Option"));
		await PollUntilAsync(() => peer.Received.Count > 0, timeoutMs: 10000);

		var plaintext = await ExchangeAsync(peer, Reply("NAME\tFrom The Text"));

		await Assert.That(plaintext.Name).IsEqualTo("From The Text");
		await Assert.That(plaintext.Source).IsEqualTo(MSSPSource.Plaintext);

		await PollUntilAsync(() => peer.Received.Count > 1, timeoutMs: 10000);
		await Assert.That(peer.Received.Count).IsEqualTo(2);
		await Assert.That(peer.Received[0].Source).IsEqualTo(MSSPSource.TelnetOption);
		await Assert.That(peer.Received[1].Source).IsEqualTo(MSSPSource.Plaintext);

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
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var request = peer.Plaintext.RequestReportAsync().AsTask();
		await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);

		await peer.FeedAsync("The herald cries \"MSSP-REPLY-START\" and everyone laughs.\r\n");
		await peer.FeedAsync("Nothing here is MSSP-REPLY-END, either.\r\n");
		await Task.Delay(200);

		await Assert.That(peer.Received.Count).IsEqualTo(0);
		await Assert.That(peer.Submitted.Count).IsEqualTo(2);
		await Assert.That(peer.Submitted[0]).Contains("The herald cries");

		// And a genuine reply after all that still parses.
		await peer.FeedAsync(Reply("NAME\tStill Fine"));
		await Assert.That((await request).Name).IsEqualTo("Still Fine");

		await peer.Interpreter.DisposeAsync();
	}

	/// <summary>
	/// A line inside a reply that carries no tab is not a field. SMAUG never writes one, but a server
	/// that decorates its reply must not turn a separator into a variable.
	/// </summary>
	[Test]
	public async Task ALineWithoutATabInsideAReplyIsNotAVariable()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var config = await ExchangeAsync(peer,
			"\r\nMSSP-REPLY-START\r\n" +
			"NAME\tTabless\r\n" +
			"---------------\r\n" +
			"PLAYERS\t2\r\n" +
			"MSSP-REPLY-END\r\n");

		await Assert.That(config).IsNotNull();
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
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		var request = peer.Plaintext.RequestReportAsync().AsTask();
		await PollUntilAsync(() => peer.Wired.Contains("MSSP-REQUEST"), timeoutMs: 10000);

		await peer.FeedAsync("Welcome to Some MUD!\r\n");
		await peer.FeedAsync(Reply("NAME\tSome MUD"));
		await peer.FeedAsync("By what name do you wish to be known?\r\n");

		await request;

		await Assert.That(peer.Submitted).Contains("Welcome to Some MUD!");
		await Assert.That(peer.Submitted).Contains("By what name do you wish to be known?");
		await Assert.That(peer.Submitted.Count).IsEqualTo(2);

		await peer.Interpreter.DisposeAsync();
	}

	#endregion
}
