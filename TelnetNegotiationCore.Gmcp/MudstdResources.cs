using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// One resource or attribute the game tracks, as <c>mudstd.resources.definitions</c>,
/// <c>mudstd.char.resources.definitions</c> and <c>mudstd.char.attributes.definitions</c> list them.
/// </summary>
/// <param name="Id">Names it in updates. ASCII letters only.</param>
/// <param name="Label">A name to show, such as <c>Hit Points</c>.</param>
public sealed record StatDefinition(string Id, string Label)
{
	/// <summary>A 2 or 3 letter name for tables and bars, such as <c>HP</c>. Recommended.</summary>
	public string? Abbrev { get; init; }

	/// <summary>An RGB color in hex, such as <c>FF0000</c>.</summary>
	public string? Color { get; init; }

	internal JsonObject ToNode() => new JsonFieldWriter().Add("id", Id).Add("label", Label).Add("abbrev", Abbrev).Add("color", Color).ToNode();

	internal static StatDefinition Read(JsonFieldReader fields) => new(fields.String("id") ?? "", fields.String("label") ?? "")
	{
		Abbrev = fields.String("abbrev"),
		Color = fields.String("color")
	};
}

/// <summary>
/// The state of one resource or attribute, as <c>mudstd.resources.update</c>,
/// <c>mudstd.char.resources.update</c> and <c>mudstd.char.attributes.update</c> send it. Values
/// are strings because the page sends them as strings; they are "usually numerical".
/// </summary>
/// <param name="Id">The <see cref="StatDefinition.Id"/> it updates.</param>
/// <param name="Current">The current value.</param>
public sealed record StatValue(string Id, string Current)
{
	/// <summary>The maximum. Required for a resource, optional for an attribute.</summary>
	public string? Max { get; init; }

	/// <summary>The maximum while a temporary effect changes it. Resources only.</summary>
	public string? TempMax { get; init; }

	/// <summary>How much is held back to sustain an effect. Resources only.</summary>
	public string? Blocked { get; init; }

	internal JsonObject ToNode() => new JsonFieldWriter()
		.Add("id", Id)
		.Add("current", Current)
		.Add("max", Max)
		.Add("tempmax", TempMax)
		.Add("blocked", Blocked)
		.ToNode();

	internal static StatValue Read(JsonFieldReader fields) => new(fields.String("id") ?? "", fields.String("current") ?? "")
	{
		Max = fields.String("max"),
		TempMax = fields.String("tempmax"),
		Blocked = fields.String("blocked")
	};
}

/// <summary>Which <c>mudstd</c> stat a message carries, and so its package.</summary>
public enum StatKind
{
	/// <summary><c>mudstd.resources.*</c>: fast-changing energies such as health and mana.</summary>
	Resources,

	/// <summary><c>mudstd.char.resources.*</c>: the same, under the character. The proposal has not chosen between the two.</summary>
	CharResources,

	/// <summary><c>mudstd.char.attributes.*</c>: attributes such as strength.</summary>
	CharAttributes
}

/// <summary>
/// <c>mudstd.resources.definitions</c>, <c>mudstd.char.resources.definitions</c> or
/// <c>mudstd.char.attributes.definitions</c>: every resource or attribute the game tracks, which may
/// include some that do not apply to this character.
/// </summary>
/// <remarks>
/// The page writes the list in braces, which is not JSON. This sends a JSON array and reads either.
/// </remarks>
/// <param name="Definitions">The definitions.</param>
/// <param name="Kind">Which package.</param>
public sealed record StatDefinitions(IReadOnlyList<StatDefinition> Definitions, StatKind Kind = StatKind.Resources) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => Kind switch
	{
		StatKind.CharResources => GmcpPackages.MudstdCharResourcesDefinitions,
		StatKind.CharAttributes => GmcpPackages.MudstdCharAttributesDefinitions,
		_ => GmcpPackages.MudstdResourcesDefinitions
	};

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Array(Definitions, definition => definition.ToNode()).ToJsonString();

	/// <summary>Reads a definitions body.</summary>
	public static bool TryParse(string? data, out StatDefinitions message, StatKind kind = StatKind.Resources)
	{
		var read = JsonFieldReader.TryReadArray(data, StatDefinition.Read, out var definitions);
		message = new StatDefinitions(definitions, kind);
		return read;
	}
}

/// <summary>
/// <c>mudstd.resources.update</c>, <c>mudstd.char.resources.update</c> or
/// <c>mudstd.char.attributes.update</c>: the current values. Sent on a timer or on change.
/// </summary>
/// <remarks>
/// The page writes the list in braces, which is not JSON. This sends a JSON array and reads either.
/// </remarks>
/// <param name="Values">The values.</param>
/// <param name="Kind">Which package.</param>
public sealed record StatUpdate(IReadOnlyList<StatValue> Values, StatKind Kind = StatKind.Resources) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => Kind switch
	{
		StatKind.CharResources => GmcpPackages.MudstdCharResourcesUpdate,
		StatKind.CharAttributes => GmcpPackages.MudstdCharAttributesUpdate,
		_ => GmcpPackages.MudstdResourcesUpdate
	};

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Array(Values, value => value.ToNode()).ToJsonString();

	/// <summary>Reads an update body.</summary>
	public static bool TryParse(string? data, out StatUpdate message, StatKind kind = StatKind.Resources)
	{
		var read = JsonFieldReader.TryReadArray(data, StatValue.Read, out var values);
		message = new StatUpdate(values, kind);
		return read;
	}
}
