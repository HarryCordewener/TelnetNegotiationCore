#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Both plugins, both ends, nothing scripted: a server interpreter and a client interpreter wired to
/// each other, left to negotiate MCP between themselves.
/// </summary>
/// <remarks>
/// The other MCP tests script one side, which proves each half against what the specification says
/// the other half sends. This proves the halves against each other -- an implementation can satisfy a
/// scripted peer at both ends and still not agree with itself.
/// </remarks>
public class McpRoundTripTests : BaseTest
{
	/// <summary>
	/// Carries bytes between the two interpreters until neither has anything more to say.
	/// </summary>
	private sealed class Wire : IAsyncDisposable
	{
		private readonly List<byte[]> _toClient = [];
		private readonly List<byte[]> _toServer = [];

		public TelnetInterpreter Client { get; set; } = null!;
		public TelnetInterpreter Server { get; set; } = null!;

		public ValueTask FromServer(ReadOnlyMemory<byte> data)
		{
			lock (_toClient) _toClient.Add(data.ToArray());
			return ValueTask.CompletedTask;
		}

		public ValueTask FromClient(ReadOnlyMemory<byte> data)
		{
			lock (_toServer) _toServer.Add(data.ToArray());
			return ValueTask.CompletedTask;
		}

		/// <summary>Ends both interpreters' byte-processing tasks with the test that started them.</summary>
		public async ValueTask DisposeAsync()
		{
			if (Client is not null) await Client.DisposeAsync();
			if (Server is not null) await Server.DisposeAsync();
		}

		/// <summary>Runs both sides until the wire falls quiet, or until it plainly never will.</summary>
		public async Task SettleAsync()
		{
			for (var round = 0; round < 20; round++)
			{
				var delivered = await DrainAsync(_toClient, Client) | await DrainAsync(_toServer, Server);

				if (!delivered) return;
			}

			throw new InvalidOperationException("The two sides never stopped talking to each other.");
		}

		private static async Task<bool> DrainAsync(List<byte[]> queue, TelnetInterpreter into)
		{
			byte[][] pending;
			lock (queue)
			{
				pending = [.. queue];
				queue.Clear();
			}

			foreach (var data in pending)
			{
				await into.InterpretByteArrayAsync(data);
				await into.WaitForProcessingAsync();
			}

			return pending.Length > 0;
		}
	}

	/// <summary>
	/// The whole exchange: the server offers, the client answers with a key, both sides send their
	/// package lists, and both end up agreeing the same versions of the same packages.
	/// </summary>
	[Test]
	public async Task TwoInterpretersNegotiateMcpAndItsPackagesBetweenThemselves()
	{
		await using var wire = new Wire();

		wire.Server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(wire.FromServer)
			.AddPlugin<MudClientProtocol>()
			.SupportsMcpPackage("dns-com-example-editor", new McpVersion(1, 0), new McpVersion(2, 0))
			.SupportsMcpPackage("dns-com-example-server-only", new McpVersion(1, 0), new McpVersion(1, 0))
			.BuildAsync();

		wire.Client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(wire.FromClient)
			.AddPlugin<MudClientProtocol>()
			.SupportsMcpPackage("dns-com-example-editor", new McpVersion(1, 5), new McpVersion(3, 0))
			.BuildAsync();

		await wire.SettleAsync();

		var serverMcp = wire.Server.PluginManager!.GetPlugin<MudClientProtocol>()!;
		var clientMcp = wire.Client.PluginManager!.GetPlugin<MudClientProtocol>()!;

		// One session, one key, chosen by the client and adopted by the server.
		await Assert.That(clientMcp.IsNegotiated).IsTrue();
		await Assert.That(serverMcp.IsNegotiated).IsTrue();
		await Assert.That(serverMcp.AuthenticationKey).IsEqualTo(clientMcp.AuthenticationKey);

		var serverPackages = wire.Server.PluginManager!.GetPlugin<MudClientProtocol>()!;
		var clientPackages = wire.Client.PluginManager!.GetPlugin<MudClientProtocol>()!;

		await Assert.That(serverPackages.IsComplete).IsTrue();
		await Assert.That(clientPackages.IsComplete).IsTrue();

		// 1.5-to-3.0 against 1.0-to-2.0 shares 1.5 to 2.0, and the highest of that is 2.0. Both sides
		// work it out independently and have to reach the same answer.
		await Assert.That(clientPackages.Agreed["dns-com-example-editor"]).IsEqualTo(new McpVersion(2, 0));
		await Assert.That(serverPackages.Agreed["dns-com-example-editor"]).IsEqualTo(new McpVersion(2, 0));

		// mcp-negotiate is a package like any other, and both sides said so.
		await Assert.That(clientPackages.Agreed[MudClientProtocol.NegotiatePackage]).IsEqualTo(new McpVersion(2, 0));

		// A package only one side speaks is agreed by neither.
		await Assert.That(clientPackages.Agreed.ContainsKey("dns-com-example-server-only")).IsFalse();
		await Assert.That(serverPackages.Agreed.ContainsKey("dns-com-example-server-only")).IsFalse();
	}

