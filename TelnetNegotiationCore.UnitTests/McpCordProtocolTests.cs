#nullable enable
using System;
using System.Collections.Generic;
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
/// <c>mcp-cord</c>: named, typed channels multiplexed over the one MCP session.
/// </summary>
/// <remarks>
/// <para>
/// A cord is not a negotiation. The negotiating happened one layer down, when <c>mcp-negotiate</c>
/// established that both sides speak <c>mcp-cord</c>; opening one is use of a capability already
/// agreed, closer to opening a connection on an agreed port. What it buys is that a consumer can
/// define its own channel without a new plugin in this library.
/// </para>
/// <para>
/// Identifiers carry a role prefix so the two ends cannot collide: the endpoint that initiated MCP
/// -- the server, which sends the offer -- prefixes <c>I</c>, and the responder prefixes <c>R</c>.
/// </para>
/// </remarks>
public class McpCordProtocolTests : BaseTest
{
	private static readonly Encoding Wire = Encoding.GetEncoding("iso-8859-1");

	private sealed class Peer : IAsyncDisposable
	{
		public TelnetInterpreter Interpreter { get; set; } = null!;
		public MudClientProtocol Mcp => Interpreter.PluginManager!.GetPlugin<MudClientProtocol>()!;
		public McpCordProtocol Cords => Interpreter.PluginManager!.GetPlugin<McpCordProtocol>()!;
		private readonly List<byte> _written = [];

		public ValueTask DisposeAsync() => Interpreter.DisposeAsync();

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

	/// <summary>A client with the MCP session already up and cords available.</summary>
	private static async Task<Peer> EstablishedClientAsync(Action<McpCordProtocol>? configure = null)
	{
		var peer = new Peer();

		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				peer.Write(data);
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MudClientProtocol>()
			.AddPlugin<McpNegotiateProtocol>()
			.AddPlugin<McpCordProtocol>();

		configure?.Invoke(builder.Plugin);

		peer.Interpreter = await builder.BuildAsync();

		await peer.FeedAsync("#$#mcp version: \"2.1\" to: \"2.1\"\r\n");
		await Assert.That(await PollUntilAsync(() => peer.Mcp.IsNegotiated, timeoutMs: 10000)).IsTrue();

		return peer;
	}

	/// <summary>
	/// The dependency is declared, so adding cords without the package negotiation that advertises
	/// them is refused at <c>BuildAsync</c> rather than going quiet on the wire.
	/// </summary>
	[Test]
	public async Task CordsWithoutPackageNegotiationAreRefusedAtBuild()
	{
		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<MudClientProtocol>()
			.AddPlugin<McpCordProtocol>();

		await Assert.That(async () => await builder.BuildAsync()).Throws<InvalidOperationException>();
	}

	/// <summary>
	/// Cords are a package like any other, so the side that speaks them says so in its own list.
	/// </summary>
	[Test]
	public async Task CordsAreAdvertisedAsAPackage()
	{
		await using var peer = await EstablishedClientAsync();

		await PollUntilAsync(() => peer.Wired.Contains("mcp-negotiate-end"), timeoutMs: 10000);

		await Assert.That(peer.Wired).Contains(
			"package: \"mcp-cord\" min-version: \"1.0\" max-version: \"1.0\"");
	}

	/// <summary>
	/// Opening a cord names it and types it, and the identifier carries this end's role prefix so the
	/// two ends of a connection cannot choose the same one.
	/// </summary>
	[Test]
	public async Task OpeningACordAnnouncesItsIdentifierAndType()
	{
		await using var peer = await EstablishedClientAsync();

		var cord = await peer.Cords.OpenAsync("dns-com-example-chat");

		// A client is the responder: it answers the server's offer.
		await Assert.That(cord.Id).StartsWith("R");
		await Assert.That(cord.Type).IsEqualTo("dns-com-example-chat");
		await Assert.That(peer.Wired).Contains(
			$"#$#mcp-cord-open {peer.Mcp.AuthenticationKey} _id: \"{cord.Id}\" _type: \"dns-com-example-chat\"\r\n");
	}

	/// <summary>Two cords opened on one session do not share an identifier.</summary>
	[Test]
	public async Task EachCordGetsItsOwnIdentifier()
	{
		await using var peer = await EstablishedClientAsync();

		var first = await peer.Cords.OpenAsync("dns-com-example-chat");
		var second = await peer.Cords.OpenAsync("dns-com-example-chat");

		await Assert.That(first.Id).IsNotEqualTo(second.Id);
	}

	/// <summary>
	/// A peer opening a cord of a type this side declared produces a cord, handed to the consumer that
	/// declared it.
	/// </summary>
	[Test]
	public async Task APeerCanOpenACordOfADeclaredType()
	{
		var opened = new List<McpCord>();

		await using var peer = await EstablishedClientAsync(cords =>
			cords.SupportsCordType("dns-com-example-chat", cord =>
			{
				lock (opened) opened.Add(cord);
				return ValueTask.CompletedTask;
			}));

		await peer.FeedAsync(
			$"#$#mcp-cord-open {peer.Mcp.AuthenticationKey} _id: \"I1\" _type: \"dns-com-example-chat\"\r\n");

		await Assert.That(await PollUntilAsync(() => opened.Count > 0, timeoutMs: 10000)).IsTrue();
		await Assert.That(opened[0].Id).IsEqualTo("I1");
		await Assert.That(opened[0].Type).IsEqualTo("dns-com-example-chat");
	}

