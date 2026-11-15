using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// One open WebSocket, with sends serialized and a receive loop that hands frames to an
/// <see cref="IWebSocketSubprotocolHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="WebSocket"/> allows one send and one receive at a time. Every send here, close frames
/// included, waits on one lock, so a handler may send from any thread: a GMCP update from a game loop
/// while the receive loop answers a close, for one.
/// </para>
/// <para>
/// The connection does not own the socket. Accepting it, and disposing it after
/// <see cref="RunAsync"/> completes, stay with the caller.
/// </para>
/// </remarks>
public sealed class MudWebSocketConnection
{
	/// <summary>The receive buffer size. Frames larger than this arrive as several chunks.</summary>
	public const int ReceiveBufferSize = 4 * 1024;

	/// <summary>How long <see cref="CloseTimeout"/> is unless set: five seconds.</summary>
	public static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(5);

	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private bool _closeSent;
	private volatile bool _abortedUnanswered;
	private volatile Exception? _failure;

	/// <summary>
	/// Wraps an open socket.
	/// </summary>
	/// <param name="socket">An open WebSocket, server or client side.</param>
	public MudWebSocketConnection(WebSocket socket)
	{
		Socket = socket ?? throw new ArgumentNullException(nameof(socket));
	}

	/// <summary>The wrapped socket.</summary>
	public WebSocket Socket { get; }

	/// <summary>
	/// How long to wait for the peer to answer a close this side started. A peer that has not answered by then has
	/// its socket aborted, and <see cref="RunAsync"/> completes. <see cref="Timeout.InfiniteTimeSpan"/> waits forever.
	/// </summary>
	/// <remarks>
	/// RFC 6455 has the closing side wait for the peer's close frame, but a peer that has gone silent never sends
	/// one, and without a limit the receive loop, and whatever awaits it, would wait with it.
	/// </remarks>
	public TimeSpan CloseTimeout { get; init; } = DefaultCloseTimeout;

	/// <summary>The subprotocol the handshake settled on, or <see langword="null"/> when none was.</summary>
	public string? Subprotocol => Socket.SubProtocol;

	/// <summary>
	/// Whether the socket can still send: it is open, or the peer has closed and the close has not been answered yet.
	/// </summary>
	public bool CanSend => !_closeSent && Socket.State is WebSocketState.Open or WebSocketState.CloseReceived;

	/// <summary>
	/// Sends <paramref name="data"/> as one BINARY message.
	/// </summary>
	/// <remarks>
	/// Does nothing once the socket can no longer send (see <see cref="CanSend"/>). A telnet interpreter, for one,
	/// may still write while it is shut down after the peer has gone, and that is not an error.
	/// </remarks>
	public ValueTask SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
		SendAsync(data, WebSocketMessageType.Binary, cancellationToken);

