using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// Runs a client <see cref="Interpreters.TelnetInterpreter"/> over a WebSocket subprotocol that is not telnet: turns the
/// server's frames into the telnet stream the interpreter reads, and decides what each thing the interpreter writes
/// becomes on the wire.
/// </summary>
/// <remarks>
/// <para>
/// Subclass it for a subprotocol of your own and register it on <see cref="WebSocketClientBridges"/>. One instance
/// serves one connection. Out of the box a bridge is <c>terminal.mudstandards.org</c>: a BINARY message is text, a
/// TEXT message closes the socket with 1003, the interpreter's text goes out as BINARY, and every telnet command it
/// writes is dropped.
/// </para>
/// <para>
/// <b>Drop what the server cannot read.</b> A server with no telnet layer reads a stray <c>IAC DO …</c> as text, in
/// front of the next line the player sends, which is usually the login: the game shows its connect screen again and
/// logs nobody in. Override <see cref="OnSubnegotiationAsync"/> or <see cref="OnCommandAsync"/> only for what your
/// subprotocol carries.
/// </para>
/// <para>
/// <b>Say what the server agreed to.</b> Choosing a subprotocol is the server's agreement to what it carries. Feed the
/// matching <c>WILL</c> from <see cref="OnOpenAsync"/> (<see cref="TelnetInput.WillAsync"/>), so a plugin that waits for
/// the negotiation before speaking, as a client should, speaks.
/// </para>
/// </remarks>
public abstract class WebSocketClientBridge
{
	private MudWebSocketConnection? _connection;

	/// <summary>The connection this bridge runs on.</summary>
	protected MudWebSocketConnection Connection =>
		_connection ?? throw new InvalidOperationException("The bridge has not been started on a connection.");

	/// <summary>The largest message received, in bytes; a larger one closes the socket with 1009. 1 MiB unless set.</summary>
	public int MaxMessageSize { get; init; } = WebSocketMessageHandler.DefaultMaxMessageSize;

	internal void Attach(MudWebSocketConnection connection)
	{
		if (_connection is not null) throw new InvalidOperationException("A bridge serves one connection.");
		_connection = connection;
	}

	/// <summary>Called once the connection opens, before any frame: the place to feed the server's agreement.</summary>
	protected virtual ValueTask OnOpenAsync(TelnetInput input, CancellationToken cancellationToken) => default;

	/// <summary>
	/// Whether a BINARY message ends a line. On by default: Evennia sends each message as one frame with no line ending
	/// (its telnet adds <c>\r\n</c> per message), so without this two messages in a row run together on one line.
	/// </summary>
	/// <remarks>
	/// The line end is fed only once more text arrives, so a message still waiting for more is read as a prompt
	/// (with <see cref="Protocols.PacketPatchProtocol"/>). A message that already ends in <c>\n</c>, ignoring the
	/// colour reset Evennia appends after the text, gets nothing added. Turn it off for a server that splits a line
	/// across messages.
	/// </remarks>
	public bool MessageEndsLine { get; init; } = true;

	private bool _lineOpen;

	/// <summary>
	/// A whole BINARY message from the server. Fed as text unless overridden, after a <c>\r\n</c> when
	/// <see cref="MessageEndsLine"/> is on and the previous message left a line open.
	/// </summary>
	protected virtual async ValueTask OnBinaryMessageAsync(ReadOnlyMemory<byte> data, TelnetInput input, CancellationToken cancellationToken)
	{
		if (data.Length == 0) return;
		if (MessageEndsLine && _lineOpen) await input.TextAsync(LineEnd).ConfigureAwait(false);
		await input.TextAsync(data).ConfigureAwait(false);
		_lineOpen = !EndsLine(data.Span);
	}

	private static readonly ReadOnlyMemory<byte> LineEnd = new byte[] { (byte)'\r', (byte)'\n' };

