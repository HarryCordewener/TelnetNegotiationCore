using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// Writes a JSON object field by field, leaving out every field that is not set. No reflection.
/// </summary>
internal sealed class JsonFieldWriter
{
	private readonly JsonObject _object = new();

	public JsonFieldWriter Add(string name, string? value)
	{
		if (value is not null)
		{
			_object[name] = JsonValue.Create(value);
		}

		return this;
	}

	public JsonFieldWriter Add(string name, long? value)
	{
		if (value is { } number)
		{
			_object[name] = JsonValue.Create(number);
		}

		return this;
	}

	public JsonFieldWriter Add(string name, bool? value)
	{
		if (value is { } flag)
		{
			_object[name] = JsonValue.Create(flag);
		}

		return this;
	}

	public JsonFieldWriter Add(string name, IEnumerable<string>? values)
	{
		if (values is not null)
		{
			var array = new JsonArray();

			foreach (var value in values)
			{
				array.Add((JsonNode?)JsonValue.Create(value));
			}

			_object[name] = array;
		}

		return this;
	}

	public JsonFieldWriter Add(string name, IEnumerable<KeyValuePair<string, long>>? values)
	{
		if (values is not null)
		{
			var table = new JsonObject();

			foreach (var value in values)
			{
				table[value.Key] = JsonValue.Create(value.Value);
			}

			_object[name] = table;
		}

		return this;
	}

	/// <summary>
	/// Adds each pair as a field of its own, for a message whose extra fields are the game's choice.
	/// A field already written is not replaced.
	/// </summary>
	public JsonFieldWriter AddEach(IEnumerable<KeyValuePair<string, long>>? values)
	{
		if (values is not null)
		{
			foreach (var value in values)
			{
				if (!_object.ContainsKey(value.Key))
				{
					_object[value.Key] = JsonValue.Create(value.Value);
				}
			}
		}

		return this;
	}

	public JsonFieldWriter Add(string name, JsonNode? node)
	{
		if (node is not null)
		{
			_object[name] = node;
		}

		return this;
	}

	/// <summary>The object written so far, to nest in another message.</summary>
	public JsonObject ToNode() => _object;

	public override string ToString() => _object.ToJsonString();
}

/// <summary>
/// Builds the JSON arrays and tables nested in a message. No reflection.
/// </summary>
internal static class JsonNodes
{
	public static JsonArray Array<T>(IEnumerable<T> items, Func<T, JsonNode?> write)
	{
		var array = new JsonArray();

		foreach (var item in items)
		{
			array.Add(write(item));
		}

		return array;
	}

	public static JsonArray Strings(IEnumerable<string> values) => Array(values, value => JsonValue.Create(value));

	public static JsonArray Numbers(IEnumerable<long> values) => Array(values, value => JsonValue.Create(value));

	public static JsonObject Table<T>(IEnumerable<KeyValuePair<string, T>> entries, Func<T, JsonNode?> write)
	{
		var table = new JsonObject();

		foreach (var entry in entries)
		{
			table[entry.Key] = write(entry.Value);
		}

		return table;
	}

	/// <summary>Null when <paramref name="items"/> is null, so the field is left out.</summary>
	public static JsonArray? ArrayOrNull<T>(IEnumerable<T>? items, Func<T, JsonNode?> write) =>
		items is null ? null : Array(items, write);

	/// <summary>Null when <paramref name="entries"/> is null, so the field is left out.</summary>
	public static JsonObject? TableOrNull<T>(IEnumerable<KeyValuePair<string, T>>? entries, Func<T, JsonNode?> write) =>
		entries is null ? null : Table(entries, write);
}

