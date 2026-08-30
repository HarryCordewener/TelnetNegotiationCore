using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TelnetNegotiationCore.Models;

/// <summary>
/// One MCP message: a name, the authentication key that proves it is protocol rather than someone
/// typing, and its keyword arguments.
/// </summary>
public sealed class McpMessage
{
	/// <summary>The name of the handshake message: the only one that carries no key.</summary>
	public const string Handshake = "mcp";

	private readonly Dictionary<string, string> _values;

	private readonly Dictionary<string, List<string>> _lines;

	private McpMessage(
		string name,
		string? authenticationKey,
		Dictionary<string, string> values,
		Dictionary<string, List<string>> lines)
	{
		Name = name;
		AuthenticationKey = authenticationKey;
		_values = values;
		_lines = lines;
	}

	/// <summary>The message name, lowercased: <c>mcp</c>, <c>mcp-negotiate-can</c>, and so on.</summary>
	public string Name { get; }

	/// <summary>
	/// The key the message carried, or <see langword="null"/> for the handshake, which is the one
	/// message sent before a key exists.
	/// </summary>
	public string? AuthenticationKey { get; }

	/// <summary>The keyword arguments, keyed by lowercased name.</summary>
	public IReadOnlyDictionary<string, string> Values => _values;

	/// <summary>
	/// The tag that ties this message to its continuation lines, or <see langword="null"/> if it has
	/// none.
	/// </summary>
	public string? DataTag => Value("_data-tag");

	/// <summary>
	/// Whether this message declared continuation keys, and so is not complete until the line that
	/// closes its <see cref="DataTag"/> arrives.
	/// </summary>
	public bool IsMultiline => _lines.Count > 0;

	/// <summary>
	/// The lines accumulated for a continuation key, in the order the peer sent them. Empty for a key
	/// this message did not declare with a trailing <c>*</c>.
	/// </summary>
	public IReadOnlyList<string> Lines(string key) =>
		_lines.TryGetValue(key.ToLowerInvariant(), out var lines) ? lines : Array.Empty<string>();

	/// <summary>
	/// Adds one continuation line. Returns false if the key is not one this message declared, which
	/// is a peer sending data for a key it never opened.
	/// </summary>
	internal bool AppendLine(string key, string text)
	{
		if (!_lines.TryGetValue(key.ToLowerInvariant(), out var lines)) return false;

		lines.Add(text);
		return true;
	}

	/// <summary>The number of continuation lines accumulated so far, across every key.</summary>
	internal int LineCount
	{
		get
		{
			var total = 0;

			foreach (var lines in _lines.Values) total += lines.Count;

			return total;
		}
	}

	/// <summary>The value of <paramref name="key"/>, or <see langword="null"/> if it was not sent.</summary>
	public string? Value(string key) => _values.TryGetValue(key.ToLowerInvariant(), out var value) ? value : null;