	/// <summary>
	/// Whether <paramref name="text"/> ends with <c>\n</c>, once complete escape sequences (an SGR reset, say) after
	/// it are set aside.
	/// </summary>
	internal static bool EndsLine(ReadOnlySpan<byte> text)
	{
		var end = text.Length;
		while (end > 0)
		{
			var last = text[end - 1];
			if (last == (byte)'\n') return true;

			// A CSI sequence: ESC [ parameters (0x30–0x3F) intermediates (0x20–0x2F) final (0x40–0x7E).
			if (last < 0x40 || last > 0x7E) return false;
			var i = end - 2;
			while (i >= 0 && text[i] >= 0x20 && text[i] <= 0x3F) i--;
			if (i < 1 || text[i] != (byte)'[' || text[i - 1] != 0x1B) return false;
			end = i - 1;
		}

		return false;
	}

	/// <summary>A whole TEXT message from the server, valid UTF-8. Closes the socket with 1003 unless overridden.</summary>
	protected virtual ValueTask OnTextMessageAsync(string text, TelnetInput input, CancellationToken cancellationToken) =>
		Connection.CloseAsync(WebSocketCloseStatus.InvalidMessageType,
			$"{Connection.Subprotocol ?? "this subprotocol"} carries BINARY frames only", cancellationToken);

	/// <summary>Text the interpreter wrote, <c>IAC IAC</c> already a single <c>0xFF</c>. Sent as BINARY unless overridden.</summary>
	protected virtual ValueTask OnTextAsync(ReadOnlyMemory<byte> text, CancellationToken cancellationToken) =>
		Connection.SendBinaryAsync(text, cancellationToken);

