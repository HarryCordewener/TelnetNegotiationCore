using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// One item in a <c>Char.Items</c> message, in the Iron Realms shape.
/// </summary>
/// <param name="Id">Identifies the item. Iron Realms sends it as a string of digits.</param>
/// <param name="Name">The short description a player sees, such as "a gold nugget".</param>
public sealed record ItemInfo(string Id, string Name)
{
	/// <summary>The image the client shows for the item, such as <c>commodity</c>.</summary>
	public string? Icon { get; init; }

	/// <summary>
	/// Letters for the item's properties: <c>w</c> worn, <c>W</c> wearable but not worn, <c>l</c>
	/// wielded, <c>g</c> groupable, <c>c</c> container, <c>t</c> takeable, <c>m</c> monster,
	/// <c>d</c> dead monster, <c>x</c> should not be targeted.
	/// </summary>
	public string? Attrib { get; init; }

	internal JsonObject ToNode() => new JsonFieldWriter().Add("id", Id).Add("name", Name).Add("icon", Icon).Add("attrib", Attrib).ToNode();

	internal static ItemInfo Read(JsonFieldReader fields) => new(fields.String("id") ?? "", fields.String("name") ?? "")
	{
		Icon = fields.String("icon"),
		Attrib = fields.String("attrib")
	};
}

/// <summary>Where a <c>Char.Items</c> message's items are.</summary>
public static class ItemLocation
{
	/// <summary>The character's inventory.</summary>
	public const string Inventory = "inv";

	/// <summary>The room the character is in.</summary>
	public const string Room = "room";

	/// <summary>Inside the container with this id: <c>rep</c> and the id.</summary>
	public static string Container(string id) => "rep" + id;
}

/// <summary>
/// <c>Char.Items.List</c>: the items in a location, sent in answer to <c>Char.Items.Inv</c>,
/// <c>Char.Items.Room</c> or <c>Char.Items.Contents</c>.
/// </summary>
/// <param name="Location"><c>inv</c>, <c>room</c>, or <c>rep</c> and a container's id. See <see cref="ItemLocation"/>.</param>
/// <param name="Items">The items.</param>
public sealed record ItemsList(string Location, IReadOnlyList<ItemInfo> Items) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharItemsList;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("location", Location)
		.Add("items", JsonNodes.Array(Items, item => item.ToNode()))
		.ToString();

	/// <summary>Reads a <c>Char.Items.List</c> body.</summary>
	public static bool TryParse(string? data, out ItemsList message) =>
		JsonFieldReader.TryRead(data, fields => new ItemsList(fields.String("location") ?? "", fields.Objects("items", ItemInfo.Read) ?? []), out message)
		&& message.Location.Length > 0;
}

/// <summary>
/// <c>Char.Items.Add</c>, <c>Char.Items.Remove</c> or <c>Char.Items.Update</c>: one item arrived in,
/// left, or changed in a location. <c>Update</c> is sent only for the inventory.
/// </summary>
/// <param name="Package">The package, one of <see cref="GmcpPackages.CharItemsAdd"/>, <see cref="GmcpPackages.CharItemsRemove"/> and <see cref="GmcpPackages.CharItemsUpdate"/>. See <see cref="Add"/>, <see cref="Remove"/> and <see cref="Update"/>.</param>
/// <param name="Location">The location, as in <see cref="ItemsList"/>.</param>
/// <param name="Item">The item.</param>
public sealed record ItemChange(string Package, string Location, ItemInfo Item) : IGmcpMessage
{
	/// <summary><c>Char.Items.Add</c>.</summary>
	public static ItemChange Add(string location, ItemInfo item) => new(GmcpPackages.CharItemsAdd, location, item);

	/// <summary><c>Char.Items.Remove</c>.</summary>
	public static ItemChange Remove(string location, ItemInfo item) => new(GmcpPackages.CharItemsRemove, location, item);

	/// <summary><c>Char.Items.Update</c>.</summary>
	public static ItemChange Update(string location, ItemInfo item) => new(GmcpPackages.CharItemsUpdate, location, item);

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("location", Location).Add("item", Item.ToNode()).ToString();

	/// <summary>
	/// Reads the body of the <paramref name="package"/> message. The page describes <c>item</c> in
	/// <c>Remove</c> as the item's number but shows an object; both are read.
	/// </summary>
	public static bool TryParse(string package, string? data, out ItemChange message) =>
		JsonFieldReader.TryRead(data, fields => new ItemChange(package, fields.String("location") ?? "",
			fields.Object("item", ItemInfo.Read) ?? new ItemInfo(fields.String("item") ?? "", "")), out message)
		&& message.Location.Length > 0 && message.Item.Id.Length > 0;
}

/// <summary>
/// <c>Char.Items.Contents</c>: asks for the items inside a container. The body is the container's
/// id, a bare number when it is one.
/// </summary>
/// <param name="Id">The container's id.</param>
public sealed record ItemsContents(string Id) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharItemsContents;

	/// <inheritdoc />
	public string ToJson() => long.TryParse(Id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
		? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
		: JsonValue.Create(Id).ToJsonString();

	/// <summary>Reads a <c>Char.Items.Contents</c> body: a number or a string.</summary>
	public static bool TryParse(string? data, out ItemsContents message)
	{
		var id = data is null ? null : GmcpJson.ReadString(data) ?? data.Trim();
		message = new ItemsContents(id ?? "");
		return message.Id.Length > 0;
	}
}
