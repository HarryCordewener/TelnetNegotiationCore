using System;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Connections;
using TelnetNegotiationCore.Interpreters;

namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// Runs a <see cref="TelnetInterpreter"/> over a WebSocket: <c>telnet.mudstandards.org</c> (and <c>telnet</c>),
/// or <c>telnetb64.mudstandards.org</c> when <see cref="Base64"/> is set.
/// </summary>
/// <remarks>
/// <para>
/// The interpreter is the same one a TCP connection gets: every plugin, GMCP, MSDP, MCCP and the rest, work
/// unchanged. Telnet is a byte stream, so frame boundaries carry no meaning: received chunks go straight to
/// <see cref="TelnetInterpreter.InterpretByteArrayAsync"/>, and each interpreter write goes out as one message.
/// </para>
/// <para>
/// A frame of the wrong type (TEXT on <c>telnet.mudstandards.org</c>, BINARY on <c>telnetb64</c>) closes the
/// socket with 1003.
/// </para>
/// </remarks>
public sealed class TelnetWebSocketHandler : IWebSocketSubprotocolHandler
{
	private readonly Func<TelnetInterpreterBuilder>? _builderFactory;
	private readonly Func<TelnetInterpreter, MudWebSocketConnection, ValueTask>? _onConnected;
	private readonly bool _ownsInterpreter;
	private readonly MemoryStream? _base64Message;
	private Func<ReadOnlyMemory<byte>, ValueTask>? _onData;
	private bool _discardingBase64Message;

	/// <summary>
	/// A handler that builds its own interpreter when the connection opens, and disposes it when the connection ends.
	/// </summary>
	/// <param name="builderFactory">
	/// Returns a configured builder (mode, logger, plugins, callbacks) for one connection. Leave the negotiation
	/// callback unset; the handler wires it to the socket.
	/// </param>
	/// <param name="onConnected">
	/// Called once the interpreter is built, before the first frame is read: the place to keep a reference to it.
	/// </param>
	/// <param name="base64">Speak <c>telnetb64.mudstandards.org</c> instead of the plain telnet stream.</param>
	public TelnetWebSocketHandler(
		Func<TelnetInterpreterBuilder> builderFactory,
		Func<TelnetInterpreter, MudWebSocketConnection, ValueTask>? onConnected = null,
		bool base64 = false)
	{
		_builderFactory = builderFactory ?? throw new ArgumentNullException(nameof(builderFactory));
		_onConnected = onConnected;
		_ownsInterpreter = true;
		Base64 = base64;
		_base64Message = base64 ? new MemoryStream() : null;
	}

	/// <summary>
	/// A handler that only decodes frames, handing the telnet bytes to <paramref name="onData"/>: the read side of
	/// <see cref="Connections.TelnetTransport.WebSocket(MudWebSocketConnection)"/>.
	/// </summary>
	internal TelnetWebSocketHandler(Func<ReadOnlyMemory<byte>, ValueTask> onData, bool base64)
	{
		_onData = onData;
		Base64 = base64;
		_base64Message = base64 ? new MemoryStream() : null;
	}

	/// <summary>Whether the stream is Base64 in TEXT frames (<c>telnetb64.mudstandards.org</c>).</summary>
	public bool Base64 { get; }

	/// <summary>The interpreter, once the connection has opened. Null on a handler that only decodes frames.</summary>
	public TelnetInterpreter? Interpreter { get; private set; }

	/// <summary>
	/// The write callback for an interpreter on <paramref name="connection"/>: one BINARY message per write, or one
	/// Base64 TEXT message when <paramref name="base64"/> is set.
	/// </summary>
	/// <remarks>
	/// <paramref name="cancellationToken"/> is the connection's lifetime: once it is cancelled, a write waiting on the
	/// socket gives up rather than holding the connection open.
	/// </remarks>
	internal static Func<ReadOnlyMemory<byte>, ValueTask> Writer(
		MudWebSocketConnection connection, bool base64, CancellationToken cancellationToken) =>
		data => WriteAsync(connection, base64, data, cancellationToken);

