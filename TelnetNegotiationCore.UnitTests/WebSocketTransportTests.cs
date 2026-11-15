#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TelnetNegotiationCore.WebSockets;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The mudstandards.org WebSocket subprotocols, run over a real RFC 6455 socket pair: two in-memory pipes joined
/// into a duplex stream on each side, with <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>
/// doing the framing.
/// </summary>
public class WebSocketTransportTests : BaseTest
{
	private static readonly byte[] DoGmcp = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP];

	[Test]
	public async Task TncServerAndTncClientExchangeGmcpAndTextOverTelnetSubprotocol()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Telnet);
		var serverGmcp = new ConcurrentQueue<(string Package, string Info)>();
		var clientGmcp = new ConcurrentQueue<(string Package, string Info)>();
		var serverLines = new ConcurrentQueue<string>();
		var clientNegotiated = false;

		var (server, serverRead) = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((bytes, encoding, _) => { serverLines.Enqueue(encoding.GetString(bytes)); return ValueTask.CompletedTask; })
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(m => { serverGmcp.Enqueue(m); return ValueTask.CompletedTask; })
			.BuildAndStartAsync(serverSocket);

		var clientBuilder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.AddPlugin<GMCPProtocol>();
		clientBuilder.Plugin
			.OnGMCPMessage(m => { clientGmcp.Enqueue(m); return ValueTask.CompletedTask; })
			.OnGMCPNegotiated(on => { clientNegotiated = on; return ValueTask.CompletedTask; });
		var (client, clientRead) = await clientBuilder.BuildAndStartAsync(clientSocket);

		await Assert.That(await PollUntilAsync(() => clientNegotiated)).IsTrue();

		await client.SendGMCPCommand("Core.Hello", "{\"client\":\"ws-test\"}");
		await client.SendAsync(Encoding.UTF8.GetBytes("look"));
		await server.SendGMCPCommand("Room.Info", "{\"num\":1}");

		await Assert.That(await PollUntilAsync(() => !serverGmcp.IsEmpty && !clientGmcp.IsEmpty && !serverLines.IsEmpty)).IsTrue();
		await Assert.That(serverGmcp.Single()).IsEqualTo(("Core.Hello", "{\"client\":\"ws-test\"}"));
		await Assert.That(clientGmcp.Single()).IsEqualTo(("Room.Info", "{\"num\":1}"));
		await Assert.That(serverLines.Single()).IsEqualTo("look");

		await clientSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
		await serverRead.WaitAsync(TimeSpan.FromSeconds(5));
		await clientRead.WaitAsync(TimeSpan.FromSeconds(5));
		await server.DisposeAsync();
		await client.DisposeAsync();
	}

	[Test]
	public async Task ATelnetCommandSplitAcrossFramesStillParses()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Telnet);
		var received = new ConcurrentQueue<(string Package, string Info)>();
		var (server, read) = await GmcpServerAsync(serverSocket, received);

		var message = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP };
		message.AddRange(Encoding.UTF8.GetBytes("Foo.Bar {\"a\":1}"));
		message.AddRange([(byte)Trigger.IAC, (byte)Trigger.SE]);
		var bytes = message.ToArray();

		await SendBinaryAsync(clientSocket, DoGmcp);
		await SendBinaryAsync(clientSocket, bytes[..1]);           // a lone IAC
		await SendBinaryAsync(clientSocket, bytes[1..9]);
		await SendBinaryAsync(clientSocket, bytes[9..^1]);         // ends between IAC and SE
		await SendBinaryAsync(clientSocket, bytes[^1..]);
		// Two messages in one frame.
		await SendBinaryAsync(clientSocket, [.. bytes, .. bytes]);

		await Assert.That(await PollUntilAsync(() => received.Count == 3)).IsTrue();
		await Assert.That(received.All(m => m == ("Foo.Bar", "{\"a\":1}"))).IsTrue();

		await CloseAsync(clientSocket, read);
		await server.DisposeAsync();
	}

	[Test]
	public async Task ConcurrentWritesNeverOverlapOnTheSocket()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Telnet);
		var (server, read) = await GmcpServerAsync(serverSocket, new ConcurrentQueue<(string, string)>());

		const int count = 200;
		var receiving = ReadBinaryAsync(clientSocket, until: bytes => CountGmcp(bytes) >= count);

		// A WebSocket that sees two sends at once throws InvalidOperationException from one of them.
		await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
			Task.Run(async () => await server.SendGMCPCommand("Char.Vitals", $"{{\"hp\":{i}}}"))));

		var all = await receiving.WaitAsync(TimeSpan.FromSeconds(10));
		await Assert.That(CountGmcp(all)).IsEqualTo(count);

		await CloseAsync(clientSocket, read);
		await server.DisposeAsync();
	}

	[Test]
	public async Task ATextFrameOnTheTelnetSubprotocolClosesWith1003()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Telnet);
		var (server, read) = await GmcpServerAsync(serverSocket, new ConcurrentQueue<(string, string)>());

		await clientSocket.SendAsync(Encoding.UTF8.GetBytes("look"), WebSocketMessageType.Text, true, CancellationToken.None);

		var status = await ReceiveCloseAsync(clientSocket);
		await Assert.That(status).IsEqualTo(WebSocketCloseStatus.InvalidMessageType);
		await clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
		await read.WaitAsync(TimeSpan.FromSeconds(5));
		await server.DisposeAsync();
	}

	[Test]
	public async Task APeerCloseEndsTheReadTaskAndIsAnswered()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Telnet);
		var (server, read) = await GmcpServerAsync(serverSocket, new ConcurrentQueue<(string, string)>());

		// CloseAsync waits for the server's close frame, so it completing is the answer arriving.
		await clientSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(5));
		await read.WaitAsync(TimeSpan.FromSeconds(5));

		await Assert.That(serverSocket.State).IsEqualTo(WebSocketState.Closed);
		await Assert.That(clientSocket.CloseStatus).IsEqualTo(WebSocketCloseStatus.NormalClosure);

		// Writing after the close is not an error: the interpreter may still be shutting down.
		await server.SendGMCPCommand("Core.Goodbye", "\"bye\"");
		await server.DisposeAsync();
	}

	[Test]
	public async Task NoSubprotocolIsTakenAsTelnet()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(null);
		var received = new ConcurrentQueue<(string Package, string Info)>();
		var (server, read) = await GmcpServerAsync(serverSocket, received);

		await SendBinaryAsync(clientSocket, [.. DoGmcp, .. GmcpBytes("Core.Ping", "")]);
		await Assert.That(await PollUntilAsync(() => received.Count == 1)).IsTrue();

		await CloseAsync(clientSocket, read);
		await server.DisposeAsync();
	}

	[Test]
	[Arguments(MudWebSocketSubprotocols.Gmcp)]
	[Arguments(MudWebSocketSubprotocols.Terminal)]
	[Arguments("markup.v1.example.org")]
	public async Task BuildAndStartRejectsANonTelnetSubprotocol(string subprotocol)
	{
		var (serverSocket, _) = WebSocketPair.Create(subprotocol);

		await Assert.That(async () => await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.BuildAndStartAsync(serverSocket))
			.Throws<ArgumentException>();
	}

	[Test]
	public async Task TelnetBase64CarriesTheStreamInTextFrames()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.TelnetBase64);
		var received = new ConcurrentQueue<(string Package, string Info)>();
		var (server, read) = await GmcpServerAsync(serverSocket, received);

		var payload = Convert.ToBase64String([.. DoGmcp, .. GmcpBytes("Foo.Bar", "{}")]);
		// Split inside a four-character group: decoding has to wait for the whole message.
		var bytes = Encoding.ASCII.GetBytes(payload);
		await clientSocket.SendAsync(bytes.AsMemory(0, 5), WebSocketMessageType.Text, false, CancellationToken.None);
		await clientSocket.SendAsync(bytes.AsMemory(5), WebSocketMessageType.Text, true, CancellationToken.None);
		await Assert.That(await PollUntilAsync(() => received.Count == 1)).IsTrue();
		await Assert.That(received.Single()).IsEqualTo(("Foo.Bar", "{}"));

		var outbound = ReceiveMessageAsync(clientSocket, m => m.Type == WebSocketMessageType.Text
			&& Convert.FromBase64String(Encoding.ASCII.GetString(m.Data)).SequenceEqual(GmcpBytes("Room.Info", "{}")));
		await server.SendGMCPCommand("Room.Info", "{}");
		await Assert.That(await outbound.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();

		await CloseAsync(clientSocket, read);
		await server.DisposeAsync();
	}

	[Test]
	public async Task RegistrySelectsByServerPreferenceAndKeepsTheClientsSpelling()
	{
		var registry = new WebSocketSubprotocolRegistry()
			.Add("markup.v1.example.org", () => new EchoHandler())
			.AddTelnet(() => new TelnetInterpreterBuilder().UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger));

		await Assert.That(registry.Names).IsEquivalentTo(new[]
		{
			"markup.v1.example.org", MudWebSocketSubprotocols.Telnet, MudWebSocketSubprotocols.TelnetAlias,
		});
		await Assert.That(registry.Select(["telnet", "Markup.V1.Example.Org"])).IsEqualTo("Markup.V1.Example.Org");
		await Assert.That(registry.Select(["telnet", "TELNET.mudstandards.org"])).IsEqualTo("TELNET.mudstandards.org");
		await Assert.That(registry.Select([" telnet "])).IsEqualTo("telnet");
		await Assert.That(registry.Select(["gmcp.mudstandards.org"])).IsNull();
		await Assert.That(registry.Select([])).IsNull();
		await Assert.That(registry.Select(null)).IsNull();
		await Assert.That(() => registry.Add("TELNET", () => new EchoHandler())).Throws<ArgumentException>();
		await Assert.That(() => registry.Add("a,b", () => new EchoHandler())).Throws<ArgumentException>();
	}

	[Test]
	public async Task RegistryRunsTelnetForTheTelnetSubprotocol()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.TelnetAlias);
		var received = new ConcurrentQueue<(string Package, string Info)>();
		TelnetInterpreter? connected = null;

		var registry = new WebSocketSubprotocolRegistry().AddTelnet(
			() => new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.AddPlugin<GMCPProtocol>()
					.OnGMCPMessage(m => { received.Enqueue(m); return ValueTask.CompletedTask; }),
			onConnected: (interpreter, _) => { connected = interpreter; return ValueTask.CompletedTask; });

		var run = registry.RunAsync(serverSocket);
		await SendBinaryAsync(clientSocket, [.. DoGmcp, .. GmcpBytes("Core.Ping", "")]);
		await Assert.That(await PollUntilAsync(() => received.Count == 1)).IsTrue();
		await Assert.That(connected).IsNotNull();

		await CloseAsync(clientSocket, run);
	}

	[Test]
	public async Task GmcpSubprotocolSplitsInputLinesAndCarriesGmcpInTextFrames()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Gmcp);
		var lines = new ConcurrentQueue<string>();
		var gmcp = new ConcurrentQueue<(string, string)>();
		GmcpWebSocketHandler? handler = null;

		var registry = new WebSocketSubprotocolRegistry().Add(MudWebSocketSubprotocols.Gmcp, () => handler = new GmcpWebSocketHandler
		{
			LineReceived = (_, line) => { lines.Enqueue(line); return ValueTask.CompletedTask; },
			GmcpReceived = (_, package, data) => { gmcp.Enqueue((package, data)); return ValueTask.CompletedTask; },
		});
		var run = registry.RunAsync(serverSocket);

		await SendBinaryAsync(clientSocket, Encoding.UTF8.GetBytes("look\r\nsay hi\n"));
		await SendBinaryAsync(clientSocket, Encoding.UTF8.GetBytes("north"));
		await clientSocket.SendAsync(Encoding.UTF8.GetBytes("Core.Hello {\"client\":\"x\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
		await clientSocket.SendAsync(Encoding.UTF8.GetBytes("Core.Ping"), WebSocketMessageType.Text, true, CancellationToken.None);

		await Assert.That(await PollUntilAsync(() => lines.Count == 3 && gmcp.Count == 2)).IsTrue();
		await Assert.That(lines).IsEquivalentTo(new[] { "look", "say hi", "north" });
		await Assert.That(gmcp).IsEquivalentTo(new[] { ("Core.Hello", "{\"client\":\"x\"}"), ("Core.Ping", "") });

		var gmcpOut = ReceiveMessageAsync(clientSocket, m => m.Type == WebSocketMessageType.Text && Encoding.UTF8.GetString(m.Data) == "Room.Info {\"num\":1}");
		await handler!.Send("Room.Info", "{\"num\":1}");
		await Assert.That(await gmcpOut.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();

		var textOut = ReceiveMessageAsync(clientSocket, m => m.Type == WebSocketMessageType.Binary && Encoding.UTF8.GetString(m.Data) == "\u001b[1mHello\u001b[0m\r\n");
		await handler.SendLineAsync("\u001b[1mHello\u001b[0m");
		await Assert.That(await textOut.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();

		await CloseAsync(clientSocket, run);
	}

	[Test]
	public async Task TerminalSubprotocolRefusesTextFrames()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Terminal);
		var registry = new WebSocketSubprotocolRegistry().Add(MudWebSocketSubprotocols.Terminal, () => new TerminalWebSocketHandler());
		var run = registry.RunAsync(serverSocket);

		await clientSocket.SendAsync(Encoding.UTF8.GetBytes("Core.Hello"), WebSocketMessageType.Text, true, CancellationToken.None);
		await Assert.That(await ReceiveCloseAsync(clientSocket)).IsEqualTo(WebSocketCloseStatus.InvalidMessageType);
		await clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
		await run.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task RegistryClosesWith1002WhenNothingHandlesTheSubprotocol()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create("unknown.example.org");
		var run = new WebSocketSubprotocolRegistry().Add("other.example.org", () => new EchoHandler()).RunAsync(serverSocket);

		await Assert.That(await ReceiveCloseAsync(clientSocket)).IsEqualTo(WebSocketCloseStatus.ProtocolError);
		await run.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task ALongUnknownSubprotocolIsStillRefusedWith1002()
	{
		// A reason that named the subprotocol would pass the 123-byte close-frame limit and throw instead.
		var (serverSocket, clientSocket) = WebSocketPair.Create(new string('x', 200) + ".example.org");
		var run = new WebSocketSubprotocolRegistry().Add("other.example.org", () => new EchoHandler()).RunAsync(serverSocket);

		await Assert.That(await ReceiveCloseAsync(clientSocket)).IsEqualTo(WebSocketCloseStatus.ProtocolError);
		await run.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task ACloseReasonLongerThanTheFrameAllowsIsCutShort()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(null);
		var connection = new MudWebSocketConnection(serverSocket);

		await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, new string('é', 100));

		var buffer = new byte[256];
		var result = await clientSocket.ReceiveAsync(buffer, CancellationToken.None);
		await Assert.That(result.CloseStatus).IsEqualTo(WebSocketCloseStatus.PolicyViolation);
		await Assert.That(Encoding.UTF8.GetByteCount(result.CloseStatusDescription!)).IsLessThanOrEqualTo(MudWebSocketConnection.MaxCloseDescriptionBytes);
		await Assert.That(connection.CanSend).IsFalse();
	}

	[Test]
	public async Task TheRestOfAnOversizedBase64MessageNeverReachesTheInterpreter()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.TelnetBase64);
		var received = new ConcurrentQueue<(string Package, string Info)>();
		var (server, read) = await GmcpServerAsync(serverSocket, received);

		var payload = Encoding.ASCII.GetBytes(Convert.ToBase64String([.. DoGmcp, .. GmcpBytes("Foo.Bar", "{}")]));
		await clientSocket.SendAsync(new byte[WebSocketMessageHandler.DefaultMaxMessageSize + 1], WebSocketMessageType.Text, false, CancellationToken.None);
		// The final chunk is valid Base64 on its own, and would be a GMCP message if it started a new accumulator.
		await clientSocket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);

		await Assert.That(await ReceiveCloseAsync(clientSocket)).IsEqualTo(WebSocketCloseStatus.MessageTooBig);
		await clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
		await read.WaitAsync(TimeSpan.FromSeconds(5));
		await server.WaitForProcessingAsync(500, 0);
		await Assert.That(received).IsEmpty();
		await server.DisposeAsync();
	}

	[Test]
	public async Task RegistryUsesTheFallbackWhenNoSubprotocolWasNegotiated()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(null);
		var run = new WebSocketSubprotocolRegistry().WithFallback(() => new EchoHandler()).RunAsync(serverSocket);

		var echo = ReceiveMessageAsync(clientSocket, m => Encoding.UTF8.GetString(m.Data) == "echo: hi");
		await clientSocket.SendAsync(Encoding.UTF8.GetBytes("hi"), WebSocketMessageType.Text, true, CancellationToken.None);
		await Assert.That(await echo.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();

		await CloseAsync(clientSocket, run);
	}

	[Test]
	public async Task ACustomMessageHandlerGetsWholeMessagesAndAMessageSizeLimit()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create("markup.v1.example.org");
		var run = new WebSocketSubprotocolRegistry()
			.Add("markup.v1.example.org", () => new EchoHandler { MaxMessageSize = 64 })
			.RunAsync(serverSocket);

		// One message in three frames arrives as one message.
		var echo = ReceiveMessageAsync(clientSocket, m => Encoding.UTF8.GetString(m.Data) == "echo: {\"t\":\"Hello\"}");
		await clientSocket.SendAsync(Encoding.UTF8.GetBytes("{\"t\":"), WebSocketMessageType.Text, false, CancellationToken.None);
		await clientSocket.SendAsync(Encoding.UTF8.GetBytes("\"Hel"), WebSocketMessageType.Text, false, CancellationToken.None);
		await clientSocket.SendAsync(Encoding.UTF8.GetBytes("lo\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
		await Assert.That(await echo.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();

		await clientSocket.SendAsync(new byte[65], WebSocketMessageType.Text, true, CancellationToken.None);
		await Assert.That(await ReceiveCloseAsync(clientSocket)).IsEqualTo(WebSocketCloseStatus.MessageTooBig);
		await clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
		await run.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	[Arguments("look", new[] { "look" })]
	[Arguments("look\r\n", new[] { "look" })]
	[Arguments("a\nb\r\nc", new[] { "a", "b", "c" })]
	[Arguments("\n", new[] { "" })]
	[Arguments("", new[] { "" })]
	[Arguments("a\n\nb\n", new[] { "a", "", "b" })]
	public async Task TerminalInputIsSplitIntoLines(string input, string[] expected)
	{
		await Assert.That(TerminalWebSocketHandler.SplitLines(input)).IsEquivalentTo(expected);
	}

	[Test]
	[Arguments("Core.Ping", true, "Core.Ping", "")]
	[Arguments("Char.Vitals {\"hp\":1}", true, "Char.Vitals", "{\"hp\":1}")]
	[Arguments("  Room.Info   [1, 2]  ", true, "Room.Info", "[1, 2]")]
	[Arguments("   ", false, "", "")]
	public async Task GmcpTextFramesParseAtTheFirstSpace(string message, bool ok, string package, string data)
	{
		await Assert.That(GmcpWebSocketHandler.TryParse(message, out var p, out var d)).IsEqualTo(ok);
		await Assert.That(p).IsEqualTo(package);
		await Assert.That(d).IsEqualTo(data);
	}

	[Test]
	public async Task MsspReportsTheWebSocketVariablesAsOneValueEach()
	{
		byte[]? sent = null;
		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				var bytes = data.ToArray();
				if (bytes.Length > 3 && bytes[1] == (byte)Trigger.SB && bytes[2] == (byte)Trigger.MSSP) sent = bytes;
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();
		server.PluginManager!.GetPlugin<MSSPProtocol>()!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "Test",
			WebSocket = "wss://game.example/ws,ws://game.example/ws",
			WebSocketSubprotocols = "telnet.mudstandards.org,gmcp.mudstandards.org",
		});

		await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP]);
		await PollUntilAsync(() => sent != null);

		await Assert.That(Contains(sent!, Variable("WEBSOCKET", "wss://game.example/ws,ws://game.example/ws"))).IsTrue();
		await Assert.That(Contains(sent!, Variable("WEBSOCKET_SUBPROTOCOLS", "telnet.mudstandards.org,gmcp.mudstandards.org"))).IsTrue();
		await server.DisposeAsync();

		static byte[] Variable(string name, string value) =>
			[(byte)Trigger.MSSP_VAR, .. Encoding.ASCII.GetBytes(name), (byte)Trigger.MSSP_VAL, .. Encoding.ASCII.GetBytes(value)];

		static bool Contains(byte[] haystack, byte[] needle) =>
			Enumerable.Range(0, haystack.Length - needle.Length + 1).Any(i => haystack.AsSpan(i, needle.Length).SequenceEqual(needle));
	}

	private static async Task<(TelnetInterpreter Server, Task Read)> GmcpServerAsync(
		WebSocket socket, ConcurrentQueue<(string Package, string Info)> received) =>
		await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(m => { received.Enqueue(m); return ValueTask.CompletedTask; })
			.BuildAndStartAsync(socket);

	private static byte[] GmcpBytes(string package, string data) =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP,
		.. Encoding.UTF8.GetBytes(data.Length == 0 ? package : package + " " + data),
		(byte)Trigger.IAC, (byte)Trigger.SE,
	];

	private static int CountGmcp(byte[] bytes)
	{
		var count = 0;
		for (var i = 0; i + 2 < bytes.Length; i++)
		{
			if (bytes[i] == (byte)Trigger.IAC && bytes[i + 1] == (byte)Trigger.SB && bytes[i + 2] == (byte)Trigger.GMCP) count++;
		}

		return count;
	}

	private static Task SendBinaryAsync(WebSocket socket, byte[] data) =>
		socket.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);

	private static async Task CloseAsync(WebSocket client, Task serverRun)
	{
		await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
		await serverRun.WaitAsync(TimeSpan.FromSeconds(5));
	}

	/// <summary>Receives until a BINARY payload satisfies <paramref name="until"/>, returning everything received.</summary>
	private static async Task<byte[]> ReadBinaryAsync(WebSocket socket, Func<byte[], bool> until)
	{
		var all = new MemoryStream();
		var buffer = new byte[4096];
		while (!until(all.ToArray()))
		{
			var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
			if (result.MessageType == WebSocketMessageType.Close) break;
			all.Write(buffer, 0, result.Count);
		}

		return all.ToArray();
	}

	/// <summary>Receives whole messages until one matches; false when the socket closes first.</summary>
	private static async Task<bool> ReceiveMessageAsync(WebSocket socket, Func<(WebSocketMessageType Type, byte[] Data), bool> match)
	{
		var buffer = new byte[4096];
		var message = new MemoryStream();
		while (true)
		{
			var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
			if (result.MessageType == WebSocketMessageType.Close) return false;
			message.Write(buffer, 0, result.Count);
			if (!result.EndOfMessage) continue;
			if (match((result.MessageType, message.ToArray()))) return true;
			message.SetLength(0);
		}
	}

	private static async Task<WebSocketCloseStatus?> ReceiveCloseAsync(WebSocket socket)
	{
		var buffer = new byte[4096];
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (true)
		{
			var result = await socket.ReceiveAsync(buffer, timeout.Token);
			if (result.MessageType == WebSocketMessageType.Close) return result.CloseStatus;
		}
	}

	/// <summary>Echoes each TEXT message back with an <c>echo: </c> prefix.</summary>
	private sealed class EchoHandler : WebSocketMessageHandler
	{
		protected override ValueTask OnTextMessageAsync(string text, CancellationToken cancellationToken) =>
			Connection.SendTextAsync("echo: " + text, cancellationToken);
	}
}

/// <summary>
/// A connected server/client WebSocket pair over in-memory pipes.
/// </summary>
internal static class WebSocketPair
{
	public static (WebSocket Server, WebSocket Client) Create(string? subprotocol)
	{
		var toServer = new Pipe();
		var toClient = new Pipe();
		var server = WebSocket.CreateFromStream(new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
			new WebSocketCreationOptions { IsServer = true, SubProtocol = subprotocol, KeepAliveInterval = Timeout.InfiniteTimeSpan });
		var client = WebSocket.CreateFromStream(new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream()),
			new WebSocketCreationOptions { IsServer = false, SubProtocol = subprotocol, KeepAliveInterval = Timeout.InfiniteTimeSpan });
		return (server, client);
	}

	private sealed class DuplexStream(Stream input, Stream output) : Stream
	{
		public override bool CanRead => true;
		public override bool CanWrite => true;
		public override bool CanSeek => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => output.Flush();
		public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
		public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => input.ReadAsync(buffer, offset, count, cancellationToken);
		public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => output.WriteAsync(buffer, offset, count, cancellationToken);
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				input.Dispose();
				output.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}
