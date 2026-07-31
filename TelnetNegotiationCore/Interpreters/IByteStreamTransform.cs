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
