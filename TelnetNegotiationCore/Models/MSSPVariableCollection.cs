using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TelnetNegotiationCore.Models;

/// <summary>
/// Every MSSP variable a peer reported, with every value, in the order it reported them.
/// </summary>
/// <remarks>
/// <para>
/// The shape is a map from a canonical variable name (see <see cref="MSSPVariables.Canonicalize"/>)
/// to an <em>ordered list</em> of values, and that is not incidental. MSSP has two ways to attach
/// several values to one variable -- repeating the variable, and repeating <c>MSSP_VAL</c> under one
/// variable -- and gives both the same meaning: "The same variable can be send more than once with
/// different values, in which case the last reported value should be used as the default value", and
/// "It's also possible to attach several values to a single variable by using MSSP_VAL more than
/// once, with the default value reported last". A model holding one value per name cannot represent
/// that, and would lose <c>REFERRAL</c> entirely, since a referral list is nothing but an array.
/// </para>
/// <para>
/// Nothing is discarded on the way in. Variables the specification does not define are kept beside
/// the ones it does, because MSSP exists for servers to describe themselves and a name the library
/// has never heard of is exactly the case that matters.
/// </para>
/// </remarks>
public sealed class MSSPVariableCollection : IReadOnlyDictionary<string, IReadOnlyList<string>>
{
	private static readonly IReadOnlyList<string> None = Array.Empty<string>();
	private static readonly IReadOnlyList<ReadOnlyMemory<byte>> NoBytes = Array.Empty<ReadOnlyMemory<byte>>();

	private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
	private readonly Dictionary<string, List<ReadOnlyMemory<byte>>> _raw = new(StringComparer.Ordinal);
	private readonly List<string> _order = [];

	/// <summary>Variable names in the order the peer first mentioned them.</summary>
	public IEnumerable<string> Keys => _order;

	/// <summary>The value lists, in the same order as <see cref="Keys"/>.</summary>
	public IEnumerable<IReadOnlyList<string>> Values => _order.Select(name => (IReadOnlyList<string>)_values[name]);

	/// <summary>The number of distinct variables reported.</summary>
	public int Count => _order.Count;

	/// <summary>
	/// Every value of <paramref name="variable"/>, in wire order; an empty list when the peer never
	/// mentioned it. The name is canonicalized, so <c>CRAWL_DELAY</c> and <c>crawl delay</c> both
	/// find <c>CRAWL DELAY</c>.
	/// </summary>
	public IReadOnlyList<string> this[string variable] =>
		_values.TryGetValue(MSSPVariables.Canonicalize(variable), out var values) ? values : None;

	/// <summary>The names in this report the specification defines, in wire order.</summary>
	public IEnumerable<string> OfficialNames => _order.Where(MSSPVariables.IsOfficial);

	/// <summary>The names in this report the specification does not define, in wire order.</summary>
	public IEnumerable<string> UnofficialNames => _order.Where(name => !MSSPVariables.IsOfficial(name));

	/// <inheritdoc />
	public bool ContainsKey(string variable) => _values.ContainsKey(MSSPVariables.Canonicalize(variable));

	/// <inheritdoc />
	public bool TryGetValue(string variable, out IReadOnlyList<string> values)
	{
		if (_values.TryGetValue(MSSPVariables.Canonicalize(variable), out var list))
		{
			values = list;
			return true;
		}

		values = None;
		return false;
	}

	/// <summary>
	/// The default value of <paramref name="variable"/> -- the last one reported, per the
	/// specification -- or <see langword="null"/> when the peer did not report it.
	/// </summary>
	public string? Default(string variable)
	{
		var values = this[variable];
		return values.Count == 0 ? null : values[values.Count - 1];
	}

	/// <summary>
	/// The default value of <paramref name="variable"/> read as an MSSP boolean (<c>1</c> or
	/// <c>0</c>), or <see langword="null"/> when unreported or unparseable.
	/// </summary>
	public bool? Flag(string variable) => MSSPValue.TryParseFlag(Default(variable), out var flag) ? flag : null;

