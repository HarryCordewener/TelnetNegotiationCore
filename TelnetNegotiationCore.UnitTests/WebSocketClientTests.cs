#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;
using TelnetNegotiationCore.WebSockets;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// TNC as a WebSocket client: a real <see cref="ClientWebSocket"/> handshake against an <see cref="HttpListener"/>
/// server, so subprotocol negotiation runs as it does against a game.
/// </summary>
[NotInParallel(nameof(WebSocketClientTests))]
public class WebSocketClientTests : BaseTest
{
	[Test]
	public async Task AClientConnectsWithTheTelnetSubprotocolAndExchangesGmcpWithAServerSession()
	{
		var serverGmcp = new ConcurrentQueue<(string Package, string Info)>();
		var serverRegistry = new WebSocketSubprotocolRegistry().AddTelnetSession(
			() => Builder(TelnetInterpreter.TelnetMode.Server)
				.AddPlugin<GMCPProtocol>()
					.OnGMCPMessage(m => { serverGmcp.Enqueue(m); return ValueTask.CompletedTask; }),
			async (connection, ct) =>
			{
				await connection.Interpreter.SendGMCPCommand("Room.Info", "{\"num\":1}");
				await connection.Completion;
			});
		await using var server = Server.Start(serverRegistry);

		using var socket = await TelnetWebSocketClient.ConnectAsync(server.Uri);
		await Assert.That(socket.SubProtocol).IsEqualTo(MudWebSocketSubprotocols.Telnet);

		var clientGmcp = new ConcurrentQueue<(string Package, string Info)>();
		var negotiated = false;
		var clientBuilder = Builder(TelnetInterpreter.TelnetMode.Client).AddPlugin<GMCPProtocol>();
		clientBuilder.Plugin
			.OnGMCPMessage(m => { clientGmcp.Enqueue(m); return ValueTask.CompletedTask; })
			.OnGMCPNegotiated(on => { negotiated = on; return ValueTask.CompletedTask; });
		await using var client = await clientBuilder.StartAsync(socket);

		await Assert.That(await PollUntilAsync(() => negotiated)).IsTrue();
		await client.Interpreter.SendGMCPCommand("Core.Hello", "{\"client\":\"ws-client\"}");

		await Assert.That(await PollUntilAsync(() => !serverGmcp.IsEmpty && !clientGmcp.IsEmpty)).IsTrue();
		await Assert.That(serverGmcp.Single()).IsEqualTo(("Core.Hello", "{\"client\":\"ws-client\"}"));
		await Assert.That(clientGmcp.Single()).IsEqualTo(("Room.Info", "{\"num\":1}"));

		// The client leaves; the server's session ends with it.
		await client.CloseAsync();
		await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		await server.LastRun.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.That(client.ClosedByServer).IsTrue();
	}

