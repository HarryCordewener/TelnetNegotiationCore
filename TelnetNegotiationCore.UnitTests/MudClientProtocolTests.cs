#nullable enable
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
/// MCP, the MUD Client Protocol: the out-of-band layer LambdaMOO and its descendants carry over
/// ordinary telnet text, on lines beginning <c>#$#</c>.
/// </summary>
/// <remarks>
/// <para>
/// The handshake is asymmetric and the server opens it. The server sends the one message in the
/// protocol that carries no authentication key:
/// </para>
/// <code>#$#mcp version: "2.1" to: "2.1"</code>
/// <para>
/// and the client answers with the key it has chosen for the rest of the session:
/// </para>
/// <code>#$#mcp authentication-key: "1234" version: "2.1" to: "2.1"</code>
/// <para>
/// Everything here is driven by a scripted peer rather than a network. No live host is contacted.
/// </para>
/// </remarks>
public class MudClientProtocolTests : BaseTest
{
	/// <summary>Byte-for-character, so a wire dump stays readable whatever is in it.</summary>
	private static readonly Encoding Wire = Encoding.GetEncoding("iso-8859-1");

	/// <summary>
	/// One interpreter plus everything it told us about: the lines it passed through to the host
	/// application, and every byte it put on the wire.
	/// </summary>
	private sealed class Peer
	{
		public TelnetInterpreter Interpreter { get; set; } = null!;
		public MudClientProtocol Mcp => Interpreter.PluginManager!.GetPlugin<MudClientProtocol>()!;
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
			await Interpreter.InterpretByteArrayAsync(Wire.GetBytes(text));
			await Interpreter.WaitForProcessingAsync();
		}
	}

	private static async Task<Peer> PeerAsync(TelnetInterpreter.TelnetMode mode, bool answerOffers = true)
	{
		var peer = new Peer();

		var builder = new TelnetInterpreterBuilder()
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
			.AddPlugin<MudClientProtocol>();

		if (!answerOffers) builder = builder.WithoutAnsweringMcpOffers();

		peer.Interpreter = await builder.BuildAsync();

		return peer;
	}

	/// <summary>
	/// A server opens the handshake unprompted, because nothing else can: the client has no way to
	/// know MCP is on offer until the server says so, and the opening message is the only one in the
	/// protocol that carries no authentication key.
	/// </summary>
	[Test]
	public async Task AServerOffersMcpAsSoonAsItIsConnected()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server);

		var offered = await PollUntilAsync(() => peer.Wired.Contains("#$#mcp "), timeoutMs: 10000);

		await Assert.That(offered).IsTrue();
		await Assert.That(peer.Wired).Contains("#$#mcp version: \"2.1\" to: \"2.1\"\r\n");
	}

	/// <summary>
	/// The client answers the offer with the key it has chosen, and that key is what authenticates
	/// every later message in the session -- it is picked by the client, not by the server.
	/// </summary>
	[Test]
	public async Task AClientAnswersTheOfferWithTheKeyItChose()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync("#$#mcp version: \"2.1\" to: \"2.1\"\r\n");

		var answered = await PollUntilAsync(() => peer.Wired.Contains("#$#mcp "), timeoutMs: 10000);
		await Assert.That(answered).IsTrue();

		var key = peer.Mcp.AuthenticationKey;
		await Assert.That(key).IsNotNull();
		await Assert.That(peer.Wired)
			.Contains($"#$#mcp authentication-key: \"{key}\" version: \"2.1\" to: \"2.1\"\r\n");
	}

	/// <summary>
	/// A client that has answered the offer is in an MCP session, and says so the way every other
	/// plugin does.
	/// </summary>
	[Test]
	public async Task AClientIsNegotiatedOnceItHasAnsweredTheOffer()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await Assert.That(peer.Mcp.IsNegotiated).IsFalse();

		await peer.FeedAsync("#$#mcp version: \"2.1\" to: \"2.1\"\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();
	}

	/// <summary>
	/// The server takes the key from the client's answer rather than inventing one: after the
	/// handshake both sides quote the key the client picked.
	/// </summary>
	[Test]
	public async Task AServerTakesTheKeyTheClientChose()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server);

		await peer.FeedAsync("#$#mcp authentication-key: \"1234\" version: \"2.1\" to: \"2.1\"\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();
		await Assert.That(peer.Mcp.AuthenticationKey).IsEqualTo("1234");
	}

	/// <summary>A client with the handshake already done, ready to be sent MCP.</summary>
	private static async Task<Peer> EstablishedClientAsync()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync("#$#mcp version: \"2.1\" to: \"2.1\"\r\n");
		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();

		lock (peer.Submitted) peer.Submitted.Clear();
		return peer;
	}

	/// <summary>
	/// Ordinary output is not MCP's business, and passes through byte for byte.
	/// </summary>
	[Test]
	public async Task OrdinaryOutputIsUntouched()
	{
		var peer = await EstablishedClientAsync();

		await peer.FeedAsync("You see a small mailbox here.\r\n");

		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "You see a small mailbox here." });
	}

	/// <summary>
	/// The other half of the framing rule, and the reason an observer has to be able to rewrite a
	/// line rather than only drop it: a server in an MCP session quotes any line of real output that
	/// would otherwise look like protocol, and the client has to put it back.
	/// </summary>
	[Test]
	public async Task AQuotedLineIsDeliveredWithoutItsQuoting()
	{
		var peer = await EstablishedClientAsync();

		await peer.FeedAsync("#$\"#$#mcp version: \"2.1\" to: \"2.1\"\r\n");

		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "#$#mcp version: \"2.1\" to: \"2.1\"" });
	}

	/// <summary>
	/// Unquoting is a fact about a running session, not about the characters. A server only quotes
	/// once MCP is up, so before that a line beginning <c>#$"</c> is just what it looks like.
	/// </summary>
	[Test]
	public async Task AQuotedLineIsNotUnquotedBeforeMcpIsRunning()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync("#$\"still just text\r\n");

		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "#$\"still just text" });
	}

	/// <summary>
	/// A message carrying the wrong key is not protocol, and is not shown either. Anyone on a MUD can
	/// type <c>#$#</c> at the start of a line; a server in an MCP session is obliged to quote real
	/// output that looks like this, so an unquoted one that fails the key is either an injection
	/// attempt or a broken server, and displaying it is what makes the attempt worth making.
	/// </summary>
	[Test]
	public async Task AMessageWithTheWrongKeyIsNeitherObeyedNorShown()
	{
		var peer = await EstablishedClientAsync();

		await peer.FeedAsync("#$#mcp-negotiate-can not-the-key package: evil min-version: \"1.0\" max-version: \"1.0\"\r\n");
		await peer.FeedAsync("after\r\n");

		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "after" });
	}

	/// <summary>
	/// A package's messages reach the plugin that asked for them, parsed, with the quoting taken off
	/// the values.
	/// </summary>
	[Test]
	public async Task ARegisteredHandlerReceivesTheMessagesItAskedFor()
	{
		var peer = await EstablishedClientAsync();
		var received = new List<McpMessage>();

		peer.Mcp.OnMessage("dns-com-example-test", message =>
		{
			lock (received) received.Add(message);
			return ValueTask.CompletedTask;
		});

		await peer.FeedAsync(
			$"#$#dns-com-example-test {peer.Mcp.AuthenticationKey} name: \"value one\" flag: yes\r\n");

		await Assert.That(await PollUntilAsync(() => received.Count > 0, timeoutMs: 10000)).IsTrue();
		await Assert.That(received[0].Value("name")).IsEqualTo("value one");
		await Assert.That(received[0].Value("flag")).IsEqualTo("yes");
	}

	/// <summary>
	/// A multiline message is one message, delivered once, when its terminator arrives -- not a
	/// handler call per continuation line.
	/// </summary>
	/// <remarks>
	/// The opening message names its continuation keys with a trailing <c>*</c> and carries a
	/// <c>_data-tag</c>; every continuation line quotes that tag, and the line that closes it is
	/// <c>#$#: &lt;tag&gt;</c>. Nothing in any of it reaches the host application.
	/// </remarks>
	[Test]
	public async Task AMultilineMessageArrivesWholeAndOnlyOnce()
	{
		var peer = await EstablishedClientAsync();
		var received = new List<McpMessage>();

		peer.Mcp.OnMessage("dns-com-example-test", message =>
		{
			lock (received) received.Add(message);
			return ValueTask.CompletedTask;
		});

		var key = peer.Mcp.AuthenticationKey;

		await peer.FeedAsync($"#$#dns-com-example-test {key} _data-tag: 9021 name: \"x\" lines*: \"\"\r\n");
		await peer.FeedAsync("#$#* 9021 lines: first\r\n");
		await peer.FeedAsync("#$#* 9021 lines: second\r\n");

		// Nothing is delivered until the terminator: the message is not complete before it.
		await Assert.That(received.Count).IsEqualTo(0);

		await peer.FeedAsync("#$#: 9021\r\n");

		await Assert.That(await PollUntilAsync(() => received.Count > 0, timeoutMs: 10000)).IsTrue();
		await Assert.That(received.Count).IsEqualTo(1);
		await Assert.That(received[0].Value("name")).IsEqualTo("x");
		await Assert.That(received[0].Lines("lines")).IsEquivalentTo(new[] { "first", "second" });
		await Assert.That(peer.Submitted).IsEmpty();
	}

	/// <summary>
	/// A peer offering a wider range than this side speaks is accepted at the version they share.
	/// This is what real servers send: LambdaMOO's own offer is <c>version: "1.0" to: "2.1"</c>.
	/// </summary>
	[Test]
	public async Task AWiderOfferIsAcceptedAtTheVersionBothSidesShare()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync("#$#mcp version: \"1.0\" to: \"2.1\"\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();
		await Assert.That(peer.Wired).Contains("version: \"2.1\" to: \"2.1\"");
	}

	/// <summary>
	/// A range with no version in common leaves the session unstarted, and nothing is sent back: there
	/// is no key to send anything with.
	/// </summary>
	[Test]
	public async Task AnOfferWithNoSharedVersionIsDeclined()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync("#$#mcp version: \"3.0\" to: \"4.0\"\r\n");

		await Assert.That(peer.Mcp.IsNegotiated).IsFalse();
		await Assert.That(peer.Mcp.AuthenticationKey).IsNull();
		await Assert.That(peer.Wired).DoesNotContain("authentication-key");
	}

	/// <summary>
	/// Turning the plugin off ends the session rather than leaving a stale key behind that would go on
	/// authenticating messages nobody is listening for.
	/// </summary>
	[Test]
	public async Task DisablingThePluginEndsTheSession()
	{
		var peer = await EstablishedClientAsync();

		await peer.Mcp.OnDisabledAsync();

		await Assert.That(peer.Mcp.IsNegotiated).IsFalse();
		await Assert.That(peer.Mcp.AuthenticationKey).IsNull();

		// And the stream goes back to being ordinary text, quoting included.
		await peer.FeedAsync("#$\"still just text\r\n");
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "#$\"still just text" });
	}

	/// <summary>
	/// Outside a session, a line that merely begins <c>#$#</c> is ordinary output and is delivered as
	/// such. Nothing is quoting anything before the handshake, so there is no basis for reading it as
	/// protocol -- and a consumer that adds this plugin but never completes a handshake, a crawler
	/// reading connect screens being the obvious one, must not lose text to it.
	/// </summary>
	[Test]
	public async Task ALineThatLooksLikeMcpOutsideASessionIsStillOutput()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync("#$#not a real message\r\n");
		await peer.FeedAsync("#$#dns-com-example-test key-but-no-session\r\n");

		await Assert.That(peer.Submitted).IsEquivalentTo(new[]
		{
			"#$#not a real message",
			"#$#dns-com-example-test key-but-no-session",
		});
	}

	/// <summary>
	/// The other side of the framing rule, which is a server's obligation: while an MCP session is up,
	/// a line of real output that would look like protocol goes out quoted, so the peer puts it back
	/// rather than reading it as a message.
	/// </summary>
	/// <remarks>
	/// The line that carries the quote prefix is quoted too. Without that, output beginning
	/// <c>#$"</c> arrives at a peer that strips the prefix, and the text loses three characters on
	/// the way.
	/// </remarks>
	[Test]
	public async Task AServerQuotesOutputThatWouldLookLikeProtocol()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server);

		await peer.FeedAsync("#$#mcp authentication-key: \"1234\" version: \"2.1\" to: \"2.1\"\r\n");
		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();

		await peer.Mcp.SendOutputAsync(
			"#$#mcp version: 9\r\n"          // line-initial: would be read as a message
			+ "#$\"already quoted\r\n"        // carries the quote prefix: would lose it in transit
			+ "You say, \"#$#hello\"\r\n"     // mid-line: nothing reads this as protocol
			+ "ordinary\r\n");

		await Assert.That(peer.Wired).Contains(
			"#$\"#$#mcp version: 9\r\n"
			+ "#$\"#$\"already quoted\r\n"
			+ "You say, \"#$#hello\"\r\n"
			+ "ordinary\r\n");
	}

	/// <summary>
	/// Outside a session nothing is quoted, because nothing is unquoting it: a peer with no MCP
	/// session would show the prefix to the player as text.
	/// </summary>
	[Test]
	public async Task OutputIsNotQuotedWhenThereIsNoSession()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server);

		await peer.Mcp.SendOutputAsync("#$#looks like protocol\r\n");

		await Assert.That(peer.Wired).Contains("#$#looks like protocol\r\n");
		await Assert.That(peer.Wired).DoesNotContain("#$\"#$#looks like protocol");
	}

	/// <summary>
	/// A multiline message goes out as the specification frames it: an opening message naming its
	/// continuation keys with a trailing <c>*</c> and carrying a data tag, one continuation line per
	/// line of content, and the line that closes the tag.
	/// </summary>
	/// <remarks>
	/// This is the direction <c>dns-org-mud-moo-simpleedit</c> needs -- a server handing a client a
	/// buffer to edit -- and the reason a session layer that could only receive multiline would be
	/// half a framing implementation.
	/// </remarks>
	[Test]
	public async Task AMultilineMessageGoesOutFramedAsTheSpecificationFramesIt()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server);

		await peer.FeedAsync("#$#mcp authentication-key: \"1234\" version: \"2.1\" to: \"2.1\"\r\n");
		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();

		await peer.Mcp.SendMultilineAsync(
			"dns-org-mud-moo-simpleedit-content",
			[("reference", "#98:2"), ("name", "Test:look"), ("type", "moo-code")],
			("content", new[] { "\"This is a test.\";", "return 1;" }));

		var tag = System.Text.RegularExpressions.Regex.Match(peer.Wired, "_data-tag: \"([^\"]+)\"");
		await Assert.That(tag.Success).IsTrue();

		var tagValue = tag.Groups[1].Value;

		await Assert.That(peer.Wired).Contains(
			"#$#dns-org-mud-moo-simpleedit-content 1234 reference: \"#98:2\" name: \"Test:look\" "
			+ $"type: \"moo-code\" content*: \"\" _data-tag: \"{tagValue}\"\r\n"
			+ $"#$#* {tagValue} content: \"This is a test.\";\r\n"
			+ $"#$#* {tagValue} content: return 1;\r\n"
			+ $"#$#: {tagValue}\r\n");
	}

	/// <summary>
	/// Every multiline message gets its own tag, so two in flight cannot be mistaken for each other.
	/// </summary>
	[Test]
	public async Task EachMultilineMessageGetsItsOwnDataTag()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server);

		await peer.FeedAsync("#$#mcp authentication-key: \"1234\" version: \"2.1\" to: \"2.1\"\r\n");
		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();

		await peer.Mcp.SendMultilineAsync("dns-com-example-test", [], ("content", new[] { "one" }));
		await peer.Mcp.SendMultilineAsync("dns-com-example-test", [], ("content", new[] { "two" }));

		var tags = System.Text.RegularExpressions.Regex.Matches(peer.Wired, "_data-tag: \"([^\"]+)\"");

		await Assert.That(tags.Count).IsEqualTo(2);
		await Assert.That(tags[0].Groups[1].Value).IsNotEqualTo(tags[1].Groups[1].Value);
	}

	/// <summary>
	/// A caller that hands over text with line breaks in it gets one continuation line per line, not
	/// one continuation line carrying an embedded newline -- which would end the line early and put
	/// the rest of the content on the wire as ordinary output.
	/// </summary>
	[Test]
	public async Task TextWithLineBreaksBecomesSeparateContinuationLines()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Server);

		await peer.FeedAsync("#$#mcp authentication-key: \"1234\" version: \"2.1\" to: \"2.1\"\r\n");
		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();

		await peer.Mcp.SendMultilineAsync("dns-com-example-test", [], ("content", new[] { "first\r\nsecond\nthird" }));

		var tag = System.Text.RegularExpressions.Regex.Match(peer.Wired, "_data-tag: \"([^\"]+)\"").Groups[1].Value;

		await Assert.That(peer.Wired).Contains(
			$"#$#* {tag} content: first\r\n#$#* {tag} content: second\r\n#$#* {tag} content: third\r\n#$#: {tag}\r\n");
	}

	/// <summary>
	/// The version range is read whether or not the server quoted it. The unquoted spelling is not a
	/// curiosity: of the 57 lines beginning <c>#$#</c> in MUIndex's stored connect screens, 37 are
	/// <c>#$#mcp version: 2.1 to: 2.1</c> and 17 are the quoted form -- so the unquoted one is what
	/// most servers that offer MCP actually send.
	/// </summary>
	[Test]
	public async Task AnUnquotedVersionRangeIsReadTheSameAsAQuotedOne()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client);

		await peer.FeedAsync("#$#mcp version: 2.1 to: 2.1\r\n");

		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();
		await Assert.That(peer.Submitted).IsEmpty();
	}

	/// <summary>
	/// A client can take MCP out of the stream without ever speaking it: the offer is consumed, and
	/// nothing is put on the wire in reply.
	/// </summary>
	/// <remarks>
	/// This is what a crawler wants. It reads connect screens from strangers and has no interest in
	/// an MCP session, but the offer is protocol and does not belong in a screen shown to a reader --
	/// 54 of the 57 lines beginning <c>#$#</c> in MUIndex's stored screens are exactly this offer.
	/// Answering would put text on a stranger's login prompt for a session it will never use, which
	/// is the same objection <c>MSSPPlaintextProtocol</c> makes to sending <c>MSSP-REQUEST</c>
	/// unbidden.
	/// </remarks>
	[Test]
	public async Task AClientThatDoesNotAnswerStillTakesTheOfferOutOfTheStream()
	{
		var peer = await PeerAsync(TelnetInterpreter.TelnetMode.Client, answerOffers: false);

		await peer.FeedAsync("#$#mcp version: 2.1 to: 2.1\r\n");
		await peer.FeedAsync("Welcome to the MOO.\r\n");

		// Consumed: the reader of the connect screen never sees it.
		await Assert.That(peer.Submitted).IsEquivalentTo(new[] { "Welcome to the MOO." });

		// And nothing was said back.
		await Assert.That(peer.Wired).DoesNotContain("authentication-key");
		await Assert.That(peer.Mcp.IsNegotiated).IsFalse();
		await Assert.That(peer.Mcp.AuthenticationKey).IsNull();
	}
}
