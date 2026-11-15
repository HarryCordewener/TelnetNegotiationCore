using System;
using System.Threading;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Connections;

/// <summary>
/// The byte stream a <see cref="TelnetConnection"/> runs on: a pipe, a stream, a WebSocket, or your own.
/// </summary>
/// <remarks>
/// <see cref="TelnetTransport"/> makes the built-in ones. Implement this to run telnet over anything else; the
/// interpreter, its plugins and the builder's hooks then work on it unchanged.
/// </remarks>
public interface ITelnetTransport
{
	/// <summary>
	/// Reads until the peer closes or <paramref name="cancellationToken"/> is cancelled, handing each chunk to
	/// <paramref name="onData"/> and awaiting it before the next read.
	/// </summary>
	/// <remarks>
	/// The chunk is only valid for the duration of the call. The task completes when the peer closes, and faults
	/// when the transport fails. Called once.
	/// </remarks>
	Task ReadAsync(Func<ReadOnlyMemory<byte>, ValueTask> onData, CancellationToken cancellationToken);

	/// <summary>Writes <paramref name="data"/>. One call carries one interpreter write.</summary>
	ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

	/// <summary>
	/// Ends the connection from this side: the server dropping a player, say. The read in progress ends soon after.
	/// </summary>
	ValueTask CloseAsync(CancellationToken cancellationToken);
}