/// <summary>
/// Reads the fields of a JSON object leniently: field names without regard to case, numbers that
/// arrive as strings, and booleans that arrive as strings or numbers, because servers and clients
/// send all of these. No reflection.
/// </summary>
internal readonly struct JsonFieldReader
{
	private readonly JsonElement _object;

	private JsonFieldReader(JsonElement element) => _object = element;

	/// <summary>
	/// Parses <paramref name="data"/> and hands its fields to <paramref name="read"/>. False when the
	/// data is not a JSON object.
	/// </summary>
	public static bool TryRead<T>(string? data, Func<JsonFieldReader, T> read, out T result)
	{
		result = default!;

		if (string.IsNullOrWhiteSpace(data))
		{
			return false;
		}

		try
		{
			using var document = JsonDocument.Parse(data!);

			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			result = read(new JsonFieldReader(document.RootElement));
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	/// <summary>
	/// Parses <paramref name="data"/> as an array and reads each object in it, skipping anything
	/// else. False when the data is not an array.
	/// </summary>
	/// <param name="data">The data section.</param>
	/// <param name="read">Reads one object.</param>
	/// <param name="result">The objects read.</param>
	/// <param name="single">Also read a lone object, as an array of one.</param>
	public static bool TryReadArray<T>(string? data, Func<JsonFieldReader, T> read, out IReadOnlyList<T> result, bool single = false)
	{
		result = [];

		if (!TryParse(data, out var document))
		{
			return false;
		}

		using (document)
		{
			var root = document.RootElement;

			if (single && root.ValueKind == JsonValueKind.Object)
			{
				result = [read(new JsonFieldReader(root))];
				return true;
			}

			if (root.ValueKind != JsonValueKind.Array)
			{
				return false;
			}

			result = ReadObjects(root, read);
			return true;
		}
	}

	/// <summary>Parses <paramref name="data"/> as an array of strings, or a lone string.</summary>
	public static bool TryReadStrings(string? data, out IReadOnlyList<string> result)
	{
		result = [];

		if (!TryParse(data, out var document))
		{
			return false;
		}

		using (document)
		{
			var strings = ReadStrings(document.RootElement);

			if (strings is null)
			{
				return false;
			}

			result = strings;
			return true;
		}
	}

	/// <summary>
	/// Parses <paramref name="data"/>, also accepting a list written in braces,
	/// <c>{ {...}, {...} }</c>, as the <c>mudstd.*</c> pages write their examples.
	/// </summary>
	private static bool TryParse(string? data, out JsonDocument document)
	{
		document = null!;

		if (string.IsNullOrWhiteSpace(data))
		{
			return false;
		}

		try
		{
			document = JsonDocument.Parse(data!);
			return true;
		}
		catch (JsonException)
		{
		}

		var text = data!.Trim();
		var inner = text.Length >= 2 ? text.Substring(1, text.Length - 2) : "";

		if (text.Length < 2 || text[0] != '{' || text[text.Length - 1] != '}' || !inner.TrimStart().StartsWith("{", StringComparison.Ordinal))
		{
			return false;
		}

		try
		{
			document = JsonDocument.Parse("[" + inner + "]");
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static List<T> ReadObjects<T>(JsonElement array, Func<JsonFieldReader, T> read)
	{
		var items = new List<T>();

		foreach (var item in array.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.Object)
			{
				items.Add(read(new JsonFieldReader(item)));
			}
		}

		return items;
	}

	private static List<string>? ReadStrings(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.String)
		{
			return [value.GetString()!];
		}

		if (value.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		var items = new List<string>();

		foreach (var item in value.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.String)
			{
				items.Add(item.GetString()!);
			}
			else if (item.ValueKind == JsonValueKind.Number)
			{
				items.Add(item.GetRawText());
			}
		}

		return items;
	}

	public bool Has(string name) => TryGet(name, out _);

	/// <summary>The object in field <paramref name="name"/>, read with <paramref name="read"/>.</summary>
	public T? Object<T>(string name, Func<JsonFieldReader, T> read) where T : class =>
		TryGet(name, out var value) && value.ValueKind == JsonValueKind.Object ? read(new JsonFieldReader(value)) : null;

	/// <summary>The objects in the array in field <paramref name="name"/>.</summary>
	public IReadOnlyList<T>? Objects<T>(string name, Func<JsonFieldReader, T> read) =>
		TryGet(name, out var value) && value.ValueKind == JsonValueKind.Array ? ReadObjects(value, read) : null;

	/// <summary>The object in field <paramref name="name"/> whose values are objects, keyed by field name.</summary>
	public IReadOnlyDictionary<string, T>? ObjectTable<T>(string name, Func<JsonFieldReader, T> read) =>
		TryGet(name, out var value) && value.ValueKind == JsonValueKind.Object ? new JsonFieldReader(value).Entries(read) : null;

	/// <summary>Every field of this object whose value is an object, keyed by field name.</summary>
	public IReadOnlyDictionary<string, T> Entries<T>(Func<JsonFieldReader, T> read)
	{
		var table = new Dictionary<string, T>(StringComparer.Ordinal);

		foreach (var property in _object.EnumerateObject())
		{
			if (property.Value.ValueKind == JsonValueKind.Object)
			{
				table[property.Name] = read(new JsonFieldReader(property.Value));
			}
		}

		return table;
	}

	/// <summary>The object in field <paramref name="name"/> whose values are strings or numbers, as strings.</summary>
	public IReadOnlyDictionary<string, string>? StringTable(string name)
	{
		if (!TryGet(name, out var value) || value.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		var table = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var property in value.EnumerateObject())
		{
			if (property.Value.ValueKind == JsonValueKind.String)
			{
				table[property.Name] = property.Value.GetString()!;
			}
			else if (property.Value.ValueKind == JsonValueKind.Number)
			{
				table[property.Name] = property.Value.GetRawText();
			}
		}

		return table;
	}

	/// <summary>The raw value of field <paramref name="name"/>, for shapes the other readers do not cover.</summary>
	public bool TryGetElement(string name, out JsonElement value) => TryGet(name, out value);

	public string? String(string name)
	{
		if (!TryGet(name, out var value))
		{
			return null;
		}

		return value.ValueKind switch
		{
			JsonValueKind.String => value.GetString(),
			JsonValueKind.Number => value.GetRawText(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => null
		};
	}

	public long? Number(string name)
	{
		if (!TryGet(name, out var value))
		{
			return null;
		}

		if (value.ValueKind == JsonValueKind.Number)
		{
			return value.TryGetInt64(out var whole) ? whole : (long)Math.Round(value.GetDouble());
		}

		return value.ValueKind == JsonValueKind.String
			&& long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
				? parsed
				: null;
	}

	public bool? Boolean(string name)
	{
		if (!TryGet(name, out var value))
		{
			return null;
		}

		return value.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.Number => value.GetDouble() != 0,
			JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() switch
			{
				"true" or "1" or "yes" => true,
				"false" or "0" or "no" or "" => false,
				_ => null
			},
			_ => null
		};
	}

	/// <summary>
	/// An array of strings, or a single string read as an array of one.
	/// </summary>
	public IReadOnlyList<string>? Strings(string name)
	{
		if (!TryGet(name, out var value))
		{
			return null;
		}

		if (value.ValueKind == JsonValueKind.String)
		{
			return [value.GetString()!];
		}

		if (value.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		var items = new List<string>();

		foreach (var item in value.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.String)
			{
				items.Add(item.GetString()!);
			}
		}

		return items;
	}

	/// <summary>
	/// An object whose values are numbers, such as a room's exits.
	/// </summary>
	public IReadOnlyDictionary<string, long>? NumberTable(string name)
	{
		if (!TryGet(name, out var value) || value.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		var table = new Dictionary<string, long>(StringComparer.Ordinal);

		foreach (var property in value.EnumerateObject())
		{
			if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var number))
			{
				table[property.Name] = number;
			}
			else if (property.Value.ValueKind == JsonValueKind.String
				&& long.TryParse(property.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
			{
				table[property.Name] = parsed;
			}
		}

		return table;
	}

	private bool TryGet(string name, out JsonElement value)
	{
		if (_object.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
		{
			return true;
		}

		foreach (var property in _object.EnumerateObject())
		{
			if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
				&& property.Value.ValueKind != JsonValueKind.Null)
			{
				value = property.Value;
				return true;
			}
		}

		value = default;
		return false;
	}
}
