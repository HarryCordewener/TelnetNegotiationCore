using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The Pueblo handshake, framed as PennMUSH frames it: the hello on connect, the client's
/// <c>PUEBLOCLIENT</c> line consumed, and the start sequence that moves the client into HTML mode.
/// </summary>
public class PuebloProtocolTests : BaseTest
{
	private sealed class Peer
	{
		public TelnetInterpreter Interpreter { get; set; } = null!;
		public PuebloProtocol Pueblo => Interpreter.PluginManager!.GetPlugin<PuebloProtocol>()!;
		public List<string> Submitted { get; } = [];
		public List<PuebloClient> Enabled { get; } = [];
		public int Offered;
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
			await Interpreter.InterpretByteArrayAsync(Encoding.ASCII.GetBytes(text));
			await Interpreter.WaitForProcessingAsync();
		}
	}

	private static async Task<Peer> PeerAsync(
		TelnetInterpreter.TelnetMode mode = TelnetInterpreter.TelnetMode.Server,
		bool withPueblo = true,
		bool callbackThrows = false)
	{
		var peer = new Peer();
		TelnetInterpreterBuilder builder = new TelnetInterpreterBuilder()
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
			});

		if (withPueblo)
		{
			builder = builder.AddPlugin<PuebloProtocol>()
				.OnPuebloEnabled(client =>
				{
					lock (peer.Enabled) peer.Enabled.Add(client);
					return callbackThrows
						? ValueTask.FromException(new InvalidOperationException("host failure"))
						: ValueTask.CompletedTask;
				})
				.OnPuebloOffered(() =>
				{
					Interlocked.Increment(ref peer.Offered);
					return ValueTask.CompletedTask;
				});
		}

		peer.Interpreter = await builder.BuildAsync();
		return peer;
	}

	[Test]
	public async Task AServerAnnouncesPuebloOnConnect()
	{
		var peer = await PeerAsync();

		await Assert.That(await PollUntilAsync(() => peer.Wired.Contains(PuebloProtocol.Hello), timeoutMs: 5000)).IsTrue();
	}

	[Test]
	public async Task TheHandshake_IsConsumed_AndAnsweredWithTheStartSequence()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync("PUEBLOCLIENT 2.50 md5=\"0123456789abcdef\"\r\n");

		await Assert.That(peer.Wired).Contains(PuebloProtocol.Start)
			.Because("the start sequence is what moves the client out of text mode");
		await Assert.That(peer.Submitted).IsEmpty()
			.Because("the handshake would otherwise reach the application as a command");
		await Assert.That(peer.Pueblo.IsPuebloActive).IsTrue();
		await Assert.That(peer.Enabled).Count().IsEqualTo(1);
		await Assert.That(peer.Enabled[0]).IsEqualTo(new PuebloClient("2.50", "0123456789abcdef"));
	}

	[Test]
	public async Task ARepeatedHandshake_IsAnsweredWithoutTheClear_AndEnablesOnce()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync("PUEBLOCLIENT 2.50\r\n");
		await peer.FeedAsync("PUEBLOCLIENT 2.50\r\n");

		await Assert.That(peer.Wired).Contains(PuebloProtocol.Restart);
		await Assert.That(peer.Enabled).Count().IsEqualTo(1);
		await Assert.That(peer.Submitted).IsEmpty();
	}

	/// <summary>
	/// The callback is the host's code, run inside byte processing. A throw from it leaves the client
	/// in Pueblo mode, the handshake consumed, and the input after it flowing.
	/// </summary>
	[Test]
	public async Task AThrowingCallback_LeavesTheConnectionWorking()
	{
		var peer = await PeerAsync(callbackThrows: true);

		await peer.FeedAsync("PUEBLOCLIENT 2.50\r\nlook\r\n");

		await Assert.That(peer.Pueblo.IsPuebloActive).IsTrue();
		await Assert.That(peer.Submitted).Contains("look");
	}

	[Test]
	[Arguments("PUEBLOCLIENTX 2.50")]
	[Arguments("puebloclient 2.50")]
	[Arguments("say PUEBLOCLIENT 2.50")]
	public async Task OnlyTheExactCommand_IsTheHandshake(string line)
	{
		var peer = await PeerAsync();

		await peer.FeedAsync(line + "\r\n");

		await Assert.That(peer.Submitted).Contains(line)
			.Because("PennMUSH matches \"PUEBLOCLIENT \" with strncmp, so anything else is ordinary input");
		await Assert.That(peer.Pueblo.IsPuebloActive).IsFalse();
	}

	[Test]
	public async Task WithoutThePlugin_NothingIsSentAndNothingIsConsumed()
	{
		var peer = await PeerAsync(withPueblo: false);

		await peer.FeedAsync("PUEBLOCLIENT 2.50\r\n");

		await Assert.That(peer.Wired).DoesNotContain("Pueblo");
		await Assert.That(peer.Submitted).Contains("PUEBLOCLIENT 2.50");
	}

	[Test]
	public async Task AClient_NeverAnnouncesPueblo()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);
		await Task.Delay(100);

		await Assert.That(peer.Wired).DoesNotContain(PuebloProtocol.Hello);
	}

	// ── The client half ─────────────────────────────────────────────────────────

	/// <summary>
	/// A client is told the server offers Pueblo and answers only when the consumer decides to:
	/// <c>PUEBLOCLIENT</c> is real text at a login prompt, where a server without Pueblo reads it as a
	/// character name.
	/// </summary>
	[Test]
	public async Task AClient_IsToldOfTheOffer_AndSendsNothingOnItsOwn()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync(PuebloProtocol.Hello);

		await Assert.That(peer.Pueblo.ServerOffered).IsTrue();
		await Assert.That(peer.Offered).IsEqualTo(1);
		await Assert.That(peer.Submitted).IsEmpty().Because("the hello is protocol, not content");
		await Assert.That(peer.Wired).DoesNotContain("PUEBLOCLIENT");
	}

	[Test]
	public async Task AClient_AnnouncesWhenAsked_AndReadsTheServersStartSequence()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);
		await peer.FeedAsync(PuebloProtocol.Hello);

		await peer.Pueblo.AnnounceAsync("2.50", "0123456789abcdef");

		await Assert.That(peer.Wired).Contains("PUEBLOCLIENT 2.50 md5=\"0123456789abcdef\"\r\n");

		await peer.FeedAsync(PuebloProtocol.Start);

		await Assert.That(peer.Pueblo.IsPuebloActive).IsTrue();
		await Assert.That(peer.Submitted).IsEmpty().Because("the start sequence is protocol, not content");
		await Assert.That(peer.Enabled).Count().IsEqualTo(1);
		await Assert.That(peer.Enabled[0]).IsEqualTo(new PuebloClient("2.50", "0123456789abcdef"));
	}

	[Test]
	public async Task AClient_AcceptsTheShortStartSequence_AndEnablesOnce()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);
		await peer.Pueblo.AnnounceAsync();

		await peer.FeedAsync(PuebloProtocol.Restart);
		await peer.FeedAsync(PuebloProtocol.Start);

		await Assert.That(peer.Pueblo.IsPuebloActive).IsTrue();
		await Assert.That(peer.Enabled).Count().IsEqualTo(1);
	}

	[Test]
	public async Task AServer_RefusesToAnnounce()
	{
		var peer = await PeerAsync();

		await Assert.That(async () => await peer.Pueblo.AnnounceAsync()).Throws<InvalidOperationException>();
	}

	[Test]
	[Arguments("2 50", null)]
	[Arguments("", null)]
	[Arguments("2.50", "has space")]
	[Arguments("2.50", "quote\"inside")]
	public async Task AnAnnouncementCannotCarryWhitespaceOrAQuote(string version, string checksum)
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await Assert.That(async () => await peer.Pueblo.AnnounceAsync(version, checksum)).Throws<ArgumentException>();
	}

	[Test]
	public async Task AServerIgnoresTheHello_AndAClientIgnoresTheHandshake()
	{
		var server = await PeerAsync();
		await server.FeedAsync(PuebloProtocol.Hello);
		await Assert.That(server.Submitted).Contains(PuebloProtocol.Hello.TrimEnd('\r', '\n'));

		var client = await PeerAsync(TelnetInterpreter.TelnetMode.Client);
		await client.FeedAsync("PUEBLOCLIENT 2.50\r\n");
		await Assert.That(client.Submitted).Contains("PUEBLOCLIENT 2.50");
	}

	/// <summary>
	/// PennMUSH parses every PUEBLOCLIENT line before deciding what to answer (<c>src/bsd.c</c>), so a
	/// client correcting its checksum on a resend is taken at its word.
	/// </summary>
	[Test]
	public async Task ARepeatedHandshakeRefreshesWhatTheClientSaid()
	{
		var peer = await PeerAsync();

		await peer.FeedAsync("PUEBLOCLIENT 2.50 md5=\"first\"\r\n");
		await peer.FeedAsync("PUEBLOCLIENT 2.51 md5=\"second\"\r\n");

		await Assert.That(peer.Pueblo.Client).IsEqualTo(new PuebloClient("2.51", "second"));
		await Assert.That(peer.Enabled).Count().IsEqualTo(1).Because("the connection entered Pueblo mode once");
	}

	/// <summary>
	/// PennMUSH re-runs its connect screen, and each pass that takes the non-telnet branch queues the
	/// hello again (<c>src/bsd.c</c>), so a client can see it more than once. The offer is still one offer.
	/// </summary>
	[Test]
	public async Task TheOfferIsReportedOnce()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync(PuebloProtocol.Hello);
		await peer.FeedAsync(PuebloProtocol.Hello);

		await Assert.That(peer.Offered).IsEqualTo(1);
		await Assert.That(peer.Submitted).IsEmpty();
	}

	/// <summary>
	/// A line that merely starts like the hello is the server's own text, not the handshake: PennMUSH's
	/// hello is a whole line.
	/// </summary>
	[Test]
	public async Task ALineThatOnlyBeginsLikeTheHelloIsOrdinaryText()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);
		var line = PuebloProtocol.Hello.TrimEnd('\r', '\n') + " Type 'help' to begin.";

		await peer.FeedAsync(line + "\r\n");

		await Assert.That(peer.Submitted).Contains(line);
		await Assert.That(peer.Pueblo.ServerOffered).IsFalse();
	}

	/// <summary>
	/// A disposed plugin takes no further part. Without that, the handshake ran again on a connection
	/// already in Pueblo mode and the callback fired a second time.
	/// </summary>
	[Test]
	public async Task ADisposedPluginNoLongerAnswersTheHandshake()
	{
		var peer = await PeerAsync();
		await peer.FeedAsync("PUEBLOCLIENT 2.50\r\n");

		await peer.Pueblo.DisposeAsync();
		await peer.FeedAsync("PUEBLOCLIENT 2.50\r\n");

		await Assert.That(peer.Enabled).Count().IsEqualTo(1);
		await Assert.That(peer.Submitted).Contains("PUEBLOCLIENT 2.50")
			.Because("a plugin that is no longer taking part does not consume the line either");
	}

	// ── Parsing ─────────────────────────────────────────────────────────────────

	[Test]
	[Arguments("PUEBLOCLIENT 2.50 md5=\"abc\"", "2.50", "abc")]
	[Arguments("PUEBLOCLIENT md5=\"abc\"", "", "abc")]
	[Arguments("PUEBLOCLIENT 2.01", "2.01", null)]
	// PUEBLO_CHECKSUM_LEN is 40 (hdrs/mushtype.h), so 40 is kept and 41 is not.
	[Arguments("PUEBLOCLIENT 2.50 md5=\"0123456789abcdef0123456789abcdef01234567\"", "2.50", "0123456789abcdef0123456789abcdef01234567")]
	[Arguments("PUEBLOCLIENT 2.50 md5=\"0123456789abcdef0123456789abcdef012345678\"", "2.50", null)]
	// string_match finds a marker at the start of a word, so this one is not a checksum.
	[Arguments("PUEBLOCLIENT 2.50 xmd5=\"abc\"", "2.50", null)]
	[Arguments("PUEBLOCLIENT 2.50 hint=x md5=\"abc\"", "2.50", "abc")]
	public async Task TheClientLine_IsReadAsPennMUSHReadsIt(string line, string version, string checksum)
	{
		await Assert.That(PuebloProtocol.Parse(line)).IsEqualTo(new PuebloClient(version, checksum));
	}
}
