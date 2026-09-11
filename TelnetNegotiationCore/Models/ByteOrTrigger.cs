using System.Runtime.CompilerServices;

namespace TelnetNegotiationCore.Models;

/// <summary>
/// The parameter every parameterized trigger carries: the byte that arrived, or a <see cref="Trigger"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every data trigger carries the byte it was read from. Only the interpreter's own
/// <see cref="Trigger.Error"/> recovery fire carries a trigger, and no handler is registered on it, so
/// the handlers match <c>is byte</c>.
/// </para>
/// <para>
/// Written out rather than declared as <c>union ByteOrTrigger(byte, Trigger);</c>, which would store
/// the value in an <c>object?</c> and box every byte read. The <c>HasValue</c>/<c>TryGetValue</c>
/// members are the non-boxing access pattern, which the compiler uses to match without touching
/// <see cref="Value"/>.
/// </para>
/// </remarks>
[Union]
internal readonly struct ByteOrTrigger : IUnion
{
	private enum Kind : byte { None, Byte, Trigger }

	private readonly short _raw;
	private readonly Kind _kind;

	public ByteOrTrigger(byte value) => (_raw, _kind) = (value, Kind.Byte);

	public ByteOrTrigger(Trigger value) => (_raw, _kind) = ((short)value, Kind.Trigger);

	/// <summary>The byte or trigger, boxed; null for <c>default</c>.</summary>
	public object? Value => _kind switch
	{
		Kind.Byte => (byte)_raw,
		Kind.Trigger => (Trigger)_raw,
		_ => null
	};

	public bool HasValue => _kind != Kind.None;

	public bool TryGetValue(out byte value)
	{
		value = (byte)_raw;
		return _kind == Kind.Byte;
	}

	public bool TryGetValue(out Trigger value)
	{
		value = (Trigger)_raw;
		return _kind == Kind.Trigger;
	}

	/// <summary>The value alone -- <c>65</c>, <c>Error</c> -- as the transition trace log prints it.</summary>
	public override string ToString() => _kind switch
	{
		Kind.Byte => ((byte)_raw).ToString(),
		Kind.Trigger => ((Trigger)_raw).ToString(),
		_ => string.Empty
	};
}
