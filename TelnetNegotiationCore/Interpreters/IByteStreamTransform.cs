using System;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// Transforms the raw inbound byte stream before the telnet state machine sees any of it.
/// </summary>
/// <remarks>
/// A protocol whose negotiation changes the meaning of every byte that follows it — MCCP is the
/// only one in this library, but stream ciphers work the same way — installs one of these through
/// <see cref="Plugins.IProtocolContext.SetInboundByteTransform"/>. Until it is installed the wire
/// bytes are telnet bytes; after it, the wire bytes are the transform's input and only what it
/// returns is telnet.
///
/// The interpreter reads bytes off a single-reader channel, so implementations are called from one
/// thread and need no locking of their own.
/// </remarks>
public interface IInboundByteTransform : IDisposable
{
	/// <summary>
	/// Decodes one byte as it arrived on the wire.
	/// </summary>
	/// <param name="raw">The byte read from the network.</param>
	/// <returns>
	/// The bytes the telnet state machine should see for it: empty while the transform is still
	/// accumulating, one or many once it can decode. The returned memory is owned by the transform
	/// and is only valid until the next call, so callers must consume it before decoding again.
	/// </returns>
	/// <remarks>
	/// Asynchronous only so that a transform which fails terminally can tell someone before it
	/// returns — decoding itself is pure computation, and an implementation that has nothing to
	/// report should return an already-completed <see cref="ValueTask{TResult}"/> rather than
	/// being an <c>async</c> method, so the per-byte path stays allocation free.
	/// </remarks>
	ValueTask<ReadOnlyMemory<byte>> DecodeAsync(byte raw);
}

/// <summary>
/// What one <see cref="IInboundChunkTransform.DecodeChunkAsync"/> call did.
/// </summary>
internal readonly struct InboundDecodeResult(int consumed, ReadOnlyMemory<byte> decoded, bool hasMoreOutput)
{
	/// <summary>How many of the wire bytes passed in this call are finished with.</summary>
	public int Consumed { get; } = consumed;

	/// <summary>Telnet bytes for the state machine, owned by the transform and valid until its next call.</summary>
	public ReadOnlyMemory<byte> Decoded { get; } = decoded;

	/// <summary>Whether a further call may produce more output, even with no new input.</summary>
	public bool HasMoreOutput { get; } = hasMoreOutput;
}

/// <summary>
/// An inbound transform that takes the wire a chunk at a time and returns bounded slices of output.
/// </summary>
/// <remarks>
/// <para>
/// The interpreter prefers this over <see cref="IInboundByteTransform.DecodeAsync"/> when a transform
/// offers it. A call may consume none, some or all of its input, and may produce output without
/// consuming anything. The caller fires what comes back into the state machine before calling again,
/// passes the unconsumed rest of the wire next time, and keeps calling while input is left or
/// <see cref="InboundDecodeResult.HasMoreOutput"/> is true.
/// </para>
/// <para>
/// Bytes a transform leaves unconsumed when it finishes (its stream ended) are telnet again; the
/// caller gives them to whatever is installed next, the state machine included.
/// </para>
/// </remarks>
internal interface IInboundChunkTransform : IInboundByteTransform
{
	/// <summary>Decodes from the start of <paramref name="wire"/>.</summary>
	ValueTask<InboundDecodeResult> DecodeChunkAsync(ReadOnlyMemory<byte> wire);
}

/// <summary>
/// Transforms outbound bytes on their way to the network, after everything else in the library has
/// had its say.
/// </summary>
/// <remarks>
/// Installed through <see cref="Plugins.IProtocolContext.SetOutboundByteTransformAsync"/>. The
/// interpreter calls this inside its write lock, so calls are serialized and arrive in the order
/// they will be written — which is what a stateful encoder such as a zlib deflater requires. The
/// same lock is taken to install and remove one, so <see cref="IDisposable.Dispose"/> is never
/// called while a write is inside <see cref="Encode"/>.
/// </remarks>
public interface IOutboundByteTransform : IDisposable
{
	/// <summary>
	/// Encodes one write.
	/// </summary>
	/// <param name="data">The bytes the library wants the peer to receive.</param>
	/// <returns>
	/// The bytes to actually put on the wire. An encoder that buffers must flush whatever it needs
	/// to for the peer to decode this write immediately; nothing else will prompt it to.
	/// </returns>
	ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> data);
}

/// <summary>
/// An outbound encoder whose stream has an orderly end the peer is waiting to see.
/// </summary>
/// <remarks>
/// Removing such an encoder without ending its stream leaves the peer decoding plain bytes as the
/// middle of the old stream. MCCP is the case in point: "the client may terminate compression at
/// any point by sending an orderly stream end (Z_FINISH)", and the server only switches back to
/// plain telnet when it reads one.
/// </remarks>
internal interface IFinishableOutboundTransform : IOutboundByteTransform
{
	/// <summary>
	/// Ends the stream and returns the bytes that end it. Called once, inside the write lock, as the
	/// last use of the encoder before it is removed.
	/// </summary>
	ReadOnlyMemory<byte> Finish();
}