	/// <summary>
	/// A cord of a type this side never declared is dropped. "Implementations should verify the cord
	/// type is supported" -- and an unsupported one is an unrecognised message, which MCP drops.
	/// </summary>
	[Test]
	public async Task ACordOfAnUndeclaredTypeIsDropped()
	{
		await using var peer = await EstablishedClientAsync();

		await peer.FeedAsync(
			$"#$#mcp-cord-open {peer.Mcp.AuthenticationKey} _id: \"I1\" _type: \"dns-com-example-unknown\"\r\n");

		await Assert.That(peer.Cords.Open).IsEmpty();
	}

	/// <summary>
	/// A message on a cord names the cord and the cord message, and carries its own arguments beside
	/// them.
	/// </summary>
	[Test]
	public async Task AMessageOnACordNamesTheCordAndTheMessage()
	{
		await using var peer = await EstablishedClientAsync();

		var cord = await peer.Cords.OpenAsync("dns-com-example-chat");
		await cord.SendAsync("say", ("text", "hello there"));

		await Assert.That(peer.Wired).Contains(
			$"#$#mcp-cord {peer.Mcp.AuthenticationKey} _id: \"{cord.Id}\" _message: \"say\" text: \"hello there\"\r\n");
	}

	/// <summary>A message arriving on an open cord reaches that cord's handler, parsed.</summary>
	[Test]
	public async Task AMessageArrivingOnACordReachesIt()
	{
		var received = new List<McpMessage>();

		await using var peer = await EstablishedClientAsync(cords =>
			cords.SupportsCordType("dns-com-example-chat", cord =>
			{
				cord.OnMessage(message =>
				{
					lock (received) received.Add(message);
					return ValueTask.CompletedTask;
				});
				return ValueTask.CompletedTask;
			}));

		var key = peer.Mcp.AuthenticationKey;

		await peer.FeedAsync($"#$#mcp-cord-open {key} _id: \"I1\" _type: \"dns-com-example-chat\"\r\n");
		await peer.FeedAsync($"#$#mcp-cord {key} _id: \"I1\" _message: \"say\" text: \"hello there\"\r\n");

		await Assert.That(await PollUntilAsync(() => received.Count > 0, timeoutMs: 10000)).IsTrue();
		await Assert.That(received[0].Value("_message")).IsEqualTo("say");
		await Assert.That(received[0].Value("text")).IsEqualTo("hello there");
	}

	/// <summary>
	/// A message for a cord that was never opened, or has been closed, is dropped: "treat as an
	/// unrecognized MCP message, silently dropping it."
	/// </summary>
	[Test]
	public async Task AMessageForAnUnknownCordIsDropped()
	{
		await using var peer = await EstablishedClientAsync();

		await peer.FeedAsync(
			$"#$#mcp-cord {peer.Mcp.AuthenticationKey} _id: \"nosuchcord\" _message: \"say\" text: \"hello\"\r\n");
		await peer.FeedAsync("after\r\n");

		// Nothing to assert but the absence of a throw and the absence of a cord.
		await Assert.That(peer.Cords.Open).IsEmpty();
	}

	/// <summary>Closing a cord says so, and the cord stops being open on this side.</summary>
	[Test]
	public async Task ClosingACordAnnouncesItAndForgetsIt()
	{
		await using var peer = await EstablishedClientAsync();

		var cord = await peer.Cords.OpenAsync("dns-com-example-chat");
		await Assert.That(peer.Cords.Open.Count).IsEqualTo(1);

		await cord.CloseAsync();

		await Assert.That(peer.Wired).Contains(
			$"#$#mcp-cord-closed {peer.Mcp.AuthenticationKey} _id: \"{cord.Id}\"\r\n");
		await Assert.That(peer.Cords.Open).IsEmpty();
		await Assert.That(cord.IsOpen).IsFalse();
	}

	/// <summary>
	/// The peer closing a cord tells this side, once. "Race conditions may result in receiving
	/// duplicate closure messages, which should be ignored."
	/// </summary>
	[Test]
	public async Task APeerClosingACordIsReportedOnceEvenIfItSaysSoTwice()
	{
		var closed = 0;

		await using var peer = await EstablishedClientAsync(cords =>
			cords.SupportsCordType("dns-com-example-chat", cord =>
			{
				cord.OnClosed(() =>
				{
					Interlocked.Increment(ref closed);
					return ValueTask.CompletedTask;
				});
				return ValueTask.CompletedTask;
			}));

		var key = peer.Mcp.AuthenticationKey;

		await peer.FeedAsync($"#$#mcp-cord-open {key} _id: \"I1\" _type: \"dns-com-example-chat\"\r\n");
		await peer.FeedAsync($"#$#mcp-cord-closed {key} _id: \"I1\"\r\n");
		await peer.FeedAsync($"#$#mcp-cord-closed {key} _id: \"I1\"\r\n");

		await Assert.That(await PollUntilAsync(() => closed > 0, timeoutMs: 10000)).IsTrue();
		await Assert.That(closed).IsEqualTo(1);
		await Assert.That(peer.Cords.Open).IsEmpty();
	}

