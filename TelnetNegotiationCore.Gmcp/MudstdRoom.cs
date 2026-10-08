using System.Collections.Generic;
using System.Linq;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>One terrain in <c>mudstd.room.terrain</c>.</summary>
/// <param name="Id">The name <see cref="MudstdRoomInfo.Terrain"/> refers to. ASCII letters only.</param>
/// <param name="Label">A name to show.</param>
public sealed record TerrainDefinition(string Id, string Label)
{
	/// <summary>An RGB color in hex.</summary>
	public string? Color { get; init; }

	/// <summary>An http(s) or <c>data:</c> URL of a small image, about 32 by 32 pixels. Written as <c>tile_url</c>.</summary>
	public string? TileUrl { get; init; }
}

/// <summary>
/// <c>mudstd.room.terrain</c>: the terrains rooms refer to, and how to draw them. Sent on connecting,
/// or at most once per area change. A proposal.
/// </summary>
/// <remarks>
/// The page writes the list in braces, which is not JSON. This sends a JSON array and reads either.
/// </remarks>
/// <param name="Terrains">The terrains.</param>
public sealed record RoomTerrain(IReadOnlyList<TerrainDefinition> Terrains) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdRoomTerrain;

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Array(Terrains, terrain => new JsonFieldWriter()
		.Add("id", terrain.Id)
		.Add("label", terrain.Label)
		.Add("color", terrain.Color)
		.Add("tile_url", terrain.TileUrl)
		.ToNode()).ToJsonString();

	/// <summary>Reads a <c>mudstd.room.terrain</c> body.</summary>
	public static bool TryParse(string? data, out RoomTerrain message)
	{
		var read = JsonFieldReader.TryReadArray(data, fields => new TerrainDefinition(fields.String("id") ?? "", fields.String("label") ?? "")
		{
			Color = fields.String("color"),
			TileUrl = fields.String("tile_url")
		}, out var terrains);
		message = new RoomTerrain(terrains);
		return read;
	}
}

/// <summary>One exit in <see cref="MudstdRoomInfo.Exits"/>.</summary>
/// <param name="Id">The room it leads to, which need not be a number.</param>
/// <param name="Inverse">The direction that leads back, when it is not simply the opposite.</param>
public sealed record RoomExit(string Id, string? Inverse = null);

/// <summary>
/// <c>mudstd.room.info</c>: the room the character is in, with ids that need not be numbers. A
/// proposal; <see cref="RoomInfo"/> is the <c>Room.Info</c> most clients read today.
/// </summary>
/// <param name="Id">The room, unique across the server.</param>
/// <param name="Name">The room's name.</param>
/// <param name="Exits">The exits, by direction, such as <c>N</c>, <c>NE</c>, <c>IN</c> or <c>clockwise</c>.</param>
public sealed record MudstdRoomInfo(string Id, string Name, IReadOnlyDictionary<string, RoomExit> Exits) : IGmcpMessage
{
	/// <summary>The full room description.</summary>
	public string? Description { get; init; }

	/// <summary>A <see cref="TerrainDefinition.Id"/>.</summary>
	public string? Terrain { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdRoomInfo;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("id", Id)
		.Add("name", Name)
		.Add("description", Description)
		.Add("terrain", Terrain)
		.Add("exits", JsonNodes.Table(Exits, exit => new JsonFieldWriter().Add("id", exit.Id).Add("inverse", exit.Inverse).ToNode()))
		.ToString();

	/// <summary>Reads a <c>mudstd.room.info</c> body.</summary>
	public static bool TryParse(string? data, out MudstdRoomInfo message) =>
		JsonFieldReader.TryRead(data, fields => new MudstdRoomInfo(fields.String("id") ?? "", fields.String("name") ?? "",
			fields.ObjectTable("exits", exit => new RoomExit(exit.String("id") ?? "", exit.String("inverse"))) ?? new Dictionary<string, RoomExit>())
		{
			Description = fields.String("description"),
			Terrain = fields.String("terrain")
		}, out message) && message.Id.Length > 0;
}

/// <summary>What a <see cref="RoomEntity"/> is.</summary>
public enum EntityType
{
	/// <summary>An NPC.</summary>
	Mobile,

