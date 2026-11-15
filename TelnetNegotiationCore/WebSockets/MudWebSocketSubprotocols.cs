namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// The WebSocket subprotocol names from the mudstandards.org WebSocket proposal
/// (<see href="https://mudstandards.org/websocket/"/>).
/// </summary>
/// <remarks>
/// A client lists the names it understands in <c>Sec-WebSocket-Protocol</c>, the server echoes the one
/// it picked, and the choice is fixed for the life of the socket. <see cref="WebSocketSubprotocolRegistry"/>
/// does the picking and runs the handler that goes with the name.
/// </remarks>
public static class MudWebSocketSubprotocols
{
	/// <summary>
	/// <c>telnet.mudstandards.org</c>: the complete telnet stream, options included, in BINARY frames.
	/// </summary>
	public const string Telnet = "telnet.mudstandards.org";

	/// <summary>
	/// <c>telnet</c>: the same as <see cref="Telnet"/>.
	/// </summary>
	public const string TelnetAlias = "telnet";

	/// <summary>
	/// <c>telnetb64.mudstandards.org</c>: the complete telnet stream, Base64 encoded, in TEXT frames.
	/// The proposal advises against it.
	/// </summary>
	public const string TelnetBase64 = "telnetb64.mudstandards.org";

	/// <summary>
	/// <c>terminal.mudstandards.org</c>: UTF-8 input and output with ANSI control codes, in BINARY frames.
	/// No telnet.
	/// </summary>
	public const string Terminal = "terminal.mudstandards.org";

	/// <summary>
	/// <c>gmcp.mudstandards.org</c>: UTF-8 input and output with ANSI control codes in BINARY frames, and
	/// UTF-8 GMCP messages in TEXT frames. No telnet.
	/// </summary>
	public const string Gmcp = "gmcp.mudstandards.org";

	/// <summary>
	/// <c>divstream.mudstandards.org</c>: HTML <c>div</c> elements in BINARY frames. TNC ships no handler for
	/// it; register one with <see cref="WebSocketSubprotocolRegistry.Add(string, System.Func{IWebSocketSubprotocolHandler})"/>.
	/// </summary>
	public const string DivStream = "divstream.mudstandards.org";

	/// <summary>
	/// Whether <paramref name="name"/> names the plain telnet stream: <see cref="Telnet"/> or <see cref="TelnetAlias"/>.
	/// </summary>
	public static bool IsTelnet(string? name) =>
		string.Equals(name, Telnet, System.StringComparison.OrdinalIgnoreCase)
		|| string.Equals(name, TelnetAlias, System.StringComparison.OrdinalIgnoreCase);
}