	/// <summary>A subnegotiation the interpreter wrote, its payload unescaped. Dropped unless overridden.</summary>
	protected virtual ValueTask OnSubnegotiationAsync(byte option, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
		default;

	/// <summary>
	/// Any other command the interpreter wrote: <c>WILL</c>, <c>WONT</c>, <c>DO</c> or <c>DONT</c> with its option, or a
	/// two-byte command (GA, EOR, NOP, …) with none. Dropped unless overridden.
	/// </summary>
	protected virtual ValueTask OnCommandAsync(byte command, byte? option, CancellationToken cancellationToken) =>
		default;

	/// <summary>Called once the connection has ended.</summary>
	protected virtual ValueTask OnClosedAsync(WebSocketCloseStatus? status, string? description) => default;

	internal ValueTask OpenAsync(TelnetInput input, CancellationToken ct) => OnOpenAsync(input, ct);
	internal ValueTask BinaryMessageAsync(ReadOnlyMemory<byte> data, TelnetInput input, CancellationToken ct) => OnBinaryMessageAsync(data, input, ct);
	internal ValueTask TextMessageAsync(string text, TelnetInput input, CancellationToken ct) => OnTextMessageAsync(text, input, ct);
	internal ValueTask TextAsync(ReadOnlyMemory<byte> text, CancellationToken ct) => OnTextAsync(text, ct);
	internal ValueTask SubnegotiationAsync(byte option, ReadOnlyMemory<byte> payload, CancellationToken ct) => OnSubnegotiationAsync(option, payload, ct);
	internal ValueTask CommandAsync(byte command, byte? option, CancellationToken ct) => OnCommandAsync(command, option, ct);
	internal ValueTask ClosedAsync(WebSocketCloseStatus? status, string? description) => OnClosedAsync(status, description);
}

/// <summary>
/// What a <see cref="WebSocketClientBridge"/> feeds the interpreter: text, or telnet commands as if the server had sent them.
/// </summary>
public readonly struct TelnetInput
{
	private const byte Iac = (byte)Trigger.IAC;
	private readonly Func<ReadOnlyMemory<byte>, ValueTask> _feed;

	internal TelnetInput(Func<ReadOnlyMemory<byte>, ValueTask> feed) => _feed = feed;

	/// <summary>Text, with every <c>0xFF</c> doubled so it stays data.</summary>
	public ValueTask TextAsync(ReadOnlyMemory<byte> text) => _feed(Escape(text.Span));

	/// <summary>Text, UTF-8 encoded.</summary>
	public ValueTask TextAsync(string text) => TextAsync(Encoding.UTF8.GetBytes(text ?? throw new ArgumentNullException(nameof(text))));

	/// <summary><c>IAC WILL option</c>: the server agrees to <paramref name="option"/>.</summary>
	public ValueTask WillAsync(byte option) => _feed(new[] { Iac, (byte)Trigger.WILL, option });

	/// <summary><c>IAC DO option</c>: the server asks the client to use <paramref name="option"/>.</summary>
	public ValueTask DoAsync(byte option) => _feed(new[] { Iac, (byte)Trigger.DO, option });

	/// <summary><c>IAC SB option payload IAC SE</c>, the payload escaped.</summary>
	public ValueTask SubnegotiationAsync(byte option, ReadOnlyMemory<byte> payload)
	{
		var escaped = Escape(payload.Span);
		var stream = new byte[escaped.Length + 5];
		stream[0] = Iac;
		stream[1] = (byte)Trigger.SB;
		stream[2] = option;
		escaped.CopyTo(stream, 3);
		stream[stream.Length - 2] = Iac;
		stream[stream.Length - 1] = (byte)Trigger.SE;
		return _feed(stream);
	}

	/// <summary>Bytes fed as they are: a telnet stream you have already built.</summary>
	public ValueTask RawAsync(ReadOnlyMemory<byte> telnet) => _feed(telnet);

	internal static byte[] Escape(ReadOnlySpan<byte> text)
	{
		var count = 0;
		foreach (var b in text)
		{
			if (b == Iac) count++;
		}

		if (count == 0) return text.ToArray();

		var stream = new byte[text.Length + count];
		var i = 0;
		foreach (var b in text)
		{
			stream[i++] = b;
			if (b == Iac) stream[i++] = Iac;
		}

		return stream;
	}
}

/// <summary><c>terminal.mudstandards.org</c>: UTF-8 text with ANSI in BINARY frames, and no telnet.</summary>
public sealed class TerminalClientBridge : WebSocketClientBridge
{
}

/// <summary>
/// One telnet option carried in TEXT frames, text in BINARY frames: the shape of <c>gmcp.mudstandards.org</c>, and of a
/// subprotocol of your own that carries another option (MSDP, say) the same way.
/// </summary>
/// <remarks>
/// The connection opens with <c>IAC WILL option</c>. A TEXT message becomes <c>IAC SB option … IAC SE</c>, and a
/// subnegotiation for the option the interpreter writes becomes a TEXT message; <see cref="Decode"/> and
/// <see cref="Encode"/> translate between the two, UTF-8 as it is unless overridden. Everything else is a terminal's.
/// </remarks>
public class OptionTextFrameBridge : WebSocketClientBridge
{
	/// <param name="option">The telnet option the TEXT frames carry.</param>
	public OptionTextFrameBridge(byte option) => Option = option;

	/// <summary>The telnet option the TEXT frames carry.</summary>
	public byte Option { get; }

	/// <summary>The subnegotiation payload for one TEXT message, or null to drop it.</summary>
	protected virtual byte[]? Decode(string text) => Encoding.UTF8.GetBytes(text);

	/// <summary>The TEXT message for one subnegotiation the interpreter wrote, or null to drop it.</summary>
	protected virtual string? Encode(ReadOnlyMemory<byte> payload) =>
		payload.Length == 0 ? null : Encoding.UTF8.GetString(payload.ToArray());

	/// <inheritdoc/>
	protected override ValueTask OnOpenAsync(TelnetInput input, CancellationToken cancellationToken) =>
		input.WillAsync(Option);

	/// <inheritdoc/>
	protected override ValueTask OnTextMessageAsync(string text, TelnetInput input, CancellationToken cancellationToken) =>
		Decode(text) is { } payload ? input.SubnegotiationAsync(Option, payload) : default;