	/// <summary>
	/// The default value of <paramref name="variable"/> read as an integer, or
	/// <see langword="null"/> when unreported or unparseable.
	/// </summary>
	/// <remarks>
	/// Negative values are returned as-is: the specification uses <c>-1</c> to mean "data not
	/// available" for the World counts and "use the crawler's default" for <c>CRAWL DELAY</c>, and
	/// both are information a caller needs rather than noise to swallow.
	/// </remarks>
	public int? Integer(string variable) =>
		int.TryParse(Default(variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
			? value
			: null;

	/// <summary>
	/// Every value of <paramref name="variable"/> as it arrived on the wire, in the same order and at
	/// the same indices as <see cref="this[string]"/>; an empty list when the peer never mentioned it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A peer's declared encoding is not a measurement of the bytes it sends. Servers negotiate
	/// CHARSET down to UTF-8 and then send GB18030 or ISO-8859-1 regardless -- sometimes because the
	/// encoding is chosen later in the session than the negotiation, by a menu on the login screen --
	/// and decoding those bytes with the negotiated encoding substitutes <c>U+FFFD</c> for each one it
	/// cannot read. That substitution is not reversible: the original byte is gone. So the bytes are
	/// kept beside the text, and a caller that suspects a field can decide for itself what it holds.
	/// </para>
	/// <para>
	/// These are copies taken as each field closed, not views into a live parse buffer, so they stay
	/// valid for as long as the report does. An entry is empty for a value that never came off a wire
	/// -- one added through <see cref="Add(string, string)"/>, which is how a report a program builds
	/// for itself is assembled. Note that an empty entry is therefore indistinguishable from a value
	/// the peer really did send as zero bytes, which the specification allows.
	/// </para>
	/// </remarks>
	public IReadOnlyList<ReadOnlyMemory<byte>> Raw(string variable) =>
		_raw.TryGetValue(MSSPVariables.Canonicalize(variable), out var bytes) ? bytes : NoBytes;

	/// <summary>
	/// The bytes of the default value of <paramref name="variable"/> -- the last one reported, the
	/// same one <see cref="Default"/> returns -- or <see langword="null"/> when the peer did not
	/// report it. See <see cref="Raw"/> for why this exists.
	/// </summary>
	public ReadOnlyMemory<byte>? RawDefault(string variable)
	{
		var bytes = Raw(variable);
		return bytes.Count == 0 ? null : bytes[bytes.Count - 1];
	}

	/// <summary>
	/// Records one value of one variable. Repeating a variable appends to its list rather than
	/// replacing it, which is what makes the two ways MSSP spells an array land in one place and keep
	/// their order.
	/// </summary>
	/// <param name="variable">The variable name; canonicalized before storage.</param>
	/// <param name="value">The value, exactly as reported.</param>
	/// <returns>This instance, for chaining.</returns>
	public MSSPVariableCollection Add(string variable, string value) => Add(variable, value, default);

	/// <summary>
	/// Records one value of one variable together with the bytes it was decoded from.
	/// </summary>
	/// <param name="variable">The variable name; canonicalized before storage.</param>
	/// <param name="value">The value, exactly as reported.</param>
	/// <param name="raw">
	/// The bytes this value was decoded from. Must be a copy the collection can keep -- see
	/// <see cref="Raw"/> -- rather than a window onto a buffer the caller reuses.
	/// </param>
	/// <returns>This instance, for chaining.</returns>
	public MSSPVariableCollection Add(string variable, string value, ReadOnlyMemory<byte> raw)
	{
		if (value == null) throw new ArgumentNullException(nameof(value));

		var list = ListFor(variable, out var name);
		if (list == null)
		{
			return this;
		}

		list.Add(value);
		_raw[name].Add(raw);
		return this;
	}

	/// <summary>
	/// Records that a variable was mentioned without recording a value for it.
	/// </summary>
	/// <remarks>
	/// A variable with no <c>MSSP_VAL</c> at all is malformed, but "the peer mentioned this and said
	/// nothing" is a different fact from "the peer never mentioned it", and inventing an empty value
	/// to carry the first would erase the difference.
	/// </remarks>
	public MSSPVariableCollection Declare(string variable)
	{
		ListFor(variable);
		return this;
	}

	/// <summary>Removes every variable and value.</summary>
	public void Clear()
	{
		_values.Clear();
		_raw.Clear();
		_order.Clear();
	}

	/// <inheritdoc />
	public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator() =>
		_order.Select(name => new KeyValuePair<string, IReadOnlyList<string>>(name, _values[name])).GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	private List<string>? ListFor(string variable) => ListFor(variable, out _);

	/// <summary>
	/// The value list for a variable, creating it if this is the first mention, along with the
	/// canonical name it was filed under so a caller can reach the parallel byte list without
	/// canonicalizing a second time.
	/// </summary>
	private List<string>? ListFor(string variable, out string name)
	{
		name = MSSPVariables.Canonicalize(variable);
		if (name.Length == 0)
		{
			return null;
		}

		if (!_values.TryGetValue(name, out var list))
		{
			list = [];
			_values[name] = list;
			// Created together and appended to together, so an index into one is an index into the
			// other for the life of the report.
			_raw[name] = [];
			_order.Add(name);
		}

		return list;
	}
}

/// <summary>
/// Conversions between MSSP's wire representation of a value and CLR types.
/// </summary>
public static class MSSPValue
{
	/// <summary>
	/// Parses an MSSP boolean. The specification spells these <c>1</c> and <c>0</c>; <c>true</c>,
	/// <c>false</c>, <c>yes</c> and <c>no</c> are accepted too because real servers send them and
	/// refusing to read them loses data for no gain.
	/// </summary>
	public static bool TryParseFlag(string? value, out bool flag)
	{
		switch (value?.Trim())
		{
			case "1":
				flag = true;
				return true;
			case "0":
				flag = false;
				return true;
			case { } text when text.Equals("true", StringComparison.OrdinalIgnoreCase)
			                   || text.Equals("yes", StringComparison.OrdinalIgnoreCase):
				flag = true;
				return true;
			case { } text when text.Equals("false", StringComparison.OrdinalIgnoreCase)
			                   || text.Equals("no", StringComparison.OrdinalIgnoreCase):
				flag = false;
				return true;
			default:
				flag = false;
				return false;
		}
	}
}
