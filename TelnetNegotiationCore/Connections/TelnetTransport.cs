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
	/// <exception cref="ArgumentException">The socket negotiated a subprotocol that is not a telnet stream.</exception>
	public static ITelnetTransport WebSocket(WebSocket socket)
	{
		if (socket is null) throw new ArgumentNullException(nameof(socket));
		var base64 = IsBase64(socket.SubProtocol, nameof(socket));
		return new WebSocketTransport(new MudWebSocketConnection(socket), base64);
	}

	/// <inheritdoc cref="WebSocket(System.Net.WebSockets.WebSocket)"/>
	public static ITelnetTransport WebSocket(MudWebSocketConnection connection)
	{
		if (connection is null) throw new ArgumentNullException(nameof(connection));
		return new WebSocketTransport(connection, IsBase64(connection.Subprotocol, nameof(connection)));
	}

	/// <summary>
	/// Whether <paramref name="subprotocol"/> is the Base64 telnet stream; throws when it is no telnet stream at all.
	/// </summary>
	internal static bool IsBase64(string? subprotocol, string parameterName)
	{
		if (string.Equals(subprotocol, MudWebSocketSubprotocols.TelnetBase64, StringComparison.OrdinalIgnoreCase)) return true;
		if (string.IsNullOrEmpty(subprotocol) || MudWebSocketSubprotocols.IsTelnet(subprotocol)) return false;
		throw new ArgumentException(
			$"The socket negotiated '{subprotocol}', which is not a telnet stream. Use a WebSocketSubprotocolRegistry to serve other subprotocols.",
			parameterName);
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
