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

	public override string ToString() => _object.ToJsonString();
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

	public bool Has(string name) => TryGet(name, out _);

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
