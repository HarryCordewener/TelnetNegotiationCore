using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.WebSockets;

/// <summary>
/// Runs a telnet interpreter over <c>terminal.mudstandards.org</c> or <c>gmcp.mudstandards.org</c>, neither of which
/// carries telnet: the read side turns frames into the telnet stream the interpreter expects, and the write side takes
/// telnet back out of what it sends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read.</b> A BINARY message is text, so a <c>0xFF</c> in it is doubled to stay data. On <c>gmcp</c> a TEXT message
/// becomes <c>IAC SB GMCP … IAC SE</c>, and the connection opens with <c>IAC WILL GMCP</c>: the subprotocol is the
/// server's agreement to GMCP, and a client that waits for that agreement before saying <c>Core.Hello</c> (as a client
/// should) then says it. A TEXT message on <c>terminal</c> closes the socket with 1003.
/// </para>
/// <para>
/// <b>Write.</b> Text goes out as one BINARY message per write, <c>IAC IAC</c> as one <c>0xFF</c>. On <c>gmcp</c> a
/// GMCP subnegotiation goes out as a TEXT message. Every other telnet command is dropped, the answer to the synthesised
/// <c>WILL GMCP</c> included. None may reach the server: it reads them as text, in front of the next line the player
/// sends, which is usually the login.
/// </para>
/// </remarks>
internal sealed class TelnetBridge
{
	private const byte Iac = (byte)Trigger.IAC;

	private readonly MudWebSocketConnection _connection;
	private readonly SemaphoreSlim _writeGate = new(1, 1);
	private readonly MemoryStream _text = new();
	private readonly MemoryStream _subnegotiation = new();
	private WriteState _state;
	private byte _option;

	public TelnetBridge(MudWebSocketConnection connection, bool gmcp)
	{
		_connection = connection;
		Gmcp = gmcp;
	}

	/// <summary>Whether this is <c>gmcp.mudstandards.org</c> rather than <c>terminal.mudstandards.org</c>.</summary>
	public bool Gmcp { get; }

	public Task ReadAsync(Func<ReadOnlyMemory<byte>, ValueTask> onData, CancellationToken cancellationToken) =>
		_connection.RunAsync(new Reader(this, onData), cancellationToken);

	public ValueTask CloseAsync(CancellationToken cancellationToken) =>
		_connection.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

	public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
	{
		await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var bytes = data.ToArray();
			foreach (var b in bytes)
			{
				var gmcp = Take(b);
				if (gmcp is null) continue;
				await FlushTextAsync(cancellationToken).ConfigureAwait(false);
				await _connection.SendTextAsync(gmcp, cancellationToken).ConfigureAwait(false);
			}

			await FlushTextAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writeGate.Release();
		}
	}

	/// <summary>The telnet stream for one BINARY message: the text with every <c>0xFF</c> doubled.</summary>
	internal static byte[] FromText(ReadOnlySpan<byte> text)
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

	/// <summary>The telnet stream for one GMCP TEXT message: <c>IAC SB GMCP payload IAC SE</c>.</summary>
	internal static byte[] FromGmcp(string message)
	{
		var payload = FromText(Encoding.UTF8.GetBytes(message));
		var stream = new byte[payload.Length + 5];
		stream[0] = Iac;
		stream[1] = (byte)Trigger.SB;
		stream[2] = (byte)Trigger.GMCP;
		payload.CopyTo(stream, 3);
		stream[stream.Length - 2] = Iac;
		stream[stream.Length - 1] = (byte)Trigger.SE;
		return stream;
	}

	internal static readonly byte[] WillGmcp = [Iac, (byte)Trigger.WILL, (byte)Trigger.GMCP];

	/// <summary>
	/// Feeds one written byte through the telnet parser. Text collects in <see cref="_text"/>; a finished GMCP message
	/// is returned; every other command is consumed.
	/// </summary>
	private string? Take(byte b)
	{
		switch (_state)
		{
			case WriteState.Data:
				if (b == Iac) _state = WriteState.Iac;
				else _text.WriteByte(b);
				return null;

			case WriteState.Iac:
				_state = WriteState.Data;
				switch ((Trigger)b)
				{
					case Trigger.IAC:
						_text.WriteByte(Iac);
						break;
					case Trigger.WILL:
					case Trigger.WONT:
					case Trigger.DO:
					case Trigger.DONT:
						_state = WriteState.Option;
						break;
					case Trigger.SB:
						_state = WriteState.SubnegotiationOption;
						break;
				}

				// Anything else (GA, EOR, NOP, …) is a two-byte command and is dropped.
				return null;

			case WriteState.Option:
				_state = WriteState.Data;
				return null;

			case WriteState.SubnegotiationOption:
				_option = b;
				_subnegotiation.SetLength(0);
				_state = b == Iac ? WriteState.SubnegotiationIac : WriteState.Subnegotiation;
				return null;

			case WriteState.Subnegotiation:
				if (b == Iac) _state = WriteState.SubnegotiationIac;
				else _subnegotiation.WriteByte(b);
				return null;

			case WriteState.SubnegotiationIac:
				if (b == Iac)
				{
					_subnegotiation.WriteByte(Iac);
					_state = WriteState.Subnegotiation;
					return null;
				}

				_state = WriteState.Data;
				if (b != (byte)Trigger.SE || !Gmcp || _option != (byte)Trigger.GMCP || _subnegotiation.Length == 0) return null;
				return Encoding.UTF8.GetString(_subnegotiation.GetBuffer(), 0, (int)_subnegotiation.Length);
		}

		return null;
	}

	private async ValueTask FlushTextAsync(CancellationToken cancellationToken)
	{
		if (_text.Length == 0) return;
		var text = _text.ToArray();
		_text.SetLength(0);
		await _connection.SendBinaryAsync(text, cancellationToken).ConfigureAwait(false);
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

	private sealed class Reader(TelnetBridge bridge, Func<ReadOnlyMemory<byte>, ValueTask> onData) : WebSocketMessageHandler
	{
		protected override ValueTask OnOpenAsync(CancellationToken cancellationToken) =>
			bridge.Gmcp ? onData(WillGmcp) : default;

		protected override ValueTask OnBinaryMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
			onData(FromText(data.Span));

		protected override ValueTask OnTextMessageAsync(string text, CancellationToken cancellationToken) =>
			bridge.Gmcp
				? onData(FromGmcp(text))
				: Connection.CloseAsync(WebSocketCloseStatus.InvalidMessageType,
					$"{MudWebSocketSubprotocols.Terminal} carries BINARY frames only", cancellationToken);
	}
}