	/// <summary>
	/// Reads <paramref name="key"/> as an MCP version -- two non-negative integers written
	/// <c>major.minor</c>.
	/// </summary>
	public bool TryGetVersion(string key, out McpVersion version)
	{
		version = default;

		var text = Value(key);

		if (text is null) return false;

		var dot = text.IndexOf('.');

		if (dot <= 0 || dot == text.Length - 1) return false;

		return int.TryParse(text.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var major)
			&& int.TryParse(text.Substring(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
			&& Set(out version, new McpVersion(major, minor));
	}

	private static bool Set(out McpVersion version, McpVersion value)
	{
		version = value;
		return true;
	}

	/// <summary>
	/// Reads the body of an MCP line -- everything after the <c>#$#</c> -- into a message.
	/// </summary>
	/// <remarks>
	/// Whether an authentication key stands between the name and the arguments is decided by the
	/// name, because that is how MCP decides it: the handshake is sent before a key exists and so
	/// carries none, and every other message carries one.
	/// </remarks>
	/// <param name="body">The line with its <c>#$#</c> prefix already removed</param>
	/// <param name="message">The parsed message, or null</param>
	/// <returns>True if the body is a well-formed MCP message.</returns>
	public static bool TryParse(string body, out McpMessage? message)
	{
		message = null;

		var at = 0;

		var name = ReadToken(body, ref at);

		if (name is null || !IsMessageName(name)) return false;

		var lowered = name.ToLowerInvariant();
		string? key = null;

		if (lowered != Handshake)
		{
			key = ReadToken(body, ref at);

			if (key is null) return false;
		}

		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		var lines = new Dictionary<string, List<string>>(StringComparer.Ordinal);

		while (true)
		{
			SkipSpaces(body, ref at);

			if (at >= body.Length) break;

			var argument = ReadToken(body, ref at);

			if (argument is null || !argument.EndsWith(":", StringComparison.Ordinal)) return false;

			var argumentName = argument.Substring(0, argument.Length - 1).ToLowerInvariant();

			// A trailing * says the value does not arrive here: it arrives on the continuation lines
			// that quote this message's data tag. What is written in its place -- "" by convention --
			// is not the value and is not kept.
			var isContinuation = argumentName.EndsWith("*", StringComparison.Ordinal);

			if (isContinuation) argumentName = argumentName.Substring(0, argumentName.Length - 1);

			if (argumentName.Length == 0 || !IsArgumentName(argumentName)) return false;

			var value = ReadValue(body, ref at);

			if (value is null) return false;

			// Mangled: the specification forbids sending the same keyword twice and gives a receiver no
			// way to resolve one, so taking either value is a guess. The peer believes it said
			// something specific, and whichever half were kept would not reliably be the one it meant.
			if (values.ContainsKey(argumentName) || lines.ContainsKey(argumentName)) return false;

			if (isContinuation)
			{
				lines[argumentName] = [];
			}
			else
			{
				values[argumentName] = value;
			}
		}

		// A message that opens continuation keys but names no tag cannot be continued or closed, and a
		// tag on its own is harmless. Refusing the first is what stops it being held open forever.
		if (lines.Count > 0 && !values.ContainsKey("_data-tag")) return false;

		message = new McpMessage(lowered, key, values, lines);
		return true;
	}

	private static void SkipSpaces(string body, ref int at)
	{
		while (at < body.Length && body[at] == ' ') at++;
	}

	/// <summary>An unquoted run of characters, ended by a space or the end of the line.</summary>
	private static string? ReadToken(string body, ref int at)
	{
		SkipSpaces(body, ref at);

		var start = at;

		while (at < body.Length && body[at] != ' ') at++;

		return at == start ? null : body.Substring(start, at - start);
	}

	/// <summary>
	/// An argument's value: either a quoted string, with backslash escapes, or an unquoted run.
	/// </summary>
	private static string? ReadValue(string body, ref int at)
	{
		SkipSpaces(body, ref at);

		if (at >= body.Length) return null;

		if (body[at] != '"') return ReadToken(body, ref at);

		at++;

		var value = new StringBuilder();

		while (at < body.Length)
		{
			var c = body[at++];

			switch (c)
			{
				case '"':
					return value.ToString();
				case '\\' when at < body.Length:
					value.Append(body[at++]);
					break;
				default:
					value.Append(c);
					break;
			}
		}

		// Ran off the end of the line with the quote still open.
		return null;
	}

	/// <summary>
	/// Whether a string is an MCP identifier: a letter, then letters, digits and hyphens. Message
	/// names, keywords and package names are all built on this one production, so they are all checked
	/// against this one method.
	/// </summary>
	public static bool IsIdentifier(string? value) => value is not null && IsMessageName(value);

	/// <summary>
	/// A message name: a letter, then letters, digits and hyphens. Checked rather than assumed,
	/// because the alternative is treating a line of ordinary output that happens to start with
	/// <c>#$#</c> as protocol.
	/// </summary>
	private static bool IsMessageName(string name)
	{
		if (name.Length == 0 || !IsLetter(name[0])) return false;

		foreach (var c in name)
		{
			if (!IsLetter(c) && !IsDigit(c) && c != '-') return false;
		}

		return true;
	}

	/// <summary>
	/// An argument name, which is an <c>&lt;ident&gt;</c> like a message name. The protocol's own keys
	/// -- <c>_data-tag</c>, and cords' <c>_id</c>, <c>_type</c> and <c>_message</c> -- need no special
	/// case: an underscore is a letter here.
	/// </summary>
	private static bool IsArgumentName(string name)
	{
		if (name.Length == 0 || !IsLetter(name[0])) return false;

		foreach (var c in name)
		{
			if (!IsLetter(c) && !IsDigit(c) && c != '-') return false;
		}

		return true;
	}

	/// <summary>
	/// MCP's <c>&lt;alpha&gt;</c>, which includes the underscore:
	/// <c>&lt;alpha&gt; ::= 'a' | ... | 'z' | 'A' | ... | 'Z' | '_'</c>.
	/// </summary>
	/// <remarks>
	/// The underscore being a letter is what makes <c>_data-tag</c> a keyword at all -- a keyword is
	/// an <c>&lt;ident&gt;</c>, and an <c>&lt;ident&gt;</c> begins with an <c>&lt;alpha&gt;</c>. It
	/// follows into <c>&lt;simple-char&gt;</c> too, so an authentication key may hold one.
	/// </remarks>
	private static bool IsLetter(char c) =>
		(c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

	private static bool IsDigit(char c) => c >= '0' && c <= '9';
}