	/// <summary>
	/// A multiline message sent by one implementation is reassembled by the other: the framing this
	/// side writes is the framing the other side reads.
	/// </summary>
	/// <remarks>
	/// This is the exchange <c>dns-org-mud-moo-simpleedit</c> is built on, and the one a session layer
	/// that could only receive multiline could not carry. The content is chosen to be awkward on
	/// purpose -- a quote, a colon, a line that would look like protocol on its own, and an empty line
	/// -- because continuation text runs verbatim to the end of the line and none of it is escaped.
	/// </remarks>
	[Test]
	public async Task AMultilineMessageSurvivesTheTripBetweenTwoImplementations()
	{
		await using var wire = new Wire();
		var received = new List<McpMessage>();

		wire.Server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(wire.FromServer)
			.AddPlugin<MudClientProtocol>()
			.BuildAsync();

		wire.Client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(wire.FromClient)
			.AddPlugin<MudClientProtocol>()
			.OnMcpMessage("dns-org-mud-moo-simpleedit-content", message =>
			{
				lock (received) received.Add(message);
				return ValueTask.CompletedTask;
			})
			.BuildAsync();

		await wire.SettleAsync();

		var serverMcp = wire.Server.PluginManager!.GetPlugin<MudClientProtocol>()!;
		await Assert.That(serverMcp.IsNegotiated).IsTrue();

		string[] content =
		[
			"\"This is a test.\";",
			"#$#not a message, just code",
			"",
			"return \"done: yes\";",
		];

		await serverMcp.SendMultilineAsync(
			"dns-org-mud-moo-simpleedit-content",
			[("reference", "#98:2"), ("name", "Test:look"), ("type", "moo-code")],
			("content", content));

		await wire.SettleAsync();

		await Assert.That(received.Count).IsEqualTo(1);
		await Assert.That(received[0].Value("reference")).IsEqualTo("#98:2");
		await Assert.That(received[0].Value("name")).IsEqualTo("Test:look");
		await Assert.That(received[0].Lines("content")).IsEquivalentTo(content);
	}

	/// <summary>
	/// A cord opened by one implementation is accepted by the other, carries a message each way, and
	/// closes -- with neither side scripted.
	/// </summary>
	/// <remarks>
	/// This is the whole point of cords: the two sides here exchange a message this library has never
	/// heard of, on a channel type it has never heard of, without a plugin for either.
	/// </remarks>
	[Test]
	public async Task ACordCarriesMessagesBetweenTwoImplementations()
	{
		await using var wire = new Wire();
		var serverSaw = new List<McpMessage>();
		var clientSaw = new List<McpMessage>();
		McpCord? serverSide = null;

		wire.Server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(wire.FromServer)
			.AddPlugin<MudClientProtocol>()
			.AddPlugin<McpCordProtocol>()
			.SupportsCordType("dns-com-example-chat", cord =>
			{
				serverSide = cord.OnMessage(message =>
				{
					lock (serverSaw) serverSaw.Add(message);
					return ValueTask.CompletedTask;
				});
				return ValueTask.CompletedTask;
			})
			.BuildAsync();

		wire.Client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(wire.FromClient)
			.AddPlugin<MudClientProtocol>()
			.AddPlugin<McpCordProtocol>()
			.BuildAsync();

		await wire.SettleAsync();

		var clientCords = wire.Client.PluginManager!.GetPlugin<McpCordProtocol>()!;
		var clientNegotiate = wire.Client.PluginManager!.GetPlugin<MudClientProtocol>()!;

		// Both sides agreed on cords before either used one.
		await Assert.That(clientNegotiate.Agreed[McpCordProtocol.PackageName]).IsEqualTo(new McpVersion(1, 0));

		var cord = await clientCords.OpenAsync("dns-com-example-chat");
		cord.OnMessage(message =>
		{
			lock (clientSaw) clientSaw.Add(message);
			return ValueTask.CompletedTask;
		});

		await wire.SettleAsync();

		// The server accepted it, and gave it the identifier the client chose.
		await Assert.That(serverSide).IsNotNull();
		await Assert.That(serverSide!.Id).IsEqualTo(cord.Id);
		await Assert.That(cord.Id).StartsWith("R");

		await cord.SendAsync("say", ("text", "hello there"));
		await wire.SettleAsync();

		await Assert.That(serverSaw.Count).IsEqualTo(1);
		await Assert.That(serverSaw[0].Value("_message")).IsEqualTo("say");
		await Assert.That(serverSaw[0].Value("text")).IsEqualTo("hello there");

		// And back the other way, on the same cord.
		await serverSide.SendAsync("say", ("text", "hello yourself"));
		await wire.SettleAsync();

		await Assert.That(clientSaw.Count).IsEqualTo(1);
		await Assert.That(clientSaw[0].Value("text")).IsEqualTo("hello yourself");

		// Closing from one end disposes it on both.
		await cord.CloseAsync();
		await wire.SettleAsync();

		await Assert.That(cord.IsOpen).IsFalse();
		await Assert.That(serverSide.IsOpen).IsFalse();
		await Assert.That(wire.Server.PluginManager!.GetPlugin<McpCordProtocol>()!.Open).IsEmpty();
	}
}