	/// <summary>An item.</summary>
	Item,

	/// <summary>A player.</summary>
	Player
}

/// <summary>A command a client can offer for a <see cref="RoomEntity"/>, as a button.</summary>
/// <param name="Name">A short label, ideally one word.</param>
/// <param name="Command">What the client sends, as if typed, when it is chosen.</param>
public sealed record EntityAction(string Name, string Command)
{
	/// <summary>An emoji to put before <see cref="Name"/>.</summary>
	public string? Emoji { get; init; }

	/// <summary>True for an aggressive action, such as kill or steal: written as <c>"color":"danger"</c>.</summary>
	public bool Danger { get; init; }
}

/// <summary>An NPC, item or player in <c>mudstd.room.entities</c>.</summary>
/// <param name="Name">A short name, without color codes.</param>
/// <param name="Type">What it is.</param>
public sealed record RoomEntity(string Name, EntityType Type)
{
	/// <summary><see cref="Name"/> with ANSI color codes.</summary>
	public string? NameAnsi { get; init; }

	/// <summary>An http(s) or <c>data:</c> URL of a small image. Written as <c>icon_url</c>.</summary>
	public string? IconUrl { get; init; }

	/// <summary>A few commands the client can offer.</summary>
	public IReadOnlyList<EntityAction>? Actions { get; init; }
}

/// <summary>
/// <c>mudstd.room.entities</c>: the NPCs, items and players in or near the room. A proposal.
/// </summary>
/// <remarks>
/// The page shows one entity as the body; this sends an array and reads either.
/// </remarks>
/// <param name="Entities">The entities.</param>
public sealed record RoomEntities(IReadOnlyList<RoomEntity> Entities) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdRoomEntities;

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Array(Entities, entity => new JsonFieldWriter()
		.Add("name", entity.Name)
		.Add("nameANSI", entity.NameAnsi)
		.Add("type", entity.Type switch { EntityType.Item => "item", EntityType.Player => "player", _ => "mobile" })
		.Add("icon_url", entity.IconUrl)
		.Add("actions", JsonNodes.ArrayOrNull(entity.Actions, action => new JsonFieldWriter()
			.Add("name", action.Name)
			.Add("command", action.Command)
			.Add("emoji", action.Emoji)
			.Add("color", action.Danger ? "danger" : null)
			.ToNode()))
		.ToNode()).ToJsonString();

	/// <summary>Reads a <c>mudstd.room.entities</c> body: an array, or one entity.</summary>
	public static bool TryParse(string? data, out RoomEntities message)
	{
		var read = JsonFieldReader.TryReadArray(data, ReadEntity, out var entities, single: true);
		message = new RoomEntities(entities.Where(entity => entity is not null).Select(entity => entity!).ToList());
		return read;
	}

	private static RoomEntity? ReadEntity(JsonFieldReader fields)
	{
		EntityType? type = fields.String("type")?.Trim().ToLowerInvariant() switch
		{
			"mobile" => EntityType.Mobile,
			"item" => EntityType.Item,
			"player" => EntityType.Player,
			_ => null
		};

		return type is null
			? null
			: new RoomEntity(fields.String("name") ?? "", type.Value)
			{
				NameAnsi = fields.String("nameANSI"),
				IconUrl = fields.String("icon_url"),
				Actions = fields.Objects("actions", action => new EntityAction(action.String("name") ?? "", action.String("command") ?? "")
				{
					Emoji = action.String("emoji"),
					Danger = string.Equals(action.String("color"), "danger", System.StringComparison.OrdinalIgnoreCase)
				})
			};
	}
}
