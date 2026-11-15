#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Connections;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;
using TelnetNegotiationCore.WebSockets;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// A client interpreter on <c>gmcp.mudstandards.org</c> and <c>terminal.mudstandards.org</c>, against a server that
/// speaks them as Evennia does: text in BINARY frames, GMCP in TEXT frames, and no telnet at all.
/// </summary>
public class WebSocketBridgeTests : BaseTest
{
	[Test]
	public async Task OnTerminalTheServersTextIsReadAndTheClientsLineArrivesWithNoTelnet()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Terminal);
		var server = new RawServer(serverSocket);
		var lines = new ConcurrentQueue<string>();

		await using var client = await ClientBuilder(lines).AddDefaultMUDProtocols().StartAsync(clientSocket);
		await server.SendBinaryAsync("\u001b[1mWelcome\u001b[0m\r\nName: ");
		await server.SendBinaryAsync("look around\r\n");
		await Assert.That(await PollUntilAsync(() => lines.Count >= 2)).IsTrue();
		await Assert.That(lines.First()).IsEqualTo("\u001b[1mWelcome\u001b[0m");

		await client.Interpreter.SendAsync(Encoding.UTF8.GetBytes("Corvid"));
		await Assert.That(await PollUntilAsync(() => server.Binary.Any(f => Encoding.UTF8.GetString(f).Contains("Corvid")))).IsTrue();

		// Every default plugin has had its chance to negotiate by now; not one byte of it went out.
		await Assert.That(server.Text.IsEmpty).IsTrue();
		await Assert.That(server.Binary.SelectMany(f => f).Contains((byte)0xFF)).IsFalse();
		await Assert.That(string.Concat(server.Binary.Select(Encoding.UTF8.GetString))).IsEqualTo("Corvid\r\n");
	}

	[Test]
	public async Task OnGmcpTheClientIsToldGmcpIsOnAndGmcpGoesBothWaysInTextFrames()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Gmcp);
		var server = new RawServer(serverSocket);
		var gmcp = new ConcurrentQueue<(string Package, string Info)>();
		var negotiated = false;

		var builder = ClientBuilder(new ConcurrentQueue<string>()).AddPlugin<GMCPProtocol>();
		builder.Plugin
			.OnGMCPMessage(m => { gmcp.Enqueue(m); return ValueTask.CompletedTask; })
			.OnGMCPNegotiated(on => { negotiated = on; return ValueTask.CompletedTask; });
		await using var client = await builder.StartAsync(clientSocket);

		await Assert.That(await PollUntilAsync(() => negotiated)).IsTrue();
		await client.Interpreter.SendGMCPCommand("Core.Hello", "{\"client\":\"bridge\"}");
		await server.SendTextAsync("Char.Vitals {\"hp\":10}");

		await Assert.That(await PollUntilAsync(() => !server.Text.IsEmpty && !gmcp.IsEmpty)).IsTrue();
		await Assert.That(server.Text.Single()).IsEqualTo("Core.Hello {\"client\":\"bridge\"}");
		await Assert.That(gmcp.Single()).IsEqualTo(("Char.Vitals", "{\"hp\":10}"));
		// The answer to the synthesised WILL GMCP is not the server's to read.
		await Assert.That(server.Binary.IsEmpty).IsTrue();
	}

	[Test]
	public async Task AByteThatLooksLikeIacInTheServersTextIsData()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Terminal);
		var server = new RawServer(serverSocket);
		var lines = new ConcurrentQueue<byte[]>();

		await using var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((bytes, _, _) => { lines.Enqueue(bytes); return ValueTask.CompletedTask; })
			.StartAsync(clientSocket);
		await server.SendBinaryAsync([0x41, 0xFF, 0xFB, 0x01, 0x42, 0x0D, 0x0A]);

		await Assert.That(await PollUntilAsync(() => !lines.IsEmpty)).IsTrue();
		await Assert.That(lines.Single()).IsEquivalentTo(new byte[] { 0x41, 0xFF, 0xFB, 0x01, 0x42 });
	}

	[Test]
	public async Task TheWriteSideDropsEveryTelnetCommandEvenWhenSplitAcrossWrites()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Gmcp);
		var server = new RawServer(serverSocket);
		var transport = TelnetTransport.WebSocketClient(clientSocket);
		var reading = transport.ReadAsync(_ => default, CancellationToken.None);

		await transport.WriteAsync(new byte[] { 0xFF, 0xFB, 0x1F, (byte)'a', 0xFF }, CancellationToken.None); // WILL NAWS, a, IAC…
		await transport.WriteAsync(new byte[] { 0xFF, (byte)'b', 0xFF, 0xF9 }, CancellationToken.None);       // …IAC = 0xFF, b, GA
		await transport.WriteAsync(new byte[] { 0xFF, 0xFA, 0x1F, 0x00, 0x50, 0xFF }, CancellationToken.None); // SB NAWS 0 80 IAC…
		await transport.WriteAsync(new byte[] { 0xF0, 0xFF, 0xFA, 0xC9 }, CancellationToken.None);             // …SE, SB GMCP…
		await transport.WriteAsync(Encoding.UTF8.GetBytes("Core.Ping"), CancellationToken.None);
		await transport.WriteAsync(new byte[] { 0xFF, 0xF0, (byte)'c' }, CancellationToken.None);             // …SE, c

		await Assert.That(await PollUntilAsync(() => server.Binary.Count >= 3 && !server.Text.IsEmpty)).IsTrue();
		await Assert.That(server.Binary.Select(f => Convert.ToHexString(f)).ToArray())
			.IsEquivalentTo(new[] { "61", "FF62", "63" });
		await Assert.That(server.Text.Single()).IsEqualTo("Core.Ping");

		await transport.CloseAsync(CancellationToken.None);
		await reading.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task ATextFrameOnTerminalClosesTheSocketWith1003()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Terminal);
		var server = new RawServer(serverSocket);
		await using var client = await ClientBuilder(new ConcurrentQueue<string>()).StartAsync(clientSocket);

		await server.SendTextAsync("[\"text\", [\"hello\"], {}]");

		await Assert.That(await PollUntilAsync(() => server.CloseStatus is not null)).IsTrue();
		await Assert.That(server.CloseStatus).IsEqualTo(WebSocketCloseStatus.InvalidMessageType);
	}

	[Test]
	public async Task AServerInterpreterStillRefusesGmcpAndTerminal()
	{
		var (serverSocket, _) = WebSocketPair.Create(MudWebSocketSubprotocols.Terminal);
		await Assert.That(() => TelnetTransport.WebSocket(serverSocket)).Throws<ArgumentException>();
		await Assert.That(async () => await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.StartAsync(serverSocket))
			.Throws<ArgumentException>();
	}

	[Test]
	[Arguments("json.mudstandards.org")]
	[Arguments("v1.evennia.com")]
	public async Task AClientRefusesASubprotocolItCannotRead(string subprotocol)
	{
		var (_, clientSocket) = WebSocketPair.Create(subprotocol);
		await Assert.That(() => TelnetTransport.WebSocketClient(clientSocket)).Throws<ArgumentException>();
	}

	[Test]
	public async Task ACustomSubprotocolPlugsInWithItsOwnBridge()
	{
		// A made-up subprotocol: text in TEXT frames, and a GA the client writes becomes a "ga" TEXT frame.
		const string name = "lines.v1.example.org";
		var (serverSocket, clientSocket) = WebSocketPair.Create(name);
		var server = new RawServer(serverSocket);
		var bridges = new WebSocketClientBridges().Add(name, () => new TextLinesBridge());
		var lines = new ConcurrentQueue<string>();

		var transport = TelnetTransport.WebSocketClient(clientSocket, bridges);
		await using var client = await ClientBuilder(lines).StartAsync(transport);
		await server.SendTextAsync("hello there\r\n");
		// What the interpreter writes for a line and then a GA.
		await transport.WriteAsync(new byte[] { (byte)'h', (byte)'i', 0x0D, 0x0A, 0xFF, 0xF9 }, CancellationToken.None);

		await Assert.That(await PollUntilAsync(() => !lines.IsEmpty && server.Text.Count >= 2)).IsTrue();
		await Assert.That(lines.Single()).IsEqualTo("hello there");
		await Assert.That(server.Text.ToArray()).IsEquivalentTo(new[] { "hi", "ga" });
		await Assert.That(server.Binary.IsEmpty).IsTrue();
	}

	[Test]
	public async Task AnotherOptionCanRideTextFramesTheWayGmcpDoes()
	{
		const string name = "msdp.v1.example.org";
		var (serverSocket, clientSocket) = WebSocketPair.Create(name);
		var server = new RawServer(serverSocket);
		var bridges = WebSocketClientBridges.Default.Add(name, () => new OptionTextFrameBridge((byte)Models.Trigger.MSDP));
		var transport = TelnetTransport.WebSocketClient(clientSocket, bridges);
		var fed = new ConcurrentQueue<byte[]>();
		var reading = transport.ReadAsync(d => { fed.Enqueue(d.ToArray()); return default; }, CancellationToken.None);

		await server.SendTextAsync("hp");
		// A GMCP subnegotiation is not this bridge's option, and is dropped; an MSDP one goes out as TEXT.
		await transport.WriteAsync(new byte[] { 0xFF, 0xFA, 0xC9, (byte)'x', 0xFF, 0xF0, 0xFF, 0xFA, 0x45, (byte)'y', 0xFF, 0xF0 }, CancellationToken.None);

		await Assert.That(await PollUntilAsync(() => fed.Count >= 2 && !server.Text.IsEmpty)).IsTrue();
		await Assert.That(Convert.ToHexString(fed.First())).IsEqualTo("FFFB45");
		await Assert.That(Convert.ToHexString(fed.Skip(1).First())).IsEqualTo("FFFA456870FFF0");
		await Assert.That(server.Text.Single()).IsEqualTo("y");

		await transport.CloseAsync(CancellationToken.None);
		await reading.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task TheBridgeSetRefusesTelnetNamesDuplicatesAndNonTokens()
	{
		await Assert.That(() => new WebSocketClientBridges().Add(MudWebSocketSubprotocols.Telnet, () => new TerminalClientBridge()))
			.Throws<ArgumentException>();
		await Assert.That(() => new WebSocketClientBridges().Add(MudWebSocketSubprotocols.TelnetBase64, () => new TerminalClientBridge()))
			.Throws<ArgumentException>();
		await Assert.That(() => WebSocketClientBridges.Default.Add(MudWebSocketSubprotocols.Gmcp, () => new TerminalClientBridge()))
			.Throws<ArgumentException>();
		await Assert.That(() => new WebSocketClientBridges().Add("a b", () => new TerminalClientBridge()))
			.Throws<ArgumentException>();
		await Assert.That(WebSocketClientBridges.Default.Names.ToArray())
			.IsEquivalentTo(new[] { MudWebSocketSubprotocols.Gmcp, MudWebSocketSubprotocols.Terminal });
	}

	private sealed class TextLinesBridge : WebSocketClientBridge
	{
		protected override ValueTask OnTextMessageAsync(string text, TelnetInput input, CancellationToken cancellationToken) =>
			input.TextAsync(text);

		protected override ValueTask OnTextAsync(ReadOnlyMemory<byte> text, CancellationToken cancellationToken) =>
			Connection.SendTextAsync(Encoding.UTF8.GetString(text.ToArray()).TrimEnd('\r', '\n'), cancellationToken);

		protected override ValueTask OnCommandAsync(byte command, byte? option, CancellationToken cancellationToken) =>
			command == 0xF9 ? Connection.SendTextAsync("ga", cancellationToken) : default;
	}

	private static TelnetInterpreterBuilder ClientBuilder(ConcurrentQueue<string> lines) =>
		new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((bytes, encoding, _) => { lines.Enqueue(encoding.GetString(bytes)); return ValueTask.CompletedTask; });

	/// <summary>The server end of a socket pair, recording every frame it receives and sending frames as given.</summary>
	private sealed class RawServer
	{
		private readonly WebSocket _socket;

		public RawServer(WebSocket socket)
		{
			_socket = socket;
			_ = ReceiveAsync();
		}

		public ConcurrentQueue<byte[]> Binary { get; } = new();
		public ConcurrentQueue<string> Text { get; } = new();
		public WebSocketCloseStatus? CloseStatus { get; private set; }

		public Task SendBinaryAsync(string text) => SendBinaryAsync(Encoding.UTF8.GetBytes(text));

		public Task SendBinaryAsync(byte[] bytes) =>
			_socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, CancellationToken.None);

		public Task SendTextAsync(string text) =>
			_socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None);

		private async Task ReceiveAsync()
		{
			var buffer = new byte[65536];
			try
			{
				while (_socket.State == WebSocketState.Open)
				{
					var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
					if (result.MessageType == WebSocketMessageType.Close)
					{
						CloseStatus = result.CloseStatus;
						await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
						return;
					}

					var frame = buffer.AsSpan(0, result.Count).ToArray();
					if (result.MessageType == WebSocketMessageType.Text) Text.Enqueue(Encoding.UTF8.GetString(frame));
					else Binary.Enqueue(frame);
				}
			}
			catch (Exception)
			{
				// The pair was torn down.
			}
		}
	}
}
