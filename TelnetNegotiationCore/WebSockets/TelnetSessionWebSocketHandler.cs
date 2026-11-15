using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Connections;

namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// Runs an <see cref="ITelnetSession"/> on a WebSocket that a <see cref="WebSocketSubprotocolRegistry"/> drives:
/// the same session object a TCP connection runs, through <see cref="TelnetConnection.RunAsync(ITelnetSession, ITelnetTransport, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// The registry owns the receive loop, so frames are pushed into the connection here rather than read by it. The
/// session starts once the handler has opened and runs beside the loop; when it returns the socket is closed, and
/// once the socket has closed the session is awaited and the connection disposed.
/// </remarks>
internal sealed class TelnetSessionWebSocketHandler(Func<ITelnetSession> sessionFactory, bool base64)
	: IWebSocketSubprotocolHandler
{
	private FrameTransport? _transport;
	private TelnetWebSocketHandler? _decoder;
	private TelnetConnection? _connection;
	private Task? _session;

	public async ValueTask OnOpenAsync(MudWebSocketConnection connection, CancellationToken cancellationToken)
	{
		var session = sessionFactory() ?? throw new InvalidOperationException("The session factory returned null.");
		var builder = session.CreateBuilder() ?? throw new InvalidOperationException("The session returned no builder.");

		_transport = new FrameTransport(connection, base64);
		_connection = await builder.StartAsync(_transport, cancellationToken);
		_decoder = new TelnetWebSocketHandler(_transport.FeedAsync, base64);
		_session = RunSessionAsync(session, _connection);
	}

	private static async Task RunSessionAsync(ITelnetSession session, TelnetConnection connection)
	{
		// Let OnOpenAsync return first, so the receive loop is running before the session waits on it.
		await Task.Yield();
		try
		{
			await session.RunAsync(connection, connection.Closed);
		}
		finally
		{
			await connection.CloseIfOpenAsync();
		}
	}

	public ValueTask OnFrameAsync(
		MudWebSocketConnection connection,
		WebSocketMessageType messageType,
		ReadOnlyMemory<byte> data,
		bool endOfMessage,
		CancellationToken cancellationToken) =>
		_decoder is null
			? throw new InvalidOperationException("The connection has not opened.")
			: _decoder.OnFrameAsync(connection, messageType, data, endOfMessage, cancellationToken);

	public async ValueTask OnClosedAsync(MudWebSocketConnection connection, WebSocketCloseStatus? status, string? description)
	{
		_transport?.Complete();
		if (_connection is null) return;

		try
		{
			if (_session is not null) await _session;
		}
		finally
		{
			await _connection.DisposeAsync();
		}
	}

	/// <summary>A transport whose input is pushed by the registry's receive loop.</summary>
	private sealed class FrameTransport(MudWebSocketConnection connection, bool base64) : ITelnetTransport
	{
		private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private Func<ReadOnlyMemory<byte>, ValueTask>? _onData;

		public Task ReadAsync(Func<ReadOnlyMemory<byte>, ValueTask> onData, CancellationToken cancellationToken)
		{
			_onData = onData;
			// A close or dispose from this side ends the read; the socket's own close arrives through Complete.
			cancellationToken.Register(() => _ended.TrySetResult(true));
			return _ended.Task;
		}

		public ValueTask FeedAsync(ReadOnlyMemory<byte> data) => _onData?.Invoke(data) ?? default;

		public void Complete() => _ended.TrySetResult(true);

		public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
			TelnetWebSocketHandler.WriteAsync(connection, base64, data, cancellationToken);

		public ValueTask CloseAsync(CancellationToken cancellationToken) =>
			connection.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
	}
}
