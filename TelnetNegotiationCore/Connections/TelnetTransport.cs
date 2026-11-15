using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.WebSockets;

namespace TelnetNegotiationCore.Connections;

/// <summary>
/// The built-in <see cref="ITelnetTransport"/>s.
/// </summary>
public static class TelnetTransport
{
	/// <summary>
	/// A duplex pipe, such as Kestrel's <c>ConnectionContext.Transport</c>.
	/// </summary>
	/// <param name="pipe">The pipe. It stays the caller's.</param>
	/// <param name="close">
	/// How this side ends the connection. Leave it out to complete the pipe's output; pass
	/// <c>() =&gt; { context.Abort(); return default; }</c> to drop a Kestrel connection at once.
	/// </param>
	public static ITelnetTransport Pipe(IDuplexPipe pipe, Func<ValueTask>? close = null) =>
		new PipeTransport(pipe ?? throw new ArgumentNullException(nameof(pipe)), close);

	/// <summary>
	/// A stream, such as a <see cref="NetworkStream"/> or an <c>SslStream</c>.
	/// </summary>
	/// <param name="stream">The stream. Reading never disposes it.</param>
	/// <param name="close">How this side ends the connection. Leave it out to dispose the stream.</param>
	public static ITelnetTransport Stream(Stream stream, Func<ValueTask>? close = null) =>
		new StreamTransport(stream ?? throw new ArgumentNullException(nameof(stream)), close);

	/// <summary>
	/// A connected <see cref="TcpClient"/>. Closing the connection closes the client.
	/// </summary>
	public static ITelnetTransport Tcp(TcpClient client)
	{
		if (client is null) throw new ArgumentNullException(nameof(client));
		return new StreamTransport(client.GetStream(), () =>
		{
			client.Close();
			return default;
		});
	}

	/// <summary>
	/// An open WebSocket speaking <c>telnet.mudstandards.org</c>, its alias <c>telnet</c>, or
	/// <c>telnetb64.mudstandards.org</c>. A socket with no subprotocol is taken as plain telnet.
	/// </summary>
	/// <remarks>A client that may also have been given <c>gmcp</c> or <c>terminal</c> uses <see cref="WebSocketClient(WebSocket)"/>.</remarks>
	/// <exception cref="ArgumentException">The socket negotiated a subprotocol that is not a telnet stream.</exception>
	public static ITelnetTransport WebSocket(WebSocket socket)
	{
		if (socket is null) throw new ArgumentNullException(nameof(socket));
		return Telnet(new MudWebSocketConnection(socket), nameof(socket));
	}

	/// <inheritdoc cref="WebSocket(System.Net.WebSockets.WebSocket)"/>
	public static ITelnetTransport WebSocket(MudWebSocketConnection connection)
	{
		if (connection is null) throw new ArgumentNullException(nameof(connection));
		return Telnet(connection, nameof(connection));
	}

	/// <summary>
	/// A client's WebSocket to a game: any stream <see cref="WebSocket(WebSocket)"/> speaks, and also
	/// <c>gmcp.mudstandards.org</c> and <c>terminal.mudstandards.org</c>, which carry no telnet.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A client interpreter runs over <c>gmcp</c> and <c>terminal</c> unchanged: the transport turns the server's
	/// frames into a telnet stream and takes telnet back out of every write, so no negotiation byte reaches a server
	/// that would read it as text in front of the player's next line. On <c>gmcp</c> the connection opens as if the
	/// server had sent <c>WILL GMCP</c>, and GMCP goes both ways in TEXT frames. Nothing else is negotiated: no NAWS,
	/// TTYPE, CHARSET, echo control or prompt marking, and the text is UTF-8.
	/// </para>
	/// <para>
	/// <see cref="Builders.TelnetInterpreterBuilder.StartAsync(WebSocket, CancellationToken)"/> uses this for a
	/// client interpreter. A server answering these subprotocols registers
	/// <see cref="GmcpWebSocketHandler"/> or <see cref="TerminalWebSocketHandler"/> instead.
	/// </para>
	/// </remarks>
	/// <exception cref="ArgumentException">The socket negotiated a subprotocol this transport does not speak.</exception>
	public static ITelnetTransport WebSocketClient(WebSocket socket)
	{
		if (socket is null) throw new ArgumentNullException(nameof(socket));
		return WebSocketClient(new MudWebSocketConnection(socket), nameof(socket));
	}

	/// <inheritdoc cref="WebSocketClient(System.Net.WebSockets.WebSocket)"/>
	public static ITelnetTransport WebSocketClient(MudWebSocketConnection connection)
	{
		if (connection is null) throw new ArgumentNullException(nameof(connection));
		return WebSocketClient(connection, nameof(connection));
	}

	private static ITelnetTransport WebSocketClient(MudWebSocketConnection connection, string parameterName) =>
		KindOf(connection.Subprotocol, parameterName) switch
		{
			WebSocketStream.Gmcp => new BridgeTransport(new TelnetBridge(connection, gmcp: true)),
			WebSocketStream.Terminal => new BridgeTransport(new TelnetBridge(connection, gmcp: false)),
			var telnet => new WebSocketTransport(connection, base64: telnet == WebSocketStream.TelnetBase64),
		};