	[Test]
	public async Task AServerThatNamesNoSubprotocolIsTakenAsTelnet()
	{
		var registry = new WebSocketSubprotocolRegistry().AddTelnetSession(
			() => Builder(TelnetInterpreter.TelnetMode.Server).AddPlugin<GMCPProtocol>(),
			(connection, _) => connection.Completion,
			acceptNoSubprotocol: true);
		// A wss-to-telnet bridge: accepts without naming the subprotocol it was offered.
		await using var server = Server.Start(registry, acceptWithoutSubprotocol: true);

		using var socket = await TelnetWebSocketClient.ConnectAsync(server.Uri);
		await Assert.That(string.IsNullOrEmpty(socket.SubProtocol)).IsTrue();

		var negotiated = false;
		var builder = Builder(TelnetInterpreter.TelnetMode.Client).AddPlugin<GMCPProtocol>();
		builder.Plugin.OnGMCPNegotiated(on => { negotiated = on; return ValueTask.CompletedTask; });
		await using var client = await builder.StartAsync(socket);

		await Assert.That(await PollUntilAsync(() => negotiated)).IsTrue();
		await client.CloseAsync();
		await server.LastRun.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task ABridgeThatRefusesTheTelnetSubprotocolsIsDialledAgainOfferingNone()
	{
		var registry = new WebSocketSubprotocolRegistry().AddTelnetSession(
			() => Builder(TelnetInterpreter.TelnetMode.Server).AddPlugin<GMCPProtocol>(),
			(connection, _) => connection.Completion,
			acceptNoSubprotocol: true);
		// websockify: an offer of names it does not know is answered 400; no offer is accepted.
		await using var server = Server.Start(registry, acceptWithoutSubprotocol: true, refuseAnOffer: true);

		using var socket = await TelnetWebSocketClient.ConnectAsync(server.Uri);
		await Assert.That(string.IsNullOrEmpty(socket.SubProtocol)).IsTrue();
		await Assert.That(server.Handshakes).IsEqualTo(2);

		var negotiated = false;
		var builder = Builder(TelnetInterpreter.TelnetMode.Client).AddPlugin<GMCPProtocol>();
		builder.Plugin.OnGMCPNegotiated(on => { negotiated = on; return ValueTask.CompletedTask; });
		await using var client = await builder.StartAsync(socket);

		await Assert.That(await PollUntilAsync(() => negotiated)).IsTrue();
		await client.CloseAsync();
		await server.LastRun.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task AServerThatRefusesEveryHandshakeIsDialledOnlyTwice()
	{
		var registry = new WebSocketSubprotocolRegistry().AddTelnetSession(
			() => Builder(TelnetInterpreter.TelnetMode.Server),
			(connection, _) => connection.Completion);
		await using var server = Server.Start(registry, refuseEverything: true);

		await Assert.That(async () => await TelnetWebSocketClient.ConnectAsync(server.Uri)).Throws<WebSocketException>();
		await Assert.That(server.Handshakes).IsEqualTo(2);
	}

	[Test]
	public async Task AClientRegistryRunsWhicheverSubprotocolTheServerPicked()
	{
		// The server speaks only gmcp.mudstandards.org: no telnet layer.
		var serverRegistry = new WebSocketSubprotocolRegistry()
			.Add(MudWebSocketSubprotocols.Gmcp, () => new GmcpWebSocketHandler
			{
				Opened = async h =>
				{
					await ((GmcpWebSocketHandler)h).SendGmcpAsync("Char.Vitals", "{\"hp\":10}");
					await h.SendAsync("Name: ");
				},
				LineReceived = (h, line) => h.SendLineAsync($"Welcome, {line}."),
			});
		await using var server = Server.Start(serverRegistry);

		// The client offers telnet first and gmcp second; the server's choice decides which handler runs.
		var text = new ConcurrentQueue<string>();
		var gmcp = new ConcurrentQueue<(string Package, string Data)>();
		var clientRegistry = new WebSocketSubprotocolRegistry()
			.AddTelnetSession(() => Builder(TelnetInterpreter.TelnetMode.Client), (c, _) => c.Completion)
			.Add(MudWebSocketSubprotocols.Gmcp, () => new GmcpWebSocketHandler
			{
				TextReceived = async (h, received) =>
				{
					text.Enqueue(received);
					if (received == "Name: ") await h.SendLineAsync("Ada");
				},
				GmcpReceived = (_, package, data) => { gmcp.Enqueue((package, data)); return ValueTask.CompletedTask; },
			});

		using var socket = await clientRegistry.ConnectAsync(server.Uri);
		await Assert.That(socket.SubProtocol).IsEqualTo(MudWebSocketSubprotocols.Gmcp);
		var run = clientRegistry.RunAsync(socket);

		// The prompt arrives as it was sent, with no line ending to split on.
		await Assert.That(await PollUntilAsync(() => text.Contains("Welcome, Ada.\r\n"))).IsTrue();
		await Assert.That(text.First()).IsEqualTo("Name: ");
		await Assert.That(gmcp.Single()).IsEqualTo(("Char.Vitals", "{\"hp\":10}"));

		await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
		await run.WaitAsync(TimeSpan.FromSeconds(5));
		await server.LastRun.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private static TelnetInterpreterBuilder Builder(TelnetInterpreter.TelnetMode mode) =>
		new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback);

	/// <summary>An <see cref="HttpListener"/> WebSocket endpoint that runs a registry, one connection at a time.</summary>
	private sealed class Server : IAsyncDisposable
	{
		private readonly HttpListener _listener = new();
		private readonly CancellationTokenSource _stop = new();
		private readonly Task _accepting;
		private volatile Task _lastRun = Task.CompletedTask;
		private readonly TaskCompletionSource<bool> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

		private readonly bool _refuseAnOffer;
		private readonly bool _refuseEverything;
		private int _handshakes;

		private Server(WebSocketSubprotocolRegistry registry, bool acceptWithoutSubprotocol, bool refuseAnOffer, bool refuseEverything)
		{
			_refuseAnOffer = refuseAnOffer;
			_refuseEverything = refuseEverything;
			var port = FreePort();
			Uri = new Uri($"ws://127.0.0.1:{port}/");
			_listener.Prefixes.Add($"http://127.0.0.1:{port}/");
			_listener.Start();
			_accepting = AcceptAsync(registry, acceptWithoutSubprotocol);
		}

		public Uri Uri { get; }

		/// <summary>How many upgrade requests arrived, refused or not.</summary>
		public int Handshakes => Volatile.Read(ref _handshakes);

		/// <summary>The run of the most recent connection, once one has been accepted.</summary>
		public Task LastRun => _accepted.Task.ContinueWith(_ => _lastRun).Unwrap();

		public static Server Start(
			WebSocketSubprotocolRegistry registry,
			bool acceptWithoutSubprotocol = false,
			bool refuseAnOffer = false,
			bool refuseEverything = false) =>
			new(registry, acceptWithoutSubprotocol, refuseAnOffer, refuseEverything);

		private async Task AcceptAsync(WebSocketSubprotocolRegistry registry, bool acceptWithoutSubprotocol)
		{
			while (!_stop.IsCancellationRequested)
			{
				HttpListenerContext context;
				try
				{
					context = await _listener.GetContextAsync();
				}
				catch (Exception) when (_stop.IsCancellationRequested)
				{
					return;
				}

				var offered = (context.Request.Headers["Sec-WebSocket-Protocol"] ?? "")
					.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				Interlocked.Increment(ref _handshakes);
				if (_refuseEverything || (_refuseAnOffer && offered.Length > 0))
				{
					context.Response.StatusCode = 400;
					context.Response.Close();
					continue;
				}

				var chosen = acceptWithoutSubprotocol ? null : registry.Select(offered);
				var accepted = await context.AcceptWebSocketAsync(chosen, Timeout.InfiniteTimeSpan);
				_lastRun = RunAsync(registry, accepted.WebSocket);
				_accepted.TrySetResult(true);
			}
		}

		private async Task RunAsync(WebSocketSubprotocolRegistry registry, WebSocket socket)
		{
			using (socket)
			{
				await registry.RunAsync(socket, _stop.Token);
			}
		}

		private static int FreePort()
		{
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			return port;
		}

		public async ValueTask DisposeAsync()
		{
			await _stop.CancelAsync();
			_listener.Close();
			try { await _accepting.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
			try { await _lastRun.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
		}
	}
}
