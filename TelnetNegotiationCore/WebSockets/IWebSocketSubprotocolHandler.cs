using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// Drives one WebSocket connection for one subprotocol. A new handler is made for every connection.
/// </summary>
/// <remarks>
/// This is the raw form: frames arrive as the socket delivers them, possibly as several chunks per message.
/// <see cref="WebSocketMessageHandler"/> reassembles whole messages and is the easier one to subclass.
/// </remarks>
public interface IWebSocketSubprotocolHandler
{
	/// <summary>
	/// Called once, before the first frame is read. Sending from here is allowed. The receive loop waits for it,
	/// so long-running work belongs on its own task.
	/// </summary>
	ValueTask OnOpenAsync(MudWebSocketConnection connection, CancellationToken cancellationToken);

	/// <summary>
	/// Called for each received chunk of a TEXT or BINARY message. <paramref name="endOfMessage"/> marks the last
	/// chunk of a message. <paramref name="data"/> is only valid until this call returns.
	/// </summary>
	ValueTask OnFrameAsync(
		MudWebSocketConnection connection,
		WebSocketMessageType messageType,
		ReadOnlyMemory<byte> data,
		bool endOfMessage,
		CancellationToken cancellationToken);

	/// <summary>
	/// Called once when the receive loop ends, however it ended. <paramref name="status"/> is the peer's close status,
	/// or <see langword="null"/> when the loop ended without a close frame from the peer (cancellation, failure).
	/// </summary>
	ValueTask OnClosedAsync(MudWebSocketConnection connection, WebSocketCloseStatus? status, string? description);
}
