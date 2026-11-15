#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Connections;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TelnetNegotiationCore.WebSockets;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="TelnetConnection"/>, the builder's read and write hooks, and <see cref="ITelnetSession"/> run the same
/// way over a pipe and over a WebSocket.
/// </summary>
public class TelnetConnectionTests : BaseTest
{
	private static readonly byte[] DoGmcp = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP];

	private static readonly byte[] GmcpHello =
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP,
		.. Encoding.UTF8.GetBytes("Core.Hello {\"client\":\"test\"}"),
		(byte)Trigger.IAC, (byte)Trigger.SE,
	];

	[Test]
	public async Task OnReceivedSeesEveryByteBeforeTheInterpreter()
	{
		var wire = new PipeWire();
		long received = 0;
		var connection = await ServerBuilder()
			.OnReceived(data => { Interlocked.Add(ref received, data.Length); return ValueTask.CompletedTask; })
			.StartAsync(wire.Server);

		await wire.SendAsync(DoGmcp);
		await wire.SendAsync(GmcpHello);

		await Assert.That(await PollUntilAsync(() => Interlocked.Read(ref received) == DoGmcp.Length + GmcpHello.Length)).IsTrue();
		await connection.DisposeAsync();
	}

	[Test]
	public async Task OnInputProcessedRunsAfterTheChunksCallbacksHaveRun()
	{
		var wire = new PipeWire();
		var gmcp = new ConcurrentQueue<string>();
		var seenWhenProcessed = new ConcurrentQueue<int>();
		var builder = ServerBuilder()
			.OnInputProcessed(_ => { seenWhenProcessed.Enqueue(gmcp.Count); return ValueTask.CompletedTask; })
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(m => { gmcp.Enqueue(m.Package); return ValueTask.CompletedTask; });
		var connection = await builder.StartAsync(wire.Server);

		await wire.SendAsync([.. DoGmcp, .. GmcpHello]);

		await Assert.That(await PollUntilAsync(() => !seenWhenProcessed.IsEmpty)).IsTrue();
		// The one chunk carried the message, and its callback had run by the time the hook did.
		await Assert.That(seenWhenProcessed.First()).IsEqualTo(1);
		await connection.DisposeAsync();
	}

	[Test]
	public async Task WrapOutputSeesEveryWriteAndTheLastWrapperIsOutermost()
	{
		var wire = new PipeWire();
		var order = new ConcurrentQueue<string>();
		long written = 0;
		var builder = ServerBuilder()
			.WrapOutput(write => async data =>
			{
				order.Enqueue("inner");
				await write(data);
				Interlocked.Add(ref written, data.Length);
			})
			.WrapOutput(write => data =>
			{
				order.Enqueue("outer");
				return write(data);
			});
		var connection = await builder.StartAsync(wire.Server);

		await connection.Interpreter.SendAsync(Encoding.ASCII.GetBytes("hello"));

		var sent = await wire.ReceiveAsync(until: bytes => bytes.Length >= 5);
		await Assert.That(Interlocked.Read(ref written)).IsEqualTo((long)sent.Length);
		await Assert.That(order.Take(2).ToArray()).IsEquivalentTo(new[] { "outer", "inner" });
		await connection.DisposeAsync();
	}

	[Test]
	public async Task TheTupleStartsApplyTheHooksToo()
	{
		var wire = new PipeWire();
		long received = 0;
		var (interpreter, read) = await ServerBuilder()
			.OnReceived(data => { Interlocked.Add(ref received, data.Length); return ValueTask.CompletedTask; })
			.BuildAndStartAsync(wire.Server);

		await wire.SendAsync(DoGmcp);

		await Assert.That(await PollUntilAsync(() => Interlocked.Read(ref received) == DoGmcp.Length)).IsTrue();
		await wire.ToServer.Writer.CompleteAsync();
		await read.WaitAsync(TimeSpan.FromSeconds(5));
		await interpreter.DisposeAsync();
	}

	[Test]
	public async Task OnStartingRunsBeforeAnyInputReachesTheHooks()
	{
		var wire = new PipeWire();
		// Already waiting when the connection starts: the read loop would hand it over at once.
		await wire.SendAsync(DoGmcp);
		TelnetConnection? kept = null;
		var seen = new ConcurrentQueue<TelnetConnection?>();
		var connection = await ServerBuilder()
			.OnStarting(async started =>
			{
				// Give an early read loop every chance to run first.
				await Task.Delay(50);
				Volatile.Write(ref kept, started);
			})
			.OnReceived(_ => { seen.Enqueue(Volatile.Read(ref kept)); return ValueTask.CompletedTask; })
			.StartAsync(wire.Server);

		await Assert.That(await PollUntilAsync(() => !seen.IsEmpty)).IsTrue();
		await Assert.That(seen.First()).IsSameReferenceAs(connection);
		await connection.DisposeAsync();
	}

	[Test]
	public async Task AFailingOnStartingFailsTheStartAndReadsNothing()
	{
		var wire = new PipeWire();
		await wire.SendAsync(DoGmcp);
		var received = 0;
		var builder = ServerBuilder()
			.OnStarting(_ => throw new InvalidOperationException("refused"))
			.OnReceived(_ => { Interlocked.Increment(ref received); return ValueTask.CompletedTask; });

		var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.StartAsync(wire.Server));

		await Assert.That(error!.Message).IsEqualTo("refused");
		await Task.Delay(100);
		await Assert.That(Volatile.Read(ref received)).IsEqualTo(0);
	}

	[Test]
	public async Task ASessionsOnStartingRunsBeforeTheSessionAndTheReadLoop()
	{
		var wire = new PipeWire();
		await wire.SendAsync(DoGmcp);
		var order = new ConcurrentQueue<string>();
		var session = TelnetSession.Create(
			() => ServerBuilder()
				.OnStarting(_ => { order.Enqueue("starting"); return ValueTask.CompletedTask; })
				.OnReceived(_ => { order.Enqueue("received"); return ValueTask.CompletedTask; }),
			async (connection, _) =>
			{
				order.Enqueue("run");
				await wire.ToServer.Writer.CompleteAsync();
				await connection.Completion;
			});

		await TelnetConnection.RunAsync(session, TelnetTransport.Pipe(wire.Server)).WaitAsync(TimeSpan.FromSeconds(5));

		await Assert.That(order.First()).IsEqualTo("starting");
		await Assert.That(order).Contains("received");
		await Assert.That(order).Contains("run");
	}

	[Test]
	public async Task CloseAsyncUsesTheTransportsCloseAndMarksTheConnectionClosedByServer()
	{
		var wire = new PipeWire();
		var closes = 0;
		var transport = TelnetTransport.Pipe(wire.Server, () => { Interlocked.Increment(ref closes); return ValueTask.CompletedTask; });
		var connection = await ServerBuilder().StartAsync(transport);

		await connection.CloseAsync();
		await connection.CloseAsync();

		await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.That(closes).IsEqualTo(1);
		await Assert.That(connection.ClosedByServer).IsTrue();
		await Assert.That(connection.Closed.IsCancellationRequested).IsTrue();

		// A write after the close goes nowhere and does not throw.
		await connection.Interpreter.SendAsync(Encoding.ASCII.GetBytes("late"));
		await connection.DisposeAsync();
	}

	[Test]
	public async Task ThePeerLeavingEndsTheConnectionWithoutCountingAsAServerClose()
	{
		var wire = new PipeWire();
		var connection = await ServerBuilder().StartAsync(wire.Server);

		await wire.ToServer.Writer.CompleteAsync();

		await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.That(connection.ClosedByServer).IsFalse();
		await Assert.That(connection.Closed.IsCancellationRequested).IsTrue();
		await connection.CloseAsync(); // too late: does nothing
		await Assert.That(connection.ClosedByServer).IsFalse();
		await connection.DisposeAsync();
	}

	[Test]
	public async Task CancellingTheStartTokenEndsTheConnectionNormally()
	{
		var wire = new PipeWire();
		using var cts = new CancellationTokenSource();
		var connection = await ServerBuilder().StartAsync(wire.Server, cts.Token);

		await cts.CancelAsync();

		await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.That(connection.Completion.IsCompletedSuccessfully).IsTrue();
		await Assert.That(connection.Closed.IsCancellationRequested).IsTrue();
		await connection.DisposeAsync();
	}

	[Test]
	public async Task DisposeStopsReadingAndDisposesTheInterpreterButLeavesTheTransportOpen()
	{
		var wire = new PipeWire();
		var closes = 0;
		var transport = TelnetTransport.Pipe(wire.Server, () => { closes++; return ValueTask.CompletedTask; });
		var connection = await ServerBuilder().StartAsync(transport);
		var (interpreter, read) = connection;

		await connection.DisposeAsync();
		await connection.DisposeAsync();

		await Assert.That(read).IsSameReferenceAs(connection.Completion);
		await Assert.That(interpreter).IsSameReferenceAs(connection.Interpreter);
		await Assert.That(read.IsCompletedSuccessfully).IsTrue();
		await Assert.That(connection.Closed.IsCancellationRequested).IsTrue();
		await Assert.That(connection.ClosedByServer).IsFalse();
		await Assert.That(closes).IsEqualTo(0);
	}

	[Test]
	public async Task OneSessionClassRunsOverAPipe()
	{
		var wire = new PipeWire();
		var closes = 0;
		var session = new GmcpSession();
		var run = TelnetConnection.RunAsync(session,
			TelnetTransport.Pipe(wire.Server, () => { closes++; return ValueTask.CompletedTask; }));

		await wire.SendAsync([.. DoGmcp, .. GmcpHello]);

		await run.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.That(session.Hello).IsEqualTo("{\"client\":\"test\"}");
		await Assert.That(session.Connection!.ClosedByServer).IsTrue();
		await Assert.That(closes).IsEqualTo(1);
	}

	[Test]
	public async Task OneSessionClassRunsOverARegistryWebSocket()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Telnet);
		var session = new GmcpSession();
		var registry = new WebSocketSubprotocolRegistry().AddTelnetSession(() => session);
		var run = registry.RunAsync(serverSocket);

		await clientSocket.SendAsync(new byte[][] { DoGmcp, GmcpHello }.SelectMany(b => b).ToArray(),
			WebSocketMessageType.Binary, true, CancellationToken.None);

		// The session returns once it has its hello, and the socket is closed with 1000.
		var status = await ReceiveCloseAsync(clientSocket);
		await Assert.That(status).IsEqualTo(WebSocketCloseStatus.NormalClosure);
		await clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
		await run.WaitAsync(TimeSpan.FromSeconds(5));

		await Assert.That(session.Hello).IsEqualTo("{\"client\":\"test\"}");
		await Assert.That(session.Connection!.ClosedByServer).IsTrue();
	}

	[Test]
	public async Task ARegistrySessionEndsWhenThePeerCloses()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(null);
		TelnetConnection? seen = null;
		var sessionEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var registry = new WebSocketSubprotocolRegistry().AddTelnetSession(
			() => ServerBuilder(),
			async (connection, ct) =>
			{
				seen = connection;
				try { await Task.Delay(Timeout.Infinite, ct); }
				catch (OperationCanceledException) { }
				sessionEnded.SetResult(true);
			},
			acceptNoSubprotocol: true);
		var run = registry.RunAsync(serverSocket);

		await Assert.That(await PollUntilAsync(() => seen is not null)).IsTrue();
		await clientSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
		await run.WaitAsync(TimeSpan.FromSeconds(5));

		await Assert.That(sessionEnded.Task.IsCompleted).IsTrue();
		await Assert.That(seen!.ClosedByServer).IsFalse();
		await Assert.That(seen.Completion.IsCompletedSuccessfully).IsTrue();
	}

	[Test]
	public async Task AVanishedPeerFaultsARegistrySessionsCompletion()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Telnet);
		TelnetConnection? seen = null;
		var registry = new WebSocketSubprotocolRegistry().AddTelnetSession(
			() => ServerBuilder(),
			async (connection, _) =>
			{
				seen = connection;
				await connection.Completion;
			});
		var run = registry.RunAsync(serverSocket);

		await Assert.That(await PollUntilAsync(() => seen is not null)).IsTrue();
		// No close frame: the stream just ends, as when a player's network drops.
		clientSocket.Dispose();

		await Assert.ThrowsAsync<WebSocketException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
		await Assert.That(seen!.Completion.IsFaulted).IsTrue();
		await Assert.That(seen.Completion.Exception!.InnerException).IsTypeOf<WebSocketException>();
	}

	[Test]
	public async Task ARegistrySessionsFailureFaultsTheRun()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Telnet);
		var registry = new WebSocketSubprotocolRegistry().AddTelnetSession(
			() => ServerBuilder(),
			(_, _) => throw new InvalidOperationException("session failed"));
		var run = registry.RunAsync(serverSocket);

		var status = await ReceiveCloseAsync(clientSocket);
		await Assert.That(status).IsEqualTo(WebSocketCloseStatus.NormalClosure);
		await clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

		await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Test]
	public async Task CloseAsyncOnAWebSocketSendsANormalClose()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Telnet);
		var connection = await ServerBuilder().StartAsync(serverSocket);

		await connection.CloseAsync();

		var status = await ReceiveCloseAsync(clientSocket);
		await Assert.That(status).IsEqualTo(WebSocketCloseStatus.NormalClosure);
		await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.That(connection.ClosedByServer).IsTrue();
		await connection.DisposeAsync();
	}

	[Test]
	public async Task AnUnansweredCloseAbortsTheSocketAfterTheCloseTimeout()
	{
		var (serverSocket, clientSocket) = WebSocketPair.Create(MudWebSocketSubprotocols.Telnet);
		var socket = new MudWebSocketConnection(serverSocket) { CloseTimeout = TimeSpan.FromMilliseconds(200) };
		var connection = await ServerBuilder().StartAsync(TelnetTransport.WebSocket(socket));

		// The client never reads, so it never answers the close.
		await connection.CloseAsync();

		await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.That(connection.Completion.IsCompletedSuccessfully).IsTrue();
		await Assert.That(serverSocket.State).IsEqualTo(WebSocketState.Aborted);
		await connection.DisposeAsync();
		clientSocket.Dispose();
	}

	[Test]
	public async Task CancelPendingReadEndsThePipeReadLoopWithoutAnException()
	{
		var wire = new PipeWire();
		var connection = await ServerBuilder().StartAsync(wire.Server);

		wire.ToServer.Reader.CancelPendingRead();

		await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.That(connection.Completion.IsCompletedSuccessfully).IsTrue();
		await Assert.That(connection.ClosedByServer).IsFalse();
		await connection.DisposeAsync();
	}

	[Test]
	public async Task AWebSocketWithAnotherSubprotocolIsRefused()
	{
		var (serverSocket, _) = WebSocketPair.Create(MudWebSocketSubprotocols.Gmcp);
		var refused = await Assert.ThrowsAsync<ArgumentException>(() => ServerBuilder().StartAsync(serverSocket));
		await Assert.That(refused!.ParamName).IsEqualTo("socket");
	}

	/// <summary>A server session that waits for the client's GMCP hello, keeps it, and returns.</summary>
	private sealed class GmcpSession : ITelnetSession
	{
		private readonly TaskCompletionSource<string> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public string? Hello { get; private set; }

		public TelnetConnection? Connection { get; private set; }

		public TelnetInterpreterBuilder CreateBuilder() =>
			ServerBuilder()
				.AddPlugin<GMCPProtocol>()
					.OnGMCPMessage(m =>
					{
						if (m.Package == "Core.Hello") _hello.TrySetResult(m.Info);
						return ValueTask.CompletedTask;
					});

		public async Task RunAsync(TelnetConnection connection, CancellationToken cancellationToken)
		{
			Connection = connection;
			Hello = await _hello.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
		}
	}

	private static TelnetInterpreterBuilder ServerBuilder() =>
		new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback);

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

	/// <summary>A server-side duplex pipe and the two pipes behind it.</summary>
	private sealed class PipeWire
	{
		public Pipe ToServer { get; } = new();

		public Pipe ToClient { get; } = new();

		public IDuplexPipe Server => new Duplex(ToServer.Reader, ToClient.Writer);

		public async Task SendAsync(byte[] data) => await ToServer.Writer.WriteAsync(data);

		public async Task<byte[]> ReceiveAsync(Func<byte[], bool> until)
		{
			var all = new List<byte>();
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			while (!until([.. all]))
			{
				var result = await ToClient.Reader.ReadAsync(timeout.Token);
				foreach (var segment in result.Buffer) all.AddRange(segment.ToArray());
				ToClient.Reader.AdvanceTo(result.Buffer.End);
				if (result.IsCompleted) break;
			}

			return [.. all];
		}

		private sealed record Duplex(PipeReader Input, PipeWriter Output) : IDuplexPipe;
	}
}
