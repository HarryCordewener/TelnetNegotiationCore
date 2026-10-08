using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// The little JSON the Core package needs, read and written without reflection.
/// </summary>
internal static class GmcpJson
{
	/// <summary>
	/// A JSON string literal: <c>"text"</c>, escaped.
	/// </summary>
	public static string String(string text) => JsonValue.Create(text).ToJsonString();

	/// <summary>
	/// A JSON array of strings.
	/// </summary>
	public static string Strings(IEnumerable<string> items)
	{
		var array = new JsonArray();

		foreach (var item in items)
		{
			array.Add((JsonNode?)JsonValue.Create(item));
		}

		return array.ToJsonString();
	}

	/// <summary>
	/// The strings of a JSON array, or null when the data is not one. Entries that are not strings
	/// are skipped.
	/// </summary>
	public static List<string>? ReadStrings(string data)
	{
		if (!TryParse(data, out var document))
		{
			return null;
		}

		using (document)
		{
			if (document!.RootElement.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			var items = new List<string>();

			foreach (var element in document.RootElement.EnumerateArray())
			{
				if (element.ValueKind == JsonValueKind.String)
				{
					items.Add(element.GetString()!);
				}
			}

			return items;
		}
	}

	/// <summary>
	/// A JSON string's text, or null when the data is not a JSON string.
	/// </summary>
	public static string? ReadString(string data)
	{
		if (!TryParse(data, out var document))
		{
			return null;
		}

		using (document)
		{
			return document!.RootElement.ValueKind == JsonValueKind.String
				? document.RootElement.GetString()
				: null;
		}
	}

	/// <summary>
	/// A JSON number's value, or null when the data is empty or not a number.
	/// </summary>
	public static double? ReadNumber(string data)
	{
		if (!TryParse(data, out var document))
		{
			return null;
		}

		using (document)
		{
			return document!.RootElement.ValueKind == JsonValueKind.Number
				? document.RootElement.GetDouble()
				: null;
		}
	}

	/// <summary>
	/// The named properties of a JSON object, matched without regard to case, as text. A string
	/// property reads as its text and any other value as its JSON; a missing one reads as null.
	/// </summary>
	public static string?[]? ReadProperties(string data, params string[] names)
	{
		if (!TryParse(data, out var document))
		{
			return null;
		}

		using (document)
		{
			if (document!.RootElement.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			var values = new string?[names.Length];

			foreach (var property in document.RootElement.EnumerateObject())
			{
				var index = Array.FindIndex(names, name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase));

				if (index >= 0 && values[index] is null)
				{
					values[index] = property.Value.ValueKind == JsonValueKind.String
						? property.Value.GetString()
						: property.Value.GetRawText();
				}
			}

			return values;
		}
	}

	private static bool TryParse(string data, out JsonDocument? document)
	{
		document = null;

		if (string.IsNullOrWhiteSpace(data))
		{
			return false;
		}

		try
		{
			document = JsonDocument.Parse(data);
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}
}
