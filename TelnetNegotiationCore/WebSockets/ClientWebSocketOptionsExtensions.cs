using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// Client-side helpers for <see cref="ClientWebSocketOptions"/>.
/// </summary>
public static class ClientWebSocketOptionsExtensions
{
	/// <summary>
	/// Offers every name in <paramref name="registry"/>, in its order of preference.
	/// </summary>
	public static ClientWebSocketOptions AddSubprotocols(this ClientWebSocketOptions options, WebSocketSubprotocolRegistry registry)
	{
		if (options is null) throw new ArgumentNullException(nameof(options));
		if (registry is null) throw new ArgumentNullException(nameof(registry));
		foreach (var name in registry.Names) options.AddSubProtocol(name);
		return options;
	}
}

/// <summary>
/// Connecting to a MUD's WebSocket endpoint as a client.
/// </summary>
/// <remarks>
/// <example>
/// <code>
/// using var socket = await TelnetWebSocketClient.ConnectAsync(new Uri("wss://game.example/"), ct);
/// await using var telnet = await new TelnetInterpreterBuilder()
///     .UseMode(TelnetInterpreter.TelnetMode.Client)
///     /* ... */
///     .StartAsync(socket, ct);
/// await telnet.Completion;
/// </code>
/// </example>
/// </remarks>
public static class TelnetWebSocketClient
{
	/// <summary>
	/// Connects to <paramref name="uri"/>, offering <c>telnet.mudstandards.org</c> and <c>telnet</c>, then
	/// <c>gmcp.mudstandards.org</c> and <c>terminal.mudstandards.org</c> (<see cref="WebSocketClientBridges.Default"/>).
	/// A server that names no subprotocol is taken as telnet, as most wss-to-telnet bridges send none.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The server picks, by its own preference. Evennia speaks no telnet over a WebSocket and picks <c>gmcp</c>;
	/// offered only the telnet names it names none and sends its own JSON, which is not a telnet stream.
	/// <see cref="Connections.TelnetTransport.WebSocketClient(WebSocket, WebSocketClientBridges?)"/> runs the interpreter over whichever was picked.
	/// </para>
	/// <para>
	/// A server that answers the offer with an HTTP error is dialled once more offering no subprotocol. Bridges
	/// in use refuse names they do not know rather than ignoring them: websockify answers
	/// <c>400 Invalid protocol selected</c>, and an Apache <c>mod_proxy_wstunnel</c> in front of a game answers
	/// <c>502</c>; both accept the same handshake with no <c>Sec-WebSocket-Protocol</c> and carry telnet in
	/// binary frames. A connect that fails without an HTTP answer (no route, TLS, a timeout) is not retried.
	/// </para>
	/// </remarks>
	/// <param name="uri">A <c>ws://</c> or <c>wss://</c> address.</param>
	/// <param name="configure">
	/// Sets anything else on the options before connecting: <c>KeepAliveInterval</c>, and on .NET 9 or later
	/// <c>KeepAliveTimeout</c> so a server that stops answering is noticed; headers; a proxy. Leave
	/// <c>DangerousDeflateOptions</c> unset, as MCCP already compresses.
	/// </param>
	/// <param name="cancellationToken">Cancels the connect.</param>
	/// <returns>The connected socket. It is the caller's to dispose.</returns>
	public static Task<ClientWebSocket> ConnectAsync(
		Uri uri, Action<ClientWebSocketOptions>? configure = null, CancellationToken cancellationToken = default) =>
		ConnectTelnetAsync(uri, WebSocketClientBridges.Default, configure, cancellationToken);

	/// <summary>
	/// Connects to <paramref name="uri"/>, offering <c>telnet.mudstandards.org</c> and <c>telnet</c>, then every name in
	/// <paramref name="bridges"/> in its order. Run the result with
	/// <see cref="Connections.TelnetTransport.WebSocketClient(WebSocket, WebSocketClientBridges?)"/> and the same bridges.
	/// Otherwise as <see cref="ConnectAsync(Uri, Action{ClientWebSocketOptions}?, CancellationToken)"/>.
	/// </summary>
	public static Task<ClientWebSocket> ConnectAsync(
		Uri uri, Action<ClientWebSocketOptions>? configure, WebSocketClientBridges bridges,
		CancellationToken cancellationToken = default) =>
		ConnectTelnetAsync(uri, bridges ?? throw new ArgumentNullException(nameof(bridges)), configure, cancellationToken);