	/// <summary>Sends one interpreter write: a BINARY message, or a Base64 TEXT message.</summary>
	internal static ValueTask WriteAsync(
		MudWebSocketConnection connection, bool base64, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
		base64
			? connection.SendTextAsync(ToBase64(data), cancellationToken)
			: connection.SendBinaryAsync(data, cancellationToken);

	/// <inheritdoc/>
	public async ValueTask OnOpenAsync(MudWebSocketConnection connection, CancellationToken cancellationToken)
	{
		if (_builderFactory is null) return;

		var builder = _builderFactory() ?? throw new InvalidOperationException("The builder factory returned null.");
		Interpreter = await builder.OnNegotiation(Writer(connection, Base64, cancellationToken)).BuildAsync();
		_onData = builder.InputFeed(Interpreter);
		if (_onConnected is not null) await _onConnected(Interpreter, connection);
	}

	/// <inheritdoc/>
	public async ValueTask OnFrameAsync(
		MudWebSocketConnection connection,
		WebSocketMessageType messageType,
		ReadOnlyMemory<byte> data,
		bool endOfMessage,
		CancellationToken cancellationToken)
	{
		var onData = _onData ?? throw new InvalidOperationException("The connection has not opened.");

		if (!Base64)
		{
			if (messageType != WebSocketMessageType.Binary)
			{
				await connection.CloseAsync(WebSocketCloseStatus.InvalidMessageType,
					$"{MudWebSocketSubprotocols.Telnet} carries BINARY frames only", cancellationToken);
				return;
			}

			await onData(data);
			return;
		}

		if (messageType != WebSocketMessageType.Text)
		{
			await connection.CloseAsync(WebSocketCloseStatus.InvalidMessageType,
				$"{MudWebSocketSubprotocols.TelnetBase64} carries TEXT frames only", cancellationToken);
			return;
		}

		// Base64 is decoded per message: a chunk boundary can fall inside a four-character group.
		var message = _base64Message!;
		if (_discardingBase64Message)
		{
			// The rest of a rejected message, final chunk included, never reaches the interpreter.
			if (endOfMessage) _discardingBase64Message = false;
			return;
		}

		if (message.Length + data.Length > WebSocketMessageHandler.DefaultMaxMessageSize)
		{
			message.SetLength(0);
			_discardingBase64Message = !endOfMessage;
			await connection.CloseAsync(WebSocketCloseStatus.MessageTooBig,
				$"Messages are limited to {WebSocketMessageHandler.DefaultMaxMessageSize} bytes", cancellationToken);
			return;
		}

		var chunk = data.ToArray();
		message.Write(chunk, 0, chunk.Length);
		if (!endOfMessage) return;

		byte[] decoded;
		try
		{
			decoded = Convert.FromBase64String(Encoding.ASCII.GetString(message.GetBuffer(), 0, (int)message.Length));
		}
		catch (FormatException)
		{
			await connection.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid Base64", cancellationToken);
			return;
		}
		finally
		{
			message.SetLength(0);
		}

		await onData(decoded);
	}

	/// <inheritdoc/>
	public async ValueTask OnClosedAsync(MudWebSocketConnection connection, WebSocketCloseStatus? status, string? description)
	{
		if (_ownsInterpreter && Interpreter is not null)
		{
			await Interpreter.DisposeAsync();
		}
	}

	/// <summary>
	/// A handler that runs <paramref name="sessionFactory"/>'s session on each connection: the WebSocket side of
	/// <see cref="TelnetConnection.RunAsync(ITelnetSession, ITelnetTransport, CancellationToken)"/>.
	/// </summary>
	/// <remarks>
	/// For registering a session under a name of your choosing, <c>telnetb64.mudstandards.org</c> say:
	/// <c>registry.Add(MudWebSocketSubprotocols.TelnetBase64, () =&gt; TelnetWebSocketHandler.ForSession(factory, base64: true))</c>.
	/// <see cref="TelnetWebSocketRegistryExtensions.AddTelnetSession(WebSocketSubprotocolRegistry, Func{ITelnetSession}, bool)"/>
	/// registers the plain telnet names.
	/// </remarks>
	/// <param name="sessionFactory">Makes the session for one connection. Called once per connection.</param>
	/// <param name="base64">Speak <c>telnetb64.mudstandards.org</c> instead of the plain telnet stream.</param>
	public static IWebSocketSubprotocolHandler ForSession(Func<ITelnetSession> sessionFactory, bool base64 = false) =>
		new TelnetSessionWebSocketHandler(sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory)), base64);

	private static string ToBase64(ReadOnlyMemory<byte> data) =>
		MemoryMarshal.TryGetArray(data, out var segment) && segment.Array is not null
			? Convert.ToBase64String(segment.Array, segment.Offset, segment.Count)
			: Convert.ToBase64String(data.ToArray());
}