	private static ITelnetTransport Telnet(MudWebSocketConnection connection, string parameterName)
	{
		var kind = KindOf(connection.Subprotocol, parameterName);
		if (kind is WebSocketStream.Telnet or WebSocketStream.TelnetBase64)
			return new WebSocketTransport(connection, base64: kind == WebSocketStream.TelnetBase64);
		throw new ArgumentException(
			$"The socket negotiated '{connection.Subprotocol}', which is not a telnet stream. Use a WebSocketSubprotocolRegistry to serve other subprotocols.",
			parameterName);
	}

	/// <summary>
	/// What <paramref name="subprotocol"/> carries; throws when it is none of the streams <see cref="WebSocketClient(MudWebSocketConnection)"/> speaks.
	/// </summary>
	internal static WebSocketStream KindOf(string? subprotocol, string parameterName)
	{
		if (string.IsNullOrEmpty(subprotocol) || MudWebSocketSubprotocols.IsTelnet(subprotocol)) return WebSocketStream.Telnet;
		if (Is(subprotocol, MudWebSocketSubprotocols.TelnetBase64)) return WebSocketStream.TelnetBase64;
		if (Is(subprotocol, MudWebSocketSubprotocols.Gmcp)) return WebSocketStream.Gmcp;
		if (Is(subprotocol, MudWebSocketSubprotocols.Terminal)) return WebSocketStream.Terminal;
		throw new ArgumentException(
			$"The socket negotiated '{subprotocol}', which no TNC transport speaks. Use a WebSocketSubprotocolRegistry to serve other subprotocols.",
			parameterName);

		static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
	}

	internal enum WebSocketStream
	{
		Telnet,
		TelnetBase64,
		Gmcp,
		Terminal,
	}

	private sealed class PipeTransport(IDuplexPipe pipe, Func<ValueTask>? close) : ITelnetTransport
	{
		public Task ReadAsync(Func<ReadOnlyMemory<byte>, ValueTask> onData, CancellationToken cancellationToken) =>
			ReadPipeAsync(pipe.Input, onData, cancellationToken);

		// Uncancellable, as the pipe writes have always been: a flush the peer holds ends with the connection.
		public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
			await pipe.Output.WriteAsync(data);

		public ValueTask CloseAsync(CancellationToken cancellationToken) =>
			close is not null ? close() : pipe.Output.CompleteAsync();
	}

	private sealed class StreamTransport(Stream stream, Func<ValueTask>? close) : ITelnetTransport
	{
		public async Task ReadAsync(Func<ReadOnlyMemory<byte>, ValueTask> onData, CancellationToken cancellationToken)
		{
			// The adapter is this transport's; the stream stays the caller's.
			var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
			Exception? readFailure = null;
			try
			{
				await ReadPipeAsync(reader, onData, cancellationToken);
			}
			catch (Exception ex)
			{
				readFailure = ex;
			}

			try
			{
				await reader.CompleteAsync(readFailure);
			}
			catch (Exception cleanupFailure) when (readFailure is not null)
			{
				throw new AggregateException(
					"Reading the transport failed, and releasing its pipe adapter also failed.",
					readFailure, cleanupFailure);
			}

			if (readFailure is not null) ExceptionDispatchInfo.Capture(readFailure).Throw();
		}

		public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
		{
			// Uncancellable, as the stream writes have always been.
			if (MemoryMarshal.TryGetArray(data, out var segment) && segment.Array is not null)
			{
				await stream.WriteAsync(segment.Array, segment.Offset, segment.Count);
				return;
			}

			var copy = data.ToArray();
			await stream.WriteAsync(copy, 0, copy.Length);
		}

		public ValueTask CloseAsync(CancellationToken cancellationToken)
		{
			if (close is not null) return close();
			stream.Dispose();
			return default;
		}
	}

	private sealed class WebSocketTransport(MudWebSocketConnection connection, bool base64) : ITelnetTransport
	{
		public Task ReadAsync(Func<ReadOnlyMemory<byte>, ValueTask> onData, CancellationToken cancellationToken) =>
			connection.RunAsync(new TelnetWebSocketHandler(onData, base64), cancellationToken);

		public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
			TelnetWebSocketHandler.WriteAsync(connection, base64, data, cancellationToken);

		public ValueTask CloseAsync(CancellationToken cancellationToken) =>
			connection.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
	}

	private sealed class BridgeTransport(TelnetBridge bridge) : ITelnetTransport
	{
		public Task ReadAsync(Func<ReadOnlyMemory<byte>, ValueTask> onData, CancellationToken cancellationToken) =>
			bridge.ReadAsync(onData, cancellationToken);

		public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
			bridge.WriteAsync(data, cancellationToken);

		public ValueTask CloseAsync(CancellationToken cancellationToken) => bridge.CloseAsync(cancellationToken);
	}

	/// <summary>The read loop every pipe-shaped transport shares.</summary>
	internal static async Task ReadPipeAsync(
		PipeReader reader, Func<ReadOnlyMemory<byte>, ValueTask> onData, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			var result = await reader.ReadAtLeastAsync(1, cancellationToken);
			if (result.IsCanceled)
			{
				// CancelPendingRead: stop reading without an exception, leaving the buffer for a later reader.
				reader.AdvanceTo(result.Buffer.Start);
				break;
			}

			foreach (var segment in result.Buffer)
			{
				await onData(segment);
			}

			reader.AdvanceTo(result.Buffer.End);

			if (result.IsCompleted)
				break;
		}
	}
}