	/// <inheritdoc/>
	protected override ValueTask OnSubnegotiationAsync(byte option, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
		option == Option && Encode(payload) is { } text ? Connection.SendTextAsync(text, cancellationToken) : default;
}

/// <summary>
/// <c>gmcp.mudstandards.org</c>: GMCP in TEXT frames as <c>Package.Name {json}</c>, the payload telnet carries, which is
/// what Evennia sends; text in BINARY frames.
/// </summary>
public sealed class GmcpClientBridge : OptionTextFrameBridge
{
	/// <summary>A GMCP bridge.</summary>
	public GmcpClientBridge() : base((byte)Trigger.GMCP)
	{
	}
}

/// <summary>
/// The non-telnet subprotocols a WebSocket client speaks, in its order of preference, and the bridge for each.
/// </summary>
/// <remarks>
/// <see cref="Default"/> is <c>gmcp.mudstandards.org</c> then <c>terminal.mudstandards.org</c>. The telnet names are not
/// listed: <see cref="TelnetWebSocketClient"/> always offers them first and the interpreter reads them natively.
/// <code>
/// var bridges = WebSocketClientBridges.Default.Add("msdp.example.org", () =&gt; new OptionTextFrameBridge((byte)Trigger.MSDP));
/// using var socket = await TelnetWebSocketClient.ConnectAsync(uri, null, bridges, ct);
/// await using var connection = await builder.StartAsync(TelnetTransport.WebSocketClient(socket, bridges), ct);
/// </code>
/// </remarks>
public sealed class WebSocketClientBridges
{
	private readonly List<(string Name, Func<WebSocketClientBridge> Factory)> _entries = [];

	/// <summary>A new set holding <c>gmcp.mudstandards.org</c> and then <c>terminal.mudstandards.org</c>.</summary>
	public static WebSocketClientBridges Default => new WebSocketClientBridges()
		.Add(MudWebSocketSubprotocols.Gmcp, () => new GmcpClientBridge())
		.Add(MudWebSocketSubprotocols.Terminal, () => new TerminalClientBridge());

	/// <summary>Every registered name, in order of preference.</summary>
	public IReadOnlyList<string> Names => _entries.Select(e => e.Name).ToArray();

	/// <summary>Registers <paramref name="name"/> after the names already registered.</summary>
	/// <param name="name">The subprotocol name.</param>
	/// <param name="factory">Makes the bridge for one connection.</param>
	/// <exception cref="ArgumentException">
	/// The name is not an HTTP token, is already registered, or is a telnet name, which needs no bridge.
	/// </exception>
	public WebSocketClientBridges Add(string name, Func<WebSocketClientBridge> factory)
	{
		if (!WebSocketSubprotocolRegistry.IsToken(name))
			throw new ArgumentException("A subprotocol name is a single non-empty HTTP token (RFC 7230).", nameof(name));
		if (factory is null) throw new ArgumentNullException(nameof(factory));
		if (MudWebSocketSubprotocols.IsTelnet(name)
		    || string.Equals(name, MudWebSocketSubprotocols.TelnetBase64, StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException($"'{name}' is a telnet stream, which the interpreter reads without a bridge.", nameof(name));
		if (Find(name) is not null) throw new ArgumentException($"The subprotocol '{name}' is already registered.", nameof(name));

		_entries.Add((name, factory));
		return this;
	}

	/// <summary>Removes <paramref name="name"/>, if registered.</summary>
	public WebSocketClientBridges Remove(string name)
	{
		_entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
		return this;
	}

	internal Func<WebSocketClientBridge>? Find(string? name) =>
		_entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).Factory;
}

/// <summary>
/// The transport that runs a <see cref="WebSocketClientBridge"/>: frames in, a telnet stream to the interpreter; the
/// interpreter's writes parsed as telnet and handed to the bridge.
/// </summary>
internal sealed class BridgeTransport : Connections.ITelnetTransport
{
	private const byte Iac = (byte)Trigger.IAC;

	private readonly MudWebSocketConnection _connection;
	private readonly WebSocketClientBridge _bridge;
	private readonly SemaphoreSlim _writeGate = new(1, 1);
	private readonly MemoryStream _text = new();
	private readonly MemoryStream _subnegotiation = new();
	private WriteState _state;
	private byte _command;
	private byte _option;

	public BridgeTransport(MudWebSocketConnection connection, WebSocketClientBridge bridge)
	{
		_connection = connection;
		_bridge = bridge;
		bridge.Attach(connection);
	}

