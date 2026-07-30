using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TelnetNegotiationCore.Generated;

namespace TelnetNegotiationCore.Models;

/// <summary>
/// The MSSP variable vocabulary: the one spelling a variable name is stored under, and which names
/// the specification defines.
/// </summary>
/// <remarks>
/// <para>
/// MSSP says variable names "should exist of upper case letters and may contain spaces", and that
/// "as many programming languages have difficulties with variable names which contain spaces clients
/// and crawlers can substitute spaces with underscores as the recommended solution". A server may
/// therefore legitimately send <c>MINIMUM AGE</c> or <c>MINIMUM_AGE</c> and mean one variable, so a
/// plain case-insensitive lookup would treat them as two. <see cref="Canonicalize"/> is the single
/// place that decides they are the same thing; everything else keys on its output.
/// </para>
/// <para>
/// Canonical form is the spaced, upper-case spelling the specification's own tables print, because
/// that is what a server operator recognises from their own configuration. The underscore form is an
/// encoding convenience, not the name. <c>UTF-8</c> keeps its hyphen — no underscore is involved.
/// </para>
/// <para>See <see href="https://mudhalla.net/tintin/protocols/mssp/">the MSSP specification</see>.</para>
/// </remarks>
public static class MSSPVariables
{
	private static readonly HashSet<string> KnownNames =
		new(MSSPConfigAccessor.PropertyMap.Keys.Select(Canonicalize), StringComparer.Ordinal);

	private static readonly HashSet<string> OfficialNames =
		new(MSSPConfigAccessor.PropertyMap
			.Where(entry => entry.Value.IsOfficial)
			.Select(entry => Canonicalize(entry.Key)), StringComparer.Ordinal);

	/// <summary>
	/// Every variable name the specification lists as official, in the order
	/// <see cref="MSSPConfig"/> declares them.
	/// </summary>
	public static IReadOnlyCollection<string> Official { get; } =
		MSSPConfigAccessor.PropertyMap
			.Where(entry => entry.Value.IsOfficial)
			.Select(entry => Canonicalize(entry.Key))
			.ToArray();

	/// <summary>
	/// The one spelling a variable is stored under: trimmed, upper-cased, underscores folded to
	/// spaces, and runs of whitespace collapsed to a single space.
	/// </summary>
	/// <remarks>
	/// The underscore folding is deliberately unconditional rather than restricted to recognized
	/// names: a server that spells one of its own invented variables both ways in a single payload
	/// should still get a single entry, and there is no way to know which spelling it considers
	/// canonical.
	/// </remarks>
	/// <param name="variable">The variable name as it arrived on the wire.</param>
	/// <returns>The canonical spelling, which may be empty if <paramref name="variable"/> held no
	/// name characters.</returns>
	public static string Canonicalize(string variable)
	{
		if (variable == null) throw new ArgumentNullException(nameof(variable));

		var folded = new StringBuilder(variable.Length);
		var pendingSpace = false;

		foreach (var character in variable)
		{
			var c = character == '_' ? ' ' : character;

			if (char.IsWhiteSpace(c))
			{
				// Leading whitespace is dropped, interior runs become one space -- decided when the
				// next real character arrives, so a trailing run costs nothing.
				pendingSpace = folded.Length > 0;
				continue;
			}

			if (pendingSpace)
			{
				folded.Append(' ');
				pendingSpace = false;
			}

			folded.Append(char.ToUpperInvariant(c));
		}

		return folded.ToString();
	}

	/// <summary>
	/// Whether <paramref name="variable"/> is one of the specification's official variables.
	/// </summary>
	public static bool IsOfficial(string variable) => OfficialNames.Contains(Canonicalize(variable));

	/// <summary>
	/// Whether <paramref name="variable"/> has a strongly typed property on <see cref="MSSPConfig"/>.
	/// Variables that do not are still kept in full -- see <see cref="MSSPConfig.Variables"/> and
	/// <see cref="MSSPConfig.Extended"/>.
	/// </summary>
	public static bool IsKnown(string variable) => KnownNames.Contains(Canonicalize(variable));
}
