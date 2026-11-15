using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// A subprotocol handler that receives whole messages. Subclass it and override the message methods you need.
/// </summary>
/// <remarks>
/// <para>
/// Chunks are collected until the last one of a message, up to <see cref="MaxMessageSize"/> bytes; a larger
/// message closes the socket with 1009 (message too big). A message type the subclass does not override closes
/// the socket with 1003 (unsupported data). TEXT messages that are not valid UTF-8 close it with 1007.
/// </para>
/// <example>
/// A subprotocol that carries serialized rich text in TEXT frames:
/// <code>
/// sealed class MarkupHandler(Player player) : WebSocketMessageHandler
/// {
///     protected override ValueTask OnOpenAsync(CancellationToken ct) { player.Attach(this); return default; }
///     protected override ValueTask OnTextMessageAsync(string text, CancellationToken ct) => player.InputAsync(text);
///     public ValueTask WriteAsync(string json) => Connection.SendTextAsync(json);
/// }
/// registry.Add("markup.v1.example.org", () => new MarkupHandler(new Player()));
/// </code>
/// </example>
/// </remarks>
public abstract class WebSocketMessageHandler : IWebSocketSubprotocolHandler
{
	/// <summary>The default for <see cref="MaxMessageSize"/>: 1 MiB.</summary>
	public const int DefaultMaxMessageSize = 1024 * 1024;

	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	private readonly MemoryStream _message = new();
	private MudWebSocketConnection? _connection;
	private bool _discarding;

	/// <summary>The largest message accepted, in bytes.</summary>
	public int MaxMessageSize { get; init; } = DefaultMaxMessageSize;

	/// <summary>
	/// The connection this handler runs on. Set before <see cref="OnOpenAsync(CancellationToken)"/> is called.
	/// </summary>
	/// <exception cref="InvalidOperationException">The handler has not been started yet.</exception>
	protected MudWebSocketConnection Connection =>
		_connection ?? throw new InvalidOperationException("The handler has not been started on a connection.");

	/// <summary>Called once before the first message. See <see cref="IWebSocketSubprotocolHandler.OnOpenAsync"/>.</summary>
	protected virtual ValueTask OnOpenAsync(CancellationToken cancellationToken) => default;

	/// <summary>Called for each complete TEXT message. The default closes the socket with 1003.</summary>
	protected virtual ValueTask OnTextMessageAsync(string text, CancellationToken cancellationToken) =>
		Connection.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "TEXT frames are not used by this subprotocol", cancellationToken);

	/// <summary>
	/// Called for each complete BINARY message. <paramref name="data"/> is only valid until this call returns.
	/// The default closes the socket with 1003.
	/// </summary>
	protected virtual ValueTask OnBinaryMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
		Connection.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "BINARY frames are not used by this subprotocol", cancellationToken);

	/// <summary>Called once when the connection ends. See <see cref="IWebSocketSubprotocolHandler.OnClosedAsync"/>.</summary>
	protected virtual ValueTask OnClosedAsync(WebSocketCloseStatus? status, string? description) => default;

	ValueTask IWebSocketSubprotocolHandler.OnOpenAsync(MudWebSocketConnection connection, CancellationToken cancellationToken)
	{
		_connection = connection ?? throw new ArgumentNullException(nameof(connection));
		return OnOpenAsync(cancellationToken);
	}

	async ValueTask IWebSocketSubprotocolHandler.OnFrameAsync(
		MudWebSocketConnection connection,
		WebSocketMessageType messageType,
		ReadOnlyMemory<byte> data,
		bool endOfMessage,
		CancellationToken cancellationToken)
	{
		if (!_discarding)
		{
			if (_message.Length + data.Length > MaxMessageSize)
			{
				_discarding = true;
				_message.SetLength(0);
				await connection.CloseAsync(WebSocketCloseStatus.MessageTooBig,
					$"Messages are limited to {MaxMessageSize} bytes", cancellationToken);
			}
			else
			{
				Append(data);
			}
		}

		if (!endOfMessage) return;

		if (_discarding)
		{
			_discarding = false;
			return;
		}

		var length = (int)_message.Length;
		var bytes = _message.GetBuffer();
		try
		{
			if (messageType == WebSocketMessageType.Text)
			{
				string text;
				try
				{
					text = StrictUtf8.GetString(bytes, 0, length);
				}
				catch (DecoderFallbackException)
				{
					await connection.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "TEXT frames must be UTF-8", cancellationToken);
					return;
				}

				await OnTextMessageAsync(text, cancellationToken);
			}
			else
			{
				await OnBinaryMessageAsync(new ReadOnlyMemory<byte>(bytes, 0, length), cancellationToken);
			}
		}
		finally
		{
			_message.SetLength(0);
		}
	}

	ValueTask IWebSocketSubprotocolHandler.OnClosedAsync(MudWebSocketConnection connection, WebSocketCloseStatus? status, string? description) =>
		OnClosedAsync(status, description);

	private void Append(ReadOnlyMemory<byte> data)
	{
#if NETSTANDARD2_0
		if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out var segment) && segment.Array is not null)
		{
			_message.Write(segment.Array, segment.Offset, segment.Count);
		}
		else
		{
			var copy = data.ToArray();
			_message.Write(copy, 0, copy.Length);
		}
#else
		_message.Write(data.Span);
#endif
	}
}
