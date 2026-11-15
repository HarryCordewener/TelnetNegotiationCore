using System;
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
	/// Connects to <paramref name="uri"/>, offering <c>telnet.mudstandards.org</c> and then <c>telnet</c>. A server
	/// that names no subprotocol is taken as telnet, as most wss-to-telnet bridges send none.
	/// </summary>
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
		ConnectCoreAsync(uri, options =>
		{
			options.AddSubProtocol(MudWebSocketSubprotocols.Telnet);
			options.AddSubProtocol(MudWebSocketSubprotocols.TelnetAlias);
			configure?.Invoke(options);
		}, cancellationToken);

	/// <inheritdoc cref="ConnectAsync(Uri, Action{ClientWebSocketOptions}?, CancellationToken)"/>
	public static Task<ClientWebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
		ConnectAsync(uri, null, cancellationToken);

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

	private static async Task<ClientWebSocket> ConnectCoreAsync(
		Uri uri, Action<ClientWebSocketOptions> configure, CancellationToken cancellationToken)
	{
		if (uri is null) throw new ArgumentNullException(nameof(uri));

		var socket = new ClientWebSocket();
		try
		{
			configure(socket.Options);
			await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
			return socket;
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}
}
