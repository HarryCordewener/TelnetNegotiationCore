using System;
using System.Collections.Generic;

namespace TelnetNegotiationCore.Helpers;

/// <summary>
/// Accumulates the payload of a single subnegotiation - everything between <c>IAC SB &lt;option&gt;</c>
/// and <c>IAC SE</c> - and enforces a maximum size on it.
/// </summary>
/// <remarks>
/// <para>
/// Subnegotiation payloads arrive from an untrusted peer on the read loop, so they need an upper
/// bound. They also need to be delivered <em>whole</em>: a GMCP or MSDP message that has had its
/// tail removed is not a smaller message, it is a corrupt one, and a consumer parsing it as JSON
/// cannot tell a truncated payload from a malformed server.
/// </para>
/// <para>
/// This buffer therefore records that it overflowed rather than quietly dropping the excess.
/// Callers are expected to check <see cref="Overflowed"/> when the subnegotiation completes and
/// discard the message with a diagnostic, instead of handing a partial payload to the consumer.
/// </para>
/// </remarks>
internal sealed class SubnegotiationBuffer
{
	/// <summary>
	/// The default maximum size of a single subnegotiation payload, in bytes.
	/// </summary>
	/// <remarks>
	/// Neither the GMCP nor the MSDP specification defines a maximum message size, so this is a
	/// library policy rather than a protocol constant: large enough that no realistic package
	/// (inventory listings, room contents, translation tables) hits it, small enough that a hostile
	/// or broken peer cannot grow the buffer without limit. It is configurable per protocol.
	/// </remarks>
	public const int DefaultMaxMessageSize = 1024 * 1024;

	/// <summary>
	/// Starting capacity. Small messages are the overwhelmingly common case.
	/// </summary>
	private const int InitialCapacity = 256;

	/// <summary>
	/// Above this capacity the backing array is released on <see cref="Reset"/> rather than kept,
	/// so that one large message does not hold onto its buffer for the rest of the connection.
	/// </summary>
	private const int RetainedCapacity = 64 * 1024;

	private readonly List<byte> _bytes = new(InitialCapacity);
	private int _maxMessageSize;

	/// <param name="maxMessageSize">The maximum payload size in bytes.</param>
	public SubnegotiationBuffer(int maxMessageSize = DefaultMaxMessageSize)
	{
		MaxMessageSize = maxMessageSize;
	}

	/// <summary>
	/// The maximum payload size, in bytes. Bytes beyond this are not stored and the message is
	/// marked as <see cref="Overflowed"/>.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
	public int MaxMessageSize
	{
		get => _maxMessageSize;
		set
		{
			if (value <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(value), value, "Maximum message size must be positive.");
			}

			_maxMessageSize = value;
		}
	}

	/// <summary>
	/// The bytes accumulated so far. Never longer than <see cref="MaxMessageSize"/>.
	/// </summary>
	public List<byte> Bytes => _bytes;

	/// <summary>
	/// The number of bytes accumulated so far.
	/// </summary>
	public int Count => _bytes.Count;

	/// <summary>
	/// The number of bytes the peer sent for this message, including any that did not fit.
	/// </summary>
	public long ReceivedBytes { get; private set; }

	/// <summary>
	/// True when the peer sent more than <see cref="MaxMessageSize"/> bytes, meaning the buffered
	/// payload is incomplete and must not be delivered as if it were the whole message.
	/// </summary>
	public bool Overflowed { get; private set; }

	/// <summary>
	/// Appends a payload byte, or records an overflow if the maximum has been reached.
	/// </summary>
	public void Add(byte value)
	{
		ReceivedBytes++;

		if (_bytes.Count >= _maxMessageSize)
		{
			Overflowed = true;
			return;
		}

		_bytes.Add(value);
	}

	/// <summary>
	/// Clears the buffer, ready for the next subnegotiation.
	/// </summary>
	public void Reset()
	{
		_bytes.Clear();

		if (_bytes.Capacity > RetainedCapacity)
		{
			_bytes.Capacity = InitialCapacity;
		}

		ReceivedBytes = 0;
		Overflowed = false;
	}
}
