using System;
using System.Globalization;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// A GMCP module and its version, as <c>Core.Supports.Set</c> lists it: <c>"Char.Skills 1"</c>.
/// </summary>
/// <remarks>
/// "Message body is an array of strings, each consisting of the module name and version, separated
/// by space. Module version is a positive non-zero integer."
/// </remarks>
/// <param name="Name">The module name, such as <c>Char</c> or <c>Char.Skills</c>.</param>
/// <param name="Version">The module version, a positive integer.</param>
public readonly record struct GmcpModule(string Name, int Version)
{
	/// <summary>
	/// Reads one entry of a <c>Core.Supports</c> list. The version is optional, because
	/// <c>Core.Supports.Remove</c> lists names alone; a missing or unreadable version reads as 1.
	/// </summary>
	/// <param name="entry">The entry, such as <c>"Char 1"</c> or <c>"Char"</c>.</param>
	/// <param name="module">The module, when the entry names one.</param>
	/// <returns>False for an entry with no name.</returns>
	public static bool TryParse(string? entry, out GmcpModule module)
	{
		module = default;
		var text = entry?.Trim();

		if (string.IsNullOrEmpty(text))
		{
			return false;
		}

		var space = text!.IndexOf(' ');
		var name = space < 0 ? text : text.Substring(0, space);
		var version = 1;

		if (space >= 0
			&& int.TryParse(text.Substring(space + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
			&& parsed > 0)
		{
			version = parsed;
		}

		module = new GmcpModule(name, version);
		return true;
	}

	/// <summary>
	/// The entry as <c>Core.Supports.Set</c> and <c>Add</c> send it: <c>"Char.Skills 1"</c>.
	/// </summary>
	public override string ToString() => $"{Name} {Version.ToString(CultureInfo.InvariantCulture)}";
}
