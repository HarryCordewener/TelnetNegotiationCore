using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TelnetNegotiationCore.TestServer;

/// <summary>
/// A room, shaped like the table in the MSDP specification
/// (https://tintin.mudhalla.net/protocols/msdp/): a game's own type, handed to MSDP as the value of
/// the ROOM variable.
/// </summary>
/// <remarks>
/// The property names are the MSDP variable names, so they are spelled here rather than left to a
/// naming policy.
/// </remarks>
public sealed class Room
{
	[JsonPropertyName("VNUM")]
	public int Vnum { get; set; }

	[JsonPropertyName("NAME")]
	public string Name { get; set; } = "";

	[JsonPropertyName("AREA")]
	public string Area { get; set; } = "";

	[JsonPropertyName("TERRAIN")]
	public string Terrain { get; set; } = "";

	[JsonPropertyName("EXITS")]
	public Dictionary<string, string> Exits { get; set; } = [];
}

/// <summary>
/// The serializer contracts for the types this server sends over MSDP, written by the source
/// generator at compile time.
/// </summary>
/// <remarks>
/// Handed to <c>MSDPServerModel.SerializerOptions</c>. MSDP has no types beyond text, so numbers
/// arrive as strings and the contract has to be willing to read them that way.
/// </remarks>
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(Room))]
public sealed partial class MsdpJsonContext : JsonSerializerContext;