	/// <summary>
	/// A closed cord will not carry anything else: sending on it is a mistake by the caller, not a
	/// message quietly dropped on the floor.
	/// </summary>
	[Test]
	public async Task SendingOnAClosedCordIsRefused()
	{
		await using var peer = await EstablishedClientAsync();

		var cord = await peer.Cords.OpenAsync("dns-com-example-chat");
		await cord.CloseAsync();

		await Assert.That(async () => await cord.SendAsync("say", ("text", "too late")))
			.Throws<InvalidOperationException>();
	}

	/// <summary>
	/// Cord arguments may be multiline, which is what makes a cord able to carry anything a package
	/// could.
	/// </summary>
	[Test]
	public async Task ACordMessageCanCarryMultilineContent()
	{
		await using var peer = await EstablishedClientAsync();

		var cord = await peer.Cords.OpenAsync("dns-com-example-editor");
		await cord.SendMultilineAsync("content", [("name", "note")], ("body", new[] { "one", "two" }));

		var key = peer.Mcp.AuthenticationKey;
		var tag = System.Text.RegularExpressions.Regex.Match(peer.Wired, "_data-tag: \"([^\"]+)\"").Groups[1].Value;

		await Assert.That(peer.Wired).Contains(
			$"#$#mcp-cord {key} _id: \"{cord.Id}\" _message: \"content\" name: \"note\" body*: \"\" _data-tag: \"{tag}\"\r\n"
			+ $"#$#* {tag} body: one\r\n"
			+ $"#$#* {tag} body: two\r\n"
			+ $"#$#: {tag}\r\n");
	}

	/// <summary>
	/// Handlers can be installed before the cord is announced, so a peer that answers immediately
	/// cannot beat the caller to it.
	/// </summary>
	/// <remarks>
	/// Wiring them after <c>OpenAsync</c> returns is a race the caller cannot win on a real socket:
	/// <c>mcp-cord-open</c> has already gone out, and a peer is free to send on the cord before the
	/// next statement runs. The configure callback closes it by running before anything is written.
	/// </remarks>
	[Test]
	public async Task HandlersCanBeInstalledBeforeTheCordIsAnnounced()
	{
		await using var peer = await EstablishedClientAsync();

		var announcedDuringConfigure = true;

		var cord = await peer.Cords.OpenAsync("dns-com-example-chat", c =>
		{
			announcedDuringConfigure = peer.Wired.Contains("mcp-cord-open");
			c.OnMessage(_ => ValueTask.CompletedTask);
		});

		await Assert.That(announcedDuringConfigure).IsFalse();
		await Assert.That(peer.Wired).Contains($"_id: \"{cord.Id}\"");
	}

	/// <summary>
	/// Opening a cord without an MCP session leaves no cord behind. The call fails either way; what
	/// must not happen is a cord this side believes is open that the peer was never told about.
	/// </summary>
	[Test]
	public async Task OpeningWithoutASessionLeavesNoCord()
	{
		var peer = new Peer();

		peer.Interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				peer.Write(data);
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MudClientProtocol>()
			.AddPlugin<McpNegotiateProtocol>()
			.AddPlugin<McpCordProtocol>()
			.BuildAsync();

		await using (peer)
		{
			await Assert.That(async () => await peer.Cords.OpenAsync("dns-com-example-chat"))
				.Throws<InvalidOperationException>();

			await Assert.That(peer.Cords.Open).IsEmpty();
		}
	}

	/// <summary>
	/// The ceiling is on cords the peer opened. Cords this side opened are this side's own business,
	/// and must not spend the peer's allowance.
	/// </summary>
	[Test]
	public async Task CordsThisSideOpenedDoNotSpendThePeersAllowance()
	{
		var opened = new List<McpCord>();

		await using var peer = await EstablishedClientAsync(cords =>
			cords.SupportsCordType("dns-com-example-chat", cord =>
			{
				lock (opened) opened.Add(cord);
				return ValueTask.CompletedTask;
			}));

		// Comfortably past the peer ceiling, all of them ours.
		for (var i = 0; i < 70; i++)
		{
			await peer.Cords.OpenAsync("dns-com-example-chat");
		}

		await peer.FeedAsync(
			$"#$#mcp-cord-open {peer.Mcp.AuthenticationKey} _id: \"I1\" _type: \"dns-com-example-chat\"\r\n");

		await Assert.That(await PollUntilAsync(() => opened.Count > 0, timeoutMs: 10000)).IsTrue();
	}
}
