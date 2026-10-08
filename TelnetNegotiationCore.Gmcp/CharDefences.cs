using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// One defence, in the Iron Realms shape. <c>Char.Defences.List</c> and <c>Add</c> carry the name
/// and description; <c>Char.Defences.InfoList</c> adds the rest.
/// </summary>
/// <param name="Name">The defence, such as <c>nightsight</c>.</param>
/// <param name="Desc">Its description.</param>
public sealed record Defence(string Name, string? Desc = null) : IGmcpMessage
{
	/// <summary>A category to group it under. <c>InfoList</c> only.</summary>
	public string? Category { get; init; }

	/// <summary>Whether to call attention to it. Iron Realms sends <c>"0"</c> or <c>"1"</c>, and so does this. <c>InfoList</c> only.</summary>
	public bool? Important { get; init; }

	/// <summary>An icon name, such as <c>eye-slash</c>. <c>InfoList</c> only.</summary>
	public string? Icon { get; init; }

	/// <summary>A color name, such as <c>red</c>. <c>InfoList</c> only.</summary>
	public string? Color { get; init; }

	/// <summary>Sent on its own, a defence is <c>Char.Defences.Add</c>.</summary>
	public string Package => GmcpPackages.CharDefencesAdd;

	/// <inheritdoc />
	public string ToJson() => ToNode().ToJsonString();

	internal JsonObject ToNode() => new JsonFieldWriter()
		.Add("name", Name)
		.Add("desc", Desc)
		.Add("category", Category)
		.Add("important", Important is { } important ? important ? "1" : "0" : null)
		.Add("icon", Icon)
		.Add("color", Color)
		.ToNode();

	internal static Defence Read(JsonFieldReader fields) => new(fields.String("name") ?? "", fields.String("desc"))
	{
		Category = fields.String("category"),
		Important = fields.Boolean("important"),
		Icon = fields.String("icon"),
		Color = fields.String("color")
	};

	/// <summary>Reads a <c>Char.Defences.Add</c> body.</summary>
	public static bool TryParse(string? data, out Defence message) =>
		JsonFieldReader.TryRead(data, Read, out message) && message.Name.Length > 0;
}

/// <summary>
/// <c>Char.Defences.List</c> or <c>Char.Defences.InfoList</c>: every defence the character has.
/// </summary>
/// <param name="Defences">The defences.</param>
/// <param name="Info">True for <c>Char.Defences.InfoList</c>, which carries each defence's category, icon and color.</param>
public sealed record DefencesList(IReadOnlyList<Defence> Defences, bool Info = false) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => Info ? GmcpPackages.CharDefencesInfoList : GmcpPackages.CharDefencesList;

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Array(Defences, defence => defence.ToNode()).ToJsonString();

	/// <summary>Reads a <c>Char.Defences.List</c> or <c>InfoList</c> body, an array.</summary>
	public static bool TryParse(string? data, out DefencesList message, bool info = false)
	{
		var read = JsonFieldReader.TryReadArray(data, Defence.Read, out var defences);
		message = new DefencesList(defences, info);
		return read;
	}
}

/// <summary><c>Char.Defences.Remove</c>: defences the character no longer has, by name.</summary>
/// <param name="Names">The defences' names.</param>
public sealed record DefencesRemove(IReadOnlyList<string> Names) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharDefencesRemove;

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Strings(Names).ToJsonString();

	/// <summary>Reads a <c>Char.Defences.Remove</c> body: an array of names, or one name.</summary>
	public static bool TryParse(string? data, out DefencesRemove message)
	{
		var read = JsonFieldReader.TryReadStrings(data, out var names);
		message = new DefencesRemove(names);
		return read;
	}
}
