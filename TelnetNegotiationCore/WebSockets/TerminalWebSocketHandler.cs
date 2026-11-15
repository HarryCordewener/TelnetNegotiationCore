using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Gmcp;

namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// <c>terminal.mudstandards.org</c>: UTF-8 text with ANSI control codes in BINARY frames, and no telnet.
/// </summary>
/// <remarks>
/// <para>
/// Input is split into lines: on <c>\n</c>, with a trailing <c>\r</c> dropped. A message without a newline is
/// one line, so a client that sends one command per frame and one that sends <c>look\r\n</c> both work. An
/// empty message, or a lone newline, is an empty line (the player pressed Enter). Invalid UTF-8 is replaced
/// with U+FFFD rather than closing the socket.
/// </para>
/// <para>
/// There is no telnet here, so no NAWS, TTYPE, CHARSET, echo control or prompt marking.
/// </para>
/// <example>
/// <code>
/// registry.Add(MudWebSocketSubprotocols.Terminal, () =&gt; new TerminalWebSocketHandler
/// {
///     LineReceived = (h, line) =&gt; h.SendLineAsync($"You said: {line}"),
/// });
/// </code>
/// </example>
/// </remarks>
public class TerminalWebSocketHandler : WebSocketMessageHandler
{
	/// <summary>Called once the connection opens, before any input.</summary>
	public Func<TerminalWebSocketHandler, ValueTask>? Opened { get; init; }

	/// <summary>Called for each line of input, without its line ending.</summary>
	public Func<TerminalWebSocketHandler, string, ValueTask>? LineReceived { get; init; }

	/// <summary>Called once the connection has ended.</summary>
	public Func<TerminalWebSocketHandler, ValueTask>? Closed { get; init; }

	/// <summary>Free for the application: the player or session this connection belongs to, say.</summary>
	public object? State { get; set; }

	/// <summary>The connection this handler runs on.</summary>
	public new MudWebSocketConnection Connection => base.Connection;

	/// <summary>Sends <paramref name="text"/> as it is, ANSI codes included, as one BINARY message.</summary>
	public ValueTask SendAsync(string text, CancellationToken cancellationToken = default) =>
		Connection.SendBinaryAsync(Encoding.UTF8.GetBytes(text ?? throw new ArgumentNullException(nameof(text))), cancellationToken);

	/// <summary>Sends <paramref name="text"/> followed by CR LF, as one BINARY message.</summary>
	public ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default) =>
		SendAsync((text ?? throw new ArgumentNullException(nameof(text))) + "\r\n", cancellationToken);

	/// <summary>Splits a BINARY message into input lines. See the class remarks.</summary>
	public static IReadOnlyList<string> SplitLines(string text)
	{
		var parts = text.Split('\n');
		var count = parts.Length > 1 && parts[parts.Length - 1].Length == 0 ? parts.Length - 1 : parts.Length;
		var lines = new string[count];
		for (var i = 0; i < count; i++)
		{
			var part = parts[i];
			lines[i] = part.Length > 0 && part[part.Length - 1] == '\r' ? part.Substring(0, part.Length - 1) : part;
		}

		return lines;
	}

	/// <inheritdoc/>
	protected override ValueTask OnOpenAsync(CancellationToken cancellationToken) =>
		Opened?.Invoke(this) ?? default;

	/// <inheritdoc/>
	protected override async ValueTask OnBinaryMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
	{
		if (LineReceived is null) return;
		foreach (var line in SplitLines(Decode(data)))
		{
			await LineReceived(this, line);
		}
	}

	/// <inheritdoc/>
	protected override ValueTask OnClosedAsync(WebSocketCloseStatus? status, string? description) =>
		Closed?.Invoke(this) ?? default;

	private static string Decode(ReadOnlyMemory<byte> data) =>
		System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out var segment) && segment.Array is not null
			? Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count)
			: Encoding.UTF8.GetString(data.ToArray());
}

/// <summary>
/// <c>gmcp.mudstandards.org</c>: <see cref="TerminalWebSocketHandler"/> text in BINARY frames, plus GMCP messages
/// in TEXT frames.
/// </summary>
/// <remarks>
/// <para>
/// The proposal does not spell out the TEXT frame format. This uses the GMCP payload as telnet carries it,
/// <c>Package.Name {json}</c> with the space and data left out when there is no data, which is what Evennia and
/// Telix both send.
/// </para>
/// <para>
/// <see cref="Send"/> plugs straight into the GMCP package's sessions, so the same game code serves telnet
/// and WebSocket players:
/// <code>
/// registry.Add(MudWebSocketSubprotocols.Gmcp, () =&gt;
/// {
///     GmcpServerSession? core = null;
///     var handler = new GmcpWebSocketHandler
///     {
///         GmcpReceived = (h, package, data) =&gt; core!.HandleAsync(package, data),
///         LineReceived = (h, line) =&gt; game.InputAsync(h, line),
///     };
///     core = new GmcpServerSession(handler.Send);
///     return handler;
/// });
/// </code>
/// </para>
/// </remarks>
public sealed class GmcpWebSocketHandler : TerminalWebSocketHandler
{
	/// <summary>
	/// Called for each GMCP message: the package name and its data, empty when the message had none.
	/// </summary>
	public Func<GmcpWebSocketHandler, string, string, ValueTask>? GmcpReceived { get; init; }

	/// <summary>Sends one GMCP message as a TEXT frame. Matches <see cref="GmcpSend"/>.</summary>
	public ValueTask SendGmcpAsync(string package, string data = "")
	{
		if (string.IsNullOrWhiteSpace(package)) throw new ArgumentException("A GMCP package name is required.", nameof(package));
		return Connection.SendTextAsync(string.IsNullOrEmpty(data) ? package : package + " " + data);
	}

	/// <summary>This handler's <see cref="SendGmcpAsync"/> as a <see cref="GmcpSend"/>.</summary>
	public GmcpSend Send => (package, data) => SendGmcpAsync(package, data);

	/// <summary>
	/// Splits a TEXT frame into the package name and the data: at the first space, with surrounding whitespace
	/// trimmed. Returns <see langword="false"/> when there is no package name.
	/// </summary>
	public static bool TryParse(string message, out string package, out string data)
	{
		var text = message.Trim();
		var space = text.IndexOf(' ');
		package = space < 0 ? text : text.Substring(0, space);
		data = space < 0 ? string.Empty : text.Substring(space + 1).Trim();
		return package.Length > 0;
	}

	/// <inheritdoc/>
	protected override ValueTask OnTextMessageAsync(string text, CancellationToken cancellationToken)
	{
		if (GmcpReceived is null || !TryParse(text, out var package, out var data)) return default;
		return GmcpReceived(this, package, data);
	}
}
