using System;
using System.Net.WebSockets;

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
