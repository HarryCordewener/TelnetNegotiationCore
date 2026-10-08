using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>One affliction, in the Iron Realms shape.</summary>
/// <param name="Name">The affliction, such as <c>asthma</c>.</param>
/// <param name="Cure">The basic cure, such as <c>eat kelp</c>, which clients link to.</param>
/// <param name="Desc">What the affliction does.</param>
public sealed record Affliction(string Name, string? Cure = null, string? Desc = null) : IGmcpMessage
{
	/// <summary>Sent on its own, an affliction is <c>Char.Afflictions.Add</c>.</summary>
	public string Package => GmcpPackages.CharAfflictionsAdd;

	/// <inheritdoc />
	public string ToJson() => ToNode().ToJsonString();

	internal JsonObject ToNode() => new JsonFieldWriter().Add("name", Name).Add("cure", Cure).Add("desc", Desc).ToNode();

	internal static Affliction Read(JsonFieldReader fields) => new(fields.String("name") ?? "", fields.String("cure"), fields.String("desc"));

	/// <summary>Reads a <c>Char.Afflictions.Add</c> body.</summary>
	public static bool TryParse(string? data, out Affliction message) =>
		JsonFieldReader.TryRead(data, Read, out message) && message.Name.Length > 0;
}

/// <summary><c>Char.Afflictions.List</c>: every affliction the character has.</summary>
/// <param name="Afflictions">The afflictions.</param>
public sealed record AfflictionsList(IReadOnlyList<Affliction> Afflictions) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharAfflictionsList;

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Array(Afflictions, affliction => affliction.ToNode()).ToJsonString();

	/// <summary>Reads a <c>Char.Afflictions.List</c> body, an array.</summary>
	public static bool TryParse(string? data, out AfflictionsList message)
	{
		var read = JsonFieldReader.TryReadArray(data, Affliction.Read, out var afflictions);
		message = new AfflictionsList(afflictions);
		return read;
	}
}

/// <summary><c>Char.Afflictions.Remove</c>: afflictions the character no longer has, by name.</summary>
/// <param name="Names">The afflictions' names.</param>
public sealed record AfflictionsRemove(IReadOnlyList<string> Names) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharAfflictionsRemove;

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Strings(Names).ToJsonString();

	/// <summary>Reads a <c>Char.Afflictions.Remove</c> body: an array of names, or one name.</summary>
	public static bool TryParse(string? data, out AfflictionsRemove message)
	{
		var read = JsonFieldReader.TryReadStrings(data, out var names);
		message = new AfflictionsRemove(names);
		return read;
	}
}