	/// <summary>
	/// Sends <paramref name="utf8"/>, which must be UTF-8, as one TEXT message.
	/// </summary>
	public ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken = default) =>
		SendAsync(utf8, WebSocketMessageType.Text, cancellationToken);

	/// <summary>
	/// Sends <paramref name="text"/> as one TEXT message.
	/// </summary>
	public ValueTask SendTextAsync(string text, CancellationToken cancellationToken = default) =>
		SendAsync(Encoding.UTF8.GetBytes(text ?? throw new ArgumentNullException(nameof(text))),
			WebSocketMessageType.Text, cancellationToken);

	/// <summary>
	/// Starts or completes the close handshake. A second call, or a call after the socket closed, does nothing.
	/// </summary>
	/// <remarks>
	/// <see cref="RunAsync"/> keeps receiving until the peer answers, then completes. A description longer than
	/// RFC 6455 allows (123 UTF-8 bytes) is cut short rather than rejected.
	/// </remarks>
	public async ValueTask CloseAsync(
		WebSocketCloseStatus status,
		string? description = null,
		CancellationToken cancellationToken = default)
	{
		await _sendLock.WaitAsync(cancellationToken);
		try
		{
			if (!CanSend) return;
			await Socket.CloseOutputAsync(status, LimitCloseDescription(description), cancellationToken);
			// Only once it went out: a close that failed leaves the socket usable, and a later close can still be sent.
			_closeSent = true;
			if (CloseTimeout != Timeout.InfiniteTimeSpan) _ = AbortIfUnansweredAsync();
		}
		finally
		{
			_sendLock.Release();
		}
	}

	private async Task AbortIfUnansweredAsync()
	{
		await Task.Delay(CloseTimeout).ConfigureAwait(false);
		if (Socket.State != WebSocketState.CloseSent) return;

		_abortedUnanswered = true;
		Socket.Abort();
	}

	/// <summary>
	/// What ended <see cref="RunAsync"/> with an error, once it has: a socket that failed, or a handler that threw.
	/// Set before <see cref="IWebSocketSubprotocolHandler.OnClosedAsync"/> is called.
	/// </summary>
	internal Exception? Failure => _failure;

	private bool RecordFailure(Exception exception)
	{
		_failure = exception;
		return false;
	}

	/// <summary>The most UTF-8 bytes a close reason may take: a control frame carries 125, two of them the status.</summary>
	public const int MaxCloseDescriptionBytes = 123;

	private static string? LimitCloseDescription(string? description)
	{
		if (description is null || Encoding.UTF8.GetByteCount(description) <= MaxCloseDescriptionBytes) return description;

		// Cut on a character boundary, never inside a surrogate pair. Each step adds at least one byte, so this
		// looks at no more than MaxCloseDescriptionBytes characters however long the description is.
		int length = 0, bytes = 0;
		while (length < description.Length)
		{
			var c = description[length];
			int chars = 1, size;
			if (char.IsHighSurrogate(c) && length + 1 < description.Length && char.IsLowSurrogate(description[length + 1]))
			{
				chars = 2;
				size = 4;
			}
			else
			{
				// A lone surrogate encodes as U+FFFD, three bytes.
				size = c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
			}

			if (bytes + size > MaxCloseDescriptionBytes) break;
			bytes += size;
			length += chars;
		}

		return description.Substring(0, length);
	}

	private async ValueTask SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken cancellationToken)
	{
		await _sendLock.WaitAsync(cancellationToken);
		try
		{
			if (!CanSend) return;
#if NETSTANDARD2_0
			if (!MemoryMarshal.TryGetArray(data, out var segment) || segment.Array is null)
			{
				segment = new ArraySegment<byte>(data.ToArray());
			}
			await Socket.SendAsync(segment, type, endOfMessage: true, cancellationToken);
#else
			await Socket.SendAsync(data, type, endOfMessage: true, cancellationToken);
#endif
		}
		finally
		{
			_sendLock.Release();
		}
	}

	/// <summary>
	/// Runs <paramref name="handler"/> on this connection until the socket closes or the token is cancelled.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Calls <see cref="IWebSocketSubprotocolHandler.OnOpenAsync"/>, then hands every received chunk to
	/// <see cref="IWebSocketSubprotocolHandler.OnFrameAsync"/>, then <see cref="IWebSocketSubprotocolHandler.OnClosedAsync"/>
	/// however the loop ended. A close from the peer is answered. The task completes normally on a close
	/// handshake or cancellation, and faults when the socket fails (a peer that vanished without a close frame).
	/// </para>
	/// <para>
	/// The chunk passed to <c>OnFrameAsync</c> is only valid for the duration of that call.
	/// </para>
	/// </remarks>
	public async Task RunAsync(IWebSocketSubprotocolHandler handler, CancellationToken cancellationToken = default)
	{
		if (handler is null) throw new ArgumentNullException(nameof(handler));

		var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
		WebSocketCloseStatus? closeStatus = null;
		string? closeDescription = null;
		try
		{
			await handler.OnOpenAsync(this, cancellationToken);

			while (!cancellationToken.IsCancellationRequested
			       && Socket.State is WebSocketState.Open or WebSocketState.CloseSent)
			{
				WebSocketReceiveResult result;
				try
				{
					result = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception) when (_abortedUnanswered)
				{
					// Our close went unanswered for CloseTimeout: the end of the connection, not a failure.
					break;
				}

				if (result.MessageType == WebSocketMessageType.Close)
				{
					closeStatus = result.CloseStatus;
					closeDescription = result.CloseStatusDescription;
					// Echo the peer's status, as RFC 6455 section 5.5.1 suggests. Does nothing if we started the close.
					// Cancellable: a send stuck holding the lock must not keep the loop, and OnClosedAsync, from finishing.
					try
					{
						await CloseAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, null, cancellationToken);
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
					{
					}

					break;
				}

				await handler.OnFrameAsync(
					this,
					result.MessageType,
					new ReadOnlyMemory<byte>(buffer, 0, result.Count),
					result.EndOfMessage,
					cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Cancelled while a handler was opening or handling a frame: the same as cancelling the receive.
		}
		catch (Exception exception) when (RecordFailure(exception))
		{
			// Never entered: the filter records the failure for OnClosedAsync and lets it propagate.
			throw;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			await handler.OnClosedAsync(this, closeStatus, closeDescription);
		}
	}
}
