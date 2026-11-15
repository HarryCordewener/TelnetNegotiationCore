using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// The WebSocket subprotocols an application speaks, in its order of preference, and the handler for each.
/// </summary>
/// <remarks>
/// <para>
/// The server picks with <see cref="Select"/> before it accepts the socket, accepts with the name it got back,
/// and then calls <see cref="RunAsync(WebSocket, CancellationToken)"/>. A client adds <see cref="Names"/> to its
/// <c>ClientWebSocketOptions</c> (<see cref="ClientWebSocketOptionsExtensions.AddSubprotocols"/>), connects, and
/// calls the same <c>RunAsync</c>.
/// </para>
/// <example>
/// <code>
/// var registry = new WebSocketSubprotocolRegistry()
///     .Add("markup.v1.example.org", () => new MarkupHandler())     // preferred
///     .AddTelnet(() => factory.CreateBuilder().AddPlugin&lt;GMCPProtocol&gt;() /* ... */);
///
/// app.UseWebSockets();
/// app.Map("/ws", async ctx =>
/// {
///     var chosen = registry.Select(ctx.WebSockets.WebSocketRequestedProtocols);
///     if (!ctx.WebSockets.IsWebSocketRequest || chosen is null) { ctx.Response.StatusCode = 400; return; }
///     using var socket = await ctx.WebSockets.AcceptWebSocketAsync(chosen);
///     await registry.RunAsync(socket, ctx.RequestAborted);
/// });
/// </code>
/// </example>
/// </remarks>
public sealed class WebSocketSubprotocolRegistry
{
	private readonly List<(string Name, Func<IWebSocketSubprotocolHandler> Factory)> _entries = new();
	private Func<IWebSocketSubprotocolHandler>? _fallback;

	/// <summary>Every registered name, in order of preference.</summary>
	public IReadOnlyList<string> Names => _entries.Select(e => e.Name).ToArray();

	/// <summary>
	/// Registers <paramref name="name"/>. Names registered earlier are preferred when a client offers several.
	/// </summary>
	/// <param name="name">The subprotocol name. RFC 6455 asks for one based on a domain you control.</param>
	/// <param name="factory">Makes the handler for one connection. Called once per connection.</param>
	/// <exception cref="ArgumentException">The name is empty, contains a separator or space, or is already registered.</exception>
	public WebSocketSubprotocolRegistry Add(string name, Func<IWebSocketSubprotocolHandler> factory)
	{
		if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny([',', ' ', '\t', '"']) >= 0)
			throw new ArgumentException("A subprotocol name is a single non-empty token.", nameof(name));
		if (factory is null) throw new ArgumentNullException(nameof(factory));
		if (Find(name) is not null)
			throw new ArgumentException($"The subprotocol '{name}' is already registered.", nameof(name));

		_entries.Add((name, factory));
		return this;
	}

	/// <summary>
	/// Registers one handler under several names, such as a name and its alias, in the given order.
	/// </summary>
	public WebSocketSubprotocolRegistry Add(IEnumerable<string> names, Func<IWebSocketSubprotocolHandler> factory)
	{
		if (names is null) throw new ArgumentNullException(nameof(names));
		foreach (var name in names) Add(name, factory);
		return this;
	}

	/// <summary>
	/// The handler for a client that offers no subprotocol at all. Without one, such a socket is closed with 1002
	/// (protocol error). Most existing wss-to-telnet bridges send no subprotocol, so a telnet server usually wants
	/// one (<see cref="TelnetWebSocketRegistryExtensions.AddTelnet"/> takes a flag for it).
	/// </summary>
	public WebSocketSubprotocolRegistry WithFallback(Func<IWebSocketSubprotocolHandler> factory)
	{
		_fallback = factory ?? throw new ArgumentNullException(nameof(factory));
		return this;
	}

	/// <summary>Whether a client that offers nothing can still connect.</summary>
	public bool HasFallback => _fallback is not null;

	/// <summary>
	/// Picks the subprotocol for a handshake: the most preferred registered name the client offered, spelled as
	/// the client spelled it, or <see langword="null"/> when none matches.
	/// </summary>
	/// <remarks>
	/// Names compare ordinal and case-insensitive, as ASP.NET Core does. When this returns <see langword="null"/>,
	/// accept without a subprotocol only if the client offered nothing and <see cref="HasFallback"/> is set; RFC 6455
	/// lets the client fail the connection otherwise.
	/// </remarks>
	public string? Select(IEnumerable<string>? offered)
	{
		if (offered is null) return null;
		var offers = offered.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToArray();
		foreach (var (name, _) in _entries)
		{
			foreach (var offer in offers)
			{
				if (string.Equals(offer, name, StringComparison.OrdinalIgnoreCase)) return offer;
			}
		}

		return null;
	}

	/// <summary>
	/// Makes the handler for <paramref name="subprotocol"/>, or for no subprotocol when it is <see langword="null"/> or empty.
	/// Returns <see langword="null"/> when nothing is registered for it.
	/// </summary>
	public IWebSocketSubprotocolHandler? CreateHandler(string? subprotocol)
	{
		if (string.IsNullOrEmpty(subprotocol)) return _fallback?.Invoke();
		return Find(subprotocol!)?.Invoke();
	}

	/// <summary>
	/// Runs the handler registered for the socket's negotiated subprotocol until the socket closes.
	/// </summary>
	/// <remarks>
	/// A socket whose subprotocol has no handler is closed with 1002 and the task completes. The socket stays the
	/// caller's to dispose.
	/// </remarks>
	public Task RunAsync(WebSocket socket, CancellationToken cancellationToken = default) =>
		RunAsync(new MudWebSocketConnection(socket ?? throw new ArgumentNullException(nameof(socket))), cancellationToken);

	/// <inheritdoc cref="RunAsync(WebSocket, CancellationToken)"/>
	public async Task RunAsync(MudWebSocketConnection connection, CancellationToken cancellationToken = default)
	{
		if (connection is null) throw new ArgumentNullException(nameof(connection));

		var handler = CreateHandler(connection.Subprotocol);
		if (handler is null)
		{
			await connection.CloseAsync(WebSocketCloseStatus.ProtocolError,
				string.IsNullOrEmpty(connection.Subprotocol) ? "A subprotocol is required" : "Unsupported subprotocol",
				cancellationToken);
			return;
		}

		await connection.RunAsync(handler, cancellationToken);
	}

	private Func<IWebSocketSubprotocolHandler>? Find(string name)
	{
		foreach (var entry in _entries)
		{
			if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) return entry.Factory;
		}

		return null;
	}
}
