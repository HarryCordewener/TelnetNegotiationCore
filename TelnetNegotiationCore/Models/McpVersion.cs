using System;
using System.Globalization;

namespace TelnetNegotiationCore.Models;

/// <summary>
/// An MCP version: two non-negative integers written <c>major.minor</c>.
/// </summary>
/// <remarks>
/// Ordered rather than merely compared for equality, because every version decision in MCP is a
/// range intersection -- the highest version both sides can speak.
/// </remarks>
public readonly record struct McpVersion(int Major, int Minor) : IComparable<McpVersion>
{
	/// <summary>The major component. Never negative.</summary>
	/// <remarks>
	/// Checked where the value is made rather than where it is written. The grammar has no sign in
	/// it, so a negative component cannot be read back at all: it would go out as <c>-1.0</c> and the
	/// peer's parser -- this library's own included, which reads versions with
	/// <see cref="System.Globalization.NumberStyles.None"/> -- rejects it. Advertising a range a peer
	/// cannot parse is a malformed message rather than a version disagreement.
	/// </remarks>
	public int Major { get; } = Major >= 0
		? Major
		: throw new ArgumentOutOfRangeException(nameof(Major), Major, "An MCP version cannot be negative.");

	/// <inheritdoc cref="Major"/>
	public int Minor { get; } = Minor >= 0
		? Minor
		: throw new ArgumentOutOfRangeException(nameof(Minor), Minor, "An MCP version cannot be negative.");

	/// <inheritdoc />
	public int CompareTo(McpVersion other) =>
		Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

	/// <summary>Whether the left version is lower than the right.</summary>
	public static bool operator <(McpVersion left, McpVersion right) => left.CompareTo(right) < 0;

	/// <summary>Whether the left version is higher than the right.</summary>
	public static bool operator >(McpVersion left, McpVersion right) => left.CompareTo(right) > 0;

	/// <summary>Whether the left version is no higher than the right.</summary>
	public static bool operator <=(McpVersion left, McpVersion right) => left.CompareTo(right) <= 0;

	/// <summary>Whether the left version is no lower than the right.</summary>
	public static bool operator >=(McpVersion left, McpVersion right) => left.CompareTo(right) >= 0;

	/// <inheritdoc />
	public override string ToString() => Major.ToString(CultureInfo.InvariantCulture)
		+ "." + Minor.ToString(CultureInfo.InvariantCulture);
}
