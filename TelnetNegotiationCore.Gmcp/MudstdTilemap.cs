using System.Collections.Generic;
using System.Text.Json;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>One tileset in <c>mudstd.tilemap.tilesets</c>.</summary>
/// <param name="Url">Where to download the image.</param>
/// <param name="SizeX">A tile's width in pixels: 8, 16 or 32.</param>
/// <param name="SizeY">A tile's height in pixels.</param>
public sealed record Tileset(string Url, long SizeX, long SizeY)
{
	/// <summary>The animated tiles: tile number to frame count. Tiles not listed have one frame.</summary>
	public IReadOnlyDictionary<string, long>? Anim { get; init; }
}

/// <summary>
/// <c>mudstd.tilemap.tilesets</c>: the tilesets later messages draw from, by name. A proposal,
/// after <c>beip.tilemap</c>.
/// </summary>
/// <param name="Tilesets">The tilesets, by name.</param>
public sealed record TilemapTilesets(IReadOnlyDictionary<string, Tileset> Tilesets) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdTilemapTilesets;

	/// <inheritdoc />
	public string ToJson() => JsonNodes.Table(Tilesets, tileset => new JsonFieldWriter()
		.Add("url", tileset.Url)
		.Add("sizeX", tileset.SizeX)
		.Add("sizeY", tileset.SizeY)
		.Add("anim", tileset.Anim)
		.ToNode()).ToJsonString();

	/// <summary>Reads a <c>mudstd.tilemap.tilesets</c> body.</summary>
	public static bool TryParse(string? data, out TilemapTilesets message) =>
		JsonFieldReader.TryRead(data, fields => new TilemapTilesets(fields.Entries(tileset => new Tileset(tileset.String("url") ?? "", tileset.Number("sizeX") ?? 0, tileset.Number("sizeY") ?? 0)
		{
			Anim = tileset.NumberTable("anim")
		})), out message);
}

/// <summary>
/// <c>mudstd.tilemap.info</c>: the map's size and which tileset each tile number comes from. Sent
/// now and then, usually on an area change.
/// </summary>
/// <param name="TileWidth">A tile's width in pixels.</param>
/// <param name="TileHeight">A tile's height in pixels.</param>
/// <param name="MapWidth">The map's width in tiles.</param>
/// <param name="MapHeight">The map's height in tiles.</param>
/// <param name="Range">The first tile number of each tileset's range, to the tileset's name: <c>"1":"terrain","257":"immobiles"</c>.</param>
public sealed record TilemapInfo(long TileWidth, long TileHeight, long MapWidth, long MapHeight, IReadOnlyDictionary<string, string> Range) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdTilemapInfo;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("tileWidth", TileWidth)
		.Add("tileHeight", TileHeight)
		.Add("mapWidth", MapWidth)
		.Add("mapHeight", MapHeight)
		.Add("range", JsonNodes.Table(Range, name => System.Text.Json.Nodes.JsonValue.Create(name)))
		.ToString();

	/// <summary>Reads a <c>mudstd.tilemap.info</c> body.</summary>
	public static bool TryParse(string? data, out TilemapInfo message) =>
		JsonFieldReader.TryRead(data, fields => new TilemapInfo(
			fields.Number("tileWidth") ?? 0,
			fields.Number("tileHeight") ?? 0,
			fields.Number("mapWidth") ?? 0,
			fields.Number("mapHeight") ?? 0,
			fields.StringTable("range") ?? new Dictionary<string, string>()), out message);
}

/// <summary>
/// <c>mudstd.tilemap.update</c>: the map, row by row. Each cell holds no, one or several tile
/// numbers, drawn in order so the last is on top.
/// </summary>
/// <param name="Data">Rows of cells of tile numbers.</param>
public sealed record TilemapUpdate(IReadOnlyList<IReadOnlyList<IReadOnlyList<long>>> Data) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.MudstdTilemapUpdate;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("data", JsonNodes.Array(Data, row => JsonNodes.Array(row, cell => JsonNodes.Numbers(cell))))
		.ToString();

	/// <summary>Reads a <c>mudstd.tilemap.update</c> body. A cell that is a bare number reads as a cell of one.</summary>
	public static bool TryParse(string? data, out TilemapUpdate message)
	{
		var valid = true;
		var read = JsonFieldReader.TryRead(data, fields =>
		{
			if (!fields.TryGetElement("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
			{
				valid = false;
				return new TilemapUpdate([]);
			}

			var map = new List<IReadOnlyList<IReadOnlyList<long>>>();

			foreach (var row in rows.EnumerateArray())
			{
				var cells = new List<IReadOnlyList<long>>();

				if (row.ValueKind == JsonValueKind.Array)
				{
					foreach (var cell in row.EnumerateArray())
					{
						cells.Add(ReadCell(cell));
					}
				}

				map.Add(cells);
			}

			return new TilemapUpdate(map);
		}, out message);

		return read && valid;
	}

	private static List<long> ReadCell(JsonElement cell)
	{
		var tiles = new List<long>();

		if (cell.ValueKind == JsonValueKind.Number && cell.TryGetInt64(out var single))
		{
			tiles.Add(single);
		}
		else if (cell.ValueKind == JsonValueKind.Array)
		{
			foreach (var tile in cell.EnumerateArray())
			{
				if (tile.ValueKind == JsonValueKind.Number && tile.TryGetInt64(out var number))
				{
					tiles.Add(number);
				}
			}
		}

		return tiles;
	}
}