	/// <inheritdoc cref="ConnectAsync(Uri, Action{ClientWebSocketOptions}?, CancellationToken)"/>
	public static Task<ClientWebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
		ConnectAsync(uri, null, cancellationToken);

#if !NETSTANDARD2_0
	/// <summary>
	/// Connects to <paramref name="uri"/> through <paramref name="invoker"/>, offering the telnet names and then every
	/// name in <paramref name="bridges"/> (<see cref="WebSocketClientBridges.Default"/> when null). Otherwise as
	/// <see cref="ConnectAsync(Uri, Action{ClientWebSocketOptions}?, CancellationToken)"/>, the redial with no
	/// subprotocol included, which goes through the same invoker.
	/// </summary>
	/// <remarks>
	/// For a caller that decides where the socket goes: a <see cref="System.Net.Http.SocketsHttpHandler"/> whose
	/// <c>ConnectCallback</c> dials addresses already vetted, rather than a fresh lookup of the name, or one with its
	/// own proxy and TLS settings. The invoker is the caller's, and is not disposed here.
	/// </remarks>
	public static Task<ClientWebSocket> ConnectAsync(
		Uri uri, HttpMessageInvoker invoker, Action<ClientWebSocketOptions>? configure, WebSocketClientBridges? bridges,
		CancellationToken cancellationToken = default) =>
		ConnectTelnetAsync(
			uri, bridges ?? WebSocketClientBridges.Default, configure, cancellationToken,
			invoker ?? throw new ArgumentNullException(nameof(invoker)));
#endif

	/// <summary>
	/// Connects to <paramref name="uri"/>, offering every name in <paramref name="registry"/> in its order of
	/// preference. Run the result with <see cref="WebSocketSubprotocolRegistry.RunAsync(WebSocket, CancellationToken)"/>.
	/// </summary>
	/// <returns>The connected socket. It is the caller's to dispose.</returns>
	public static Task<ClientWebSocket> ConnectAsync(
		this WebSocketSubprotocolRegistry registry,
		Uri uri,
		Action<ClientWebSocketOptions>? configure = null,
		CancellationToken cancellationToken = default)
	{
		if (registry is null) throw new ArgumentNullException(nameof(registry));
		return ConnectCoreAsync(uri, options =>
		{
			options.AddSubprotocols(registry);
			configure?.Invoke(options);
		}, cancellationToken);
	}

	// netstandard2.0 cannot see the HTTP status of a failed upgrade, so there any failed handshake is
	// dialled once more offering nothing: a server that is simply down costs a second attempt.
	private static async Task<ClientWebSocket> ConnectTelnetAsync(
		Uri uri, WebSocketClientBridges bridges, Action<ClientWebSocketOptions>? configure, CancellationToken cancellationToken,
		HttpMessageInvoker? invoker = null)
	{
		var refused = false;
		try
		{
			return await ConnectCoreAsync(uri, options =>
			{
				options.AddSubProtocol(MudWebSocketSubprotocols.Telnet);
				options.AddSubProtocol(MudWebSocketSubprotocols.TelnetAlias);
				foreach (var name in bridges.Names) options.AddSubProtocol(name);
				configure?.Invoke(options);
#if NETSTANDARD2_0
			}, cancellationToken, invoker, _ => refused = true).ConfigureAwait(false);
#else
				options.CollectHttpResponseDetails = true;
			}, cancellationToken, invoker, socket => refused = socket.HttpStatusCode != 0).ConfigureAwait(false);
#endif
		}
		catch (WebSocketException) when (refused && !cancellationToken.IsCancellationRequested)
		{
			return await ConnectCoreAsync(uri, options => configure?.Invoke(options), cancellationToken, invoker)
				.ConfigureAwait(false);
		}
	}

	private static async Task<ClientWebSocket> ConnectCoreAsync(
		Uri uri, Action<ClientWebSocketOptions> configure, CancellationToken cancellationToken,
		HttpMessageInvoker? invoker = null, Action<ClientWebSocket>? failed = null)
	{
		if (uri is null) throw new ArgumentNullException(nameof(uri));

		var socket = new ClientWebSocket();
		try
		{
			configure(socket.Options);
#if NETSTANDARD2_0
			await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
#else
			await (invoker is null
				? socket.ConnectAsync(uri, cancellationToken)
				: socket.ConnectAsync(uri, invoker, cancellationToken)).ConfigureAwait(false);
#endif
			return socket;
		}
		catch
		{
			failed?.Invoke(socket);
			socket.Dispose();
			throw;
		}
	}
}