/// <summary>
/// Registers the telnet subprotocols on a <see cref="WebSocketSubprotocolRegistry"/>.
/// </summary>
public static class TelnetWebSocketRegistryExtensions
{
	/// <summary>
	/// Registers <c>telnet.mudstandards.org</c> and its alias <c>telnet</c>, in that order.
	/// </summary>
	/// <param name="registry">The registry.</param>
	/// <param name="builderFactory">Returns a configured builder for one connection. See <see cref="TelnetWebSocketHandler"/>.</param>
	/// <param name="onConnected">Called with each connection's interpreter once it is built.</param>
	/// <param name="acceptNoSubprotocol">
	/// Also run telnet for a client that offers no subprotocol, as most existing wss-to-telnet bridges do.
	/// </param>
	public static WebSocketSubprotocolRegistry AddTelnet(
		this WebSocketSubprotocolRegistry registry,
		Func<TelnetInterpreterBuilder> builderFactory,
		Func<TelnetInterpreter, MudWebSocketConnection, ValueTask>? onConnected = null,
		bool acceptNoSubprotocol = false)
	{
		if (registry is null) throw new ArgumentNullException(nameof(registry));
		if (builderFactory is null) throw new ArgumentNullException(nameof(builderFactory));

		Func<IWebSocketSubprotocolHandler> factory = () => new TelnetWebSocketHandler(builderFactory, onConnected);
		registry.Add([MudWebSocketSubprotocols.Telnet, MudWebSocketSubprotocols.TelnetAlias], factory);
		if (acceptNoSubprotocol) registry.WithFallback(factory);
		return registry;
	}

	/// <summary>
	/// Registers <c>telnet.mudstandards.org</c> and its alias <c>telnet</c> to run <paramref name="sessionFactory"/>'s
	/// session on each connection: the same <see cref="ITelnetSession"/> a TCP connection runs.
	/// </summary>
	/// <remarks>
	/// The session starts once the interpreter is reading, so it can wait for the opening negotiation; the socket
	/// is closed when it returns. Use this rather than <see cref="AddTelnet"/> when the application drives each
	/// connection's lifetime itself.
	/// </remarks>
	/// <param name="registry">The registry.</param>
	/// <param name="sessionFactory">Makes the session for one connection. Called once per connection.</param>
	/// <param name="acceptNoSubprotocol">Also run it for a client that offers no subprotocol.</param>
	public static WebSocketSubprotocolRegistry AddTelnetSession(
		this WebSocketSubprotocolRegistry registry,
		Func<ITelnetSession> sessionFactory,
		bool acceptNoSubprotocol = false)
	{
		if (registry is null) throw new ArgumentNullException(nameof(registry));
		if (sessionFactory is null) throw new ArgumentNullException(nameof(sessionFactory));

		Func<IWebSocketSubprotocolHandler> factory = () => TelnetWebSocketHandler.ForSession(sessionFactory);
		registry.Add([MudWebSocketSubprotocols.Telnet, MudWebSocketSubprotocols.TelnetAlias], factory);
		if (acceptNoSubprotocol) registry.WithFallback(factory);
		return registry;
	}

	/// <summary>
	/// Registers <c>telnet.mudstandards.org</c> and <c>telnet</c> to run a session made of two lambdas: the
	/// connection's builder, and what to do once it is reading. See
	/// <see cref="AddTelnetSession(WebSocketSubprotocolRegistry, Func{ITelnetSession}, bool)"/>.
	/// </summary>
	public static WebSocketSubprotocolRegistry AddTelnetSession(
		this WebSocketSubprotocolRegistry registry,
		Func<TelnetInterpreterBuilder> builderFactory,
		Func<TelnetConnection, CancellationToken, Task> run,
		bool acceptNoSubprotocol = false)
	{
		if (builderFactory is null) throw new ArgumentNullException(nameof(builderFactory));
		if (run is null) throw new ArgumentNullException(nameof(run));
		return registry.AddTelnetSession(() => TelnetSession.Create(builderFactory, run), acceptNoSubprotocol);
	}

	/// <summary>
	/// Registers <c>telnetb64.mudstandards.org</c>. The proposal advises against it; register it only for clients
	/// that need it, and after <see cref="AddTelnet"/> so the plain stream is preferred.
	/// </summary>
	public static WebSocketSubprotocolRegistry AddTelnetBase64(
		this WebSocketSubprotocolRegistry registry,
		Func<TelnetInterpreterBuilder> builderFactory,
		Func<TelnetInterpreter, MudWebSocketConnection, ValueTask>? onConnected = null)
	{
		if (registry is null) throw new ArgumentNullException(nameof(registry));
		if (builderFactory is null) throw new ArgumentNullException(nameof(builderFactory));

		return registry.Add(MudWebSocketSubprotocols.TelnetBase64,
			() => new TelnetWebSocketHandler(builderFactory, onConnected, base64: true));
	}
}