	public Task ReadAsync(Func<ReadOnlyMemory<byte>, ValueTask> onData, CancellationToken cancellationToken) =>
		_connection.RunAsync(new Reader(_bridge, new TelnetInput(onData)), cancellationToken);

	public ValueTask CloseAsync(CancellationToken cancellationToken) =>
		_connection.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

	public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
	{
		await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			foreach (var b in data.ToArray())
			{
				await TakeAsync(b, cancellationToken).ConfigureAwait(false);
			}

			await FlushTextAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writeGate.Release();
		}
	}

	// One written byte through the telnet parser. Text collects until a command or the end of the write.
	private async ValueTask TakeAsync(byte b, CancellationToken cancellationToken)
	{
		switch (_state)
		{
			case WriteState.Data:
				if (b == Iac) _state = WriteState.Iac;
				else _text.WriteByte(b);
				return;

			case WriteState.Iac:
				_state = WriteState.Data;
				switch ((Trigger)b)
				{
					case Trigger.IAC:
						_text.WriteByte(Iac);
						return;
					case Trigger.WILL:
					case Trigger.WONT:
					case Trigger.DO:
					case Trigger.DONT:
						_command = b;
						_state = WriteState.Option;
						return;
					case Trigger.SB:
						_state = WriteState.SubnegotiationOption;
						return;
				}

				await FlushTextAsync(cancellationToken).ConfigureAwait(false);
				await _bridge.CommandAsync(b, null, cancellationToken).ConfigureAwait(false);
				return;

			case WriteState.Option:
				_state = WriteState.Data;
				await FlushTextAsync(cancellationToken).ConfigureAwait(false);
				await _bridge.CommandAsync(_command, b, cancellationToken).ConfigureAwait(false);
				return;

			case WriteState.SubnegotiationOption:
				_option = b;
				_subnegotiation.SetLength(0);
				_state = WriteState.Subnegotiation;
				return;

			case WriteState.Subnegotiation:
				if (b == Iac) _state = WriteState.SubnegotiationIac;
				else _subnegotiation.WriteByte(b);
				return;

			case WriteState.SubnegotiationIac:
				if (b == Iac)
				{
					_subnegotiation.WriteByte(Iac);
					_state = WriteState.Subnegotiation;
					return;
				}

				_state = WriteState.Data;
				if (b != (byte)Trigger.SE) return;
				await FlushTextAsync(cancellationToken).ConfigureAwait(false);
				var payload = _subnegotiation.ToArray();
				_subnegotiation.SetLength(0);
				await _bridge.SubnegotiationAsync(_option, payload, cancellationToken).ConfigureAwait(false);
				return;
		}
	}

	private async ValueTask FlushTextAsync(CancellationToken cancellationToken)
	{
		if (_text.Length == 0) return;
		var text = _text.ToArray();
		_text.SetLength(0);
		await _bridge.TextAsync(text, cancellationToken).ConfigureAwait(false);
	}

	private enum WriteState
	{
		Data,
		Iac,
		Option,
		SubnegotiationOption,
		Subnegotiation,
		SubnegotiationIac,
	}

	private sealed class Reader : WebSocketMessageHandler
	{
		private readonly WebSocketClientBridge _bridge;
		private readonly TelnetInput _input;

		public Reader(WebSocketClientBridge bridge, TelnetInput input)
		{
			_bridge = bridge;
			_input = input;
			MaxMessageSize = bridge.MaxMessageSize;
		}

		protected override ValueTask OnOpenAsync(CancellationToken cancellationToken) =>
			_bridge.OpenAsync(_input, cancellationToken);

		protected override ValueTask OnBinaryMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
			_bridge.BinaryMessageAsync(data, _input, cancellationToken);

		protected override ValueTask OnTextMessageAsync(string text, CancellationToken cancellationToken) =>
			_bridge.TextMessageAsync(text, _input, cancellationToken);

		protected override ValueTask OnClosedAsync(WebSocketCloseStatus? status, string? description) =>
			_bridge.ClosedAsync(status, description);
	}
}
