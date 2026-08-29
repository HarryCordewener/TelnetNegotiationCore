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
