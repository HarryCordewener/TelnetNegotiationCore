using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TelnetNegotiationCore.Gmcp;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The Iron Realms <c>Char.*</c> packages and the <c>mudstd.*</c> proposals, against the examples
/// on their MUD Standards pages.
/// </summary>
public class GmcpCharAndMudstdTests : BaseTest
{
	[Test]
	public async Task ItemsListReadsAndWritesThePagesExample()
	{
		const string example = """
			{ "location": "inv", "items": [
			  { "id": "12807", "name": "a personal journal", "icon": "scroll", "attrib": "l" },
			  { "id": "303060", "name": "a gold nugget", "icon": "commodity" } ] }
			""";

		await Assert.That(ItemsList.TryParse(example, out var list)).IsTrue();
		await Assert.That(list.Location).IsEqualTo(ItemLocation.Inventory);
		await Assert.That(list.Items.Count).IsEqualTo(2);
		await Assert.That(list.Items[0]).IsEqualTo(new ItemInfo("12807", "a personal journal") { Icon = "scroll", Attrib = "l" });
		await AssertJsonEqual(list.ToJson(), example);
	}

	/// <summary>
	/// The page says <c>Remove</c> carries the item's number but shows an object; both read.
	/// </summary>
	[Test]
	public async Task ItemChangesReadAnObjectOrAnId()
	{
		var add = ItemChange.Add(ItemLocation.Room, new ItemInfo("239602", "an elegant white letter") { Icon = "container", Attrib = "c" });
		await Assert.That(add.Package).IsEqualTo("Char.Items.Add");
		await AssertJsonEqual(add.ToJson(), """{"location":"room","item":{"id":"239602","name":"an elegant white letter","icon":"container","attrib":"c"}}""");

		await Assert.That(ItemChange.TryParse(GmcpPackages.CharItemsRemove, """{"location":"room","item":239602}""", out var removed)).IsTrue();
		await Assert.That(removed.Item.Id).IsEqualTo("239602");
		await Assert.That(ItemChange.TryParse(GmcpPackages.CharItemsRemove, add.ToJson(), out var removedObject)).IsTrue();
		await Assert.That(removedObject.Item.Name).IsEqualTo("an elegant white letter");
		await Assert.That(ItemChange.TryParse(GmcpPackages.CharItemsAdd, """{"location":"room"}""", out _)).IsFalse();

		await Assert.That(new ItemsContents("12345").ToJson()).IsEqualTo("12345");
		await Assert.That(new ItemsContents("rep12").ToJson()).IsEqualTo("\"rep12\"");
		await Assert.That(ItemsContents.TryParse("12345", out var contents)).IsTrue();
		await Assert.That(contents.Id).IsEqualTo("12345");
		await Assert.That(ItemLocation.Container("12")).IsEqualTo("rep12");
	}

	[Test]
	public async Task SkillsMatchThePagesExamples()
	{
		await AssertJsonEqual(new SkillsGet("Elemancy", "Firelash").ToJson(), """{"group":"Elemancy","name":"Firelash"}""");
		await Assert.That(SkillsGet.TryParse("", out var all)).IsTrue();
		await Assert.That(all).IsEqualTo(new SkillsGet());

		await Assert.That(SkillGroups.TryParse("""[{"name":"Perception","rank":"Transcendent (100%)"},{"name":"Evileye","rank":"Adept (40%)"}]""", out var groups)).IsTrue();
		await Assert.That(groups.Groups[1]).IsEqualTo(new SkillGroup("Evileye", "Adept (40%)"));
		await Assert.That(groups.Package).IsEqualTo("Char.Skills.Groups");

		var list = new SkillsList("Elemancy", ["Light", "Stoneskin"], ["Cast light", "Make your skin hard as stone"]);
		await AssertJsonEqual(list.ToJson(), """{"group":"Elemancy","list":["Light","Stoneskin"],"desc":["Cast light","Make your skin hard as stone"]}""");
		await Assert.That(SkillsList.TryParse(list.ToJson(), out var readList)).IsTrue();
		await Assert.That(readList.Descriptions![1]).IsEqualTo("Make your skin hard as stone");

		await Assert.That(SkillInfo.TryParse("""{"group":"Elemancy","skill":"Firelash","info":"blah blah"}""", out var info)).IsTrue();
		await Assert.That(info).IsEqualTo(new SkillInfo("Elemancy", "Firelash", "blah blah"));
	}

	[Test]
	public async Task AfflictionsMatchThePagesExamples()
	{
		await Assert.That(AfflictionsList.TryParse("""
			[ { "name": "weariness", "cure": "eat kelp", "desc": "Decreases cutting and blunt damage that you inflict by 30%." },
			  { "name": "asthma", "cure": "eat kelp", "desc": "Makes you unable to smoke pipes." } ]
			""", out var list)).IsTrue();
		await Assert.That(list.Afflictions[1]).IsEqualTo(new Affliction("asthma", "eat kelp", "Makes you unable to smoke pipes."));

		var add = new Affliction("asthma", "eat kelp", "Makes you unable to smoke pipes.");
		await Assert.That(add.Package).IsEqualTo("Char.Afflictions.Add");
		await AssertJsonEqual(add.ToJson(), """{"name":"asthma","cure":"eat kelp","desc":"Makes you unable to smoke pipes."}""");

		await Assert.That(new AfflictionsRemove(["asthma"]).ToJson()).IsEqualTo("""["asthma"]""");
		await Assert.That(AfflictionsRemove.TryParse("""[ "asthma" ]""", out var removed)).IsTrue();
		await Assert.That(removed.Names[0]).IsEqualTo("asthma");
	}

	/// <summary>
	/// <c>important</c> arrives as <c>"0"</c> and goes back out the same way.
	/// </summary>
	[Test]
	public async Task DefencesMatchThePagesExamples()
	{
		await Assert.That(DefencesList.TryParse("""
			[ { "name": "Jogging", "desc": "Jogging around", "category": "", "important": "0", "icon": "person-running", "color": "white" } ]
			""", out var info, info: true)).IsTrue();
		var jogging = info.Defences[0];
		await Assert.That(jogging.Important).IsFalse();
		await Assert.That(jogging.Icon).IsEqualTo("person-running");
		await Assert.That(info.Package).IsEqualTo("Char.Defences.InfoList");
		await AssertJsonEqual(info.ToJson(), """[{"name":"Jogging","desc":"Jogging around","category":"","important":"0","icon":"person-running","color":"white"}]""");

		await AssertJsonEqual(new DefencesList([new Defence("deaf", "deaf")]).ToJson(), """[{"name":"deaf","desc":"deaf"}]""");
		await Assert.That(new DefencesList([]).Package).IsEqualTo("Char.Defences.List");
		await Assert.That(Defence.TryParse("""{"name":"deaf","desc":"deaf"}""", out var add)).IsTrue();
		await Assert.That(add.Package).IsEqualTo("Char.Defences.Add");
		await Assert.That(DefencesRemove.TryParse("""[ "blind" ]""", out var removed)).IsTrue();
		await Assert.That(removed.Names[0]).IsEqualTo("blind");
	}

	/// <summary>
	/// The page writes lists in braces, which is not JSON; that reads, and a JSON array goes out.
	/// </summary>
	[Test]
	public async Task StatsReadThePagesBraceListAndWriteAnArray()
	{
		await Assert.That(StatDefinitions.TryParse("""
			{ { "id": "hp", "label": "Hit Points", "abbrev": "HP", "color": "FF0000" },
			  { "id": "mana", "label": "Mana", "abbrev": "Ma", "color": "0000FF" } }
			""", out var definitions)).IsTrue();
		await Assert.That(definitions.Definitions.Count).IsEqualTo(2);
		await Assert.That(definitions.Definitions[0]).IsEqualTo(new StatDefinition("hp", "Hit Points") { Abbrev = "HP", Color = "FF0000" });
		await AssertJsonEqual(definitions.ToJson(), """
			[{"id":"hp","label":"Hit Points","abbrev":"HP","color":"FF0000"},{"id":"mana","label":"Mana","abbrev":"Ma","color":"0000FF"}]
			""");

		await Assert.That(StatUpdate.TryParse("""{ { "id": "hp", "current": "15", "max": "20", "tempmax": "24" }, { "id": "mana", "current": 16, "max": "22", "blocked": "4" } }""", out var update, StatKind.CharResources)).IsTrue();
		await Assert.That(update.Values[1]).IsEqualTo(new StatValue("mana", "16") { Max = "22", Blocked = "4" });
		await Assert.That(update.Package).IsEqualTo("mudstd.char.resources.update");
		await Assert.That(new StatUpdate([], StatKind.CharAttributes).Package).IsEqualTo("mudstd.char.attributes.update");
		await Assert.That(new StatDefinitions([]).Package).IsEqualTo("mudstd.resources.definitions");
		await Assert.That(StatUpdate.TryParse("""{"id":"hp"}""", out _)).IsFalse();
	}

	[Test]
	public async Task ChannelsReadThePagesExamples()
	{
		await Assert.That(ChannelDefinitions.TryParse("""
			{ { "id": "gtell", "label": "Group Tell", "color": "FF0000" },
			  { "id": "newbie", "label": "Newbie", "color": "00FFFF", "colorANSI": 3 } }
			""", out var definitions)).IsTrue();
		await Assert.That(definitions.Channels[1]).IsEqualTo(new ChannelDefinition("newbie", "Newbie") { Color = "00FFFF", ColorAnsi = 3 });
		await AssertJsonEqual(definitions.ToJson(), """
			[{"id":"gtell","label":"Group Tell","color":"FF0000"},{"id":"newbie","label":"Newbie","color":"00FFFF","colorANSI":3}]
			""");

		var said = new ChannelEvent("gsay", "Taranion says to the group: Hello all!") { Player = "Taranion" };
		await Assert.That(ChannelEvent.TryParse("""{ { "chan": "gsay", "player": "Taranion", "msg": "Taranion says to the group: Hello all!" } }""", out var wrapped)).IsTrue();
		await Assert.That(wrapped).IsEqualTo(said);
		await Assert.That(ChannelEvent.TryParse(said.ToJson(), out var plain)).IsTrue();
		await Assert.That(plain).IsEqualTo(said);
	}

	[Test]
	public async Task RoomReadsThePagesExamples()
	{
		await Assert.That(RoomTerrain.TryParse("""
			{ { "id": "city", "label": "City", "color": "C0C0C0", "tile_url": "data:image/jpeg;base64,/9j/4AAQ" } }
			""", out var terrain)).IsTrue();
		await Assert.That(terrain.Terrains[0].TileUrl).IsEqualTo("data:image/jpeg;base64,/9j/4AAQ");

		const string info = """
			{ "id": "1/1/12", "name": "On a hill", "description": "The view from this hill is spectacular.",
			  "terrain": "forest", "exits": { "E": { "id": "1/1/13", "inverse": "N" } } }
			""";
		await Assert.That(MudstdRoomInfo.TryParse(info, out var room)).IsTrue();
		await Assert.That(room.Exits["E"]).IsEqualTo(new RoomExit("1/1/13", "N"));
		await AssertJsonEqual(room.ToJson(), info);

		var entities = new RoomEntities([
			new RoomEntity("a goblin", EntityType.Mobile)
			{
				Actions = [new EntityAction("Kill", "kill goblin") { Emoji = "⚔", Danger = true }, new EntityAction("Look", "look goblin")]
			}
		]);
		await AssertJsonEqual(entities.ToJson(), """
			[{"name":"a goblin","type":"mobile","actions":[{"name":"Kill","command":"kill goblin","emoji":"⚔","color":"danger"},{"name":"Look","command":"look goblin"}]}]
			""");
		await Assert.That(RoomEntities.TryParse("""{"name":"a coin","type":"item"}""", out var single)).IsTrue();
		await Assert.That(single.Entities[0].Type).IsEqualTo(EntityType.Item);
		await Assert.That(RoomEntities.TryParse(entities.ToJson(), out var readBack)).IsTrue();
		await Assert.That(readBack.Entities[0].Actions![0].Danger).IsTrue();
	}

	[Test]
	public async Task FramesRoundTrip()
	{
		await AssertJsonEqual(new FrameSupport([FrameTypes.Docked, FrameTypes.Tab], [FrameContents.Terminal]).ToJson(),
			"""{"type":["docked","tab"],"content":["terminal"]}""");

		var open = new FrameOpen("stats", FrameTypes.Docked)
		{
			Content = FrameContents.Terminal,
			Align = "right",
			Label = "Stats",
			SizeValue = 30,
			SizeUnit = "c",
			Details = new FrameDetails { Closeable = false, Scrolling = "Y" }
		};
		await AssertJsonEqual(open.ToJson(), """
			{"id":"stats","type":"docked","content":"terminal","align":"right","label":"Stats","sizeValue":30,"sizeUnit":"c",
			 "details":{"scrolling":"Y","closeable":false}}
			""");
		await Assert.That(FrameOpen.TryParse("""{"id":"stats","type":"docked","sizeValue":"30","details":{"closeable":false}}""", out var read)).IsTrue();
		await Assert.That(read.SizeValue).IsEqualTo(30L);
		await Assert.That(read.Details!.Closeable).IsFalse();

		await AssertJsonEqual(new FrameTerminal("stats", "\u001b[0;1;37mSTR:\u001b[0m 12", true).ToJson(),
			"""{"id":"stats","clear":true,"ansi":"\u001b[0;1;37mSTR:\u001b[0m 12"}""");
		await AssertJsonEqual(new FrameClose("topleft").ToJson(), """{"id":"topleft"}""");
		await AssertJsonEqual(new FrameImage("topleft", "http://myserver.com/portrait.png").ToJson(), """{"id":"topleft","image":"http://myserver.com/portrait.png"}""");

		var resized = new FrameSized("topleft", new FrameSize(80, 24), Resized: true) { SizePixel = new FrameSize(640, 384) };
		await Assert.That(resized.Package).IsEqualTo("mudstd.frame.resized");
		await AssertJsonEqual(resized.ToJson(), """{"id":"topleft","sizeChar":{"width":80,"height":24},"sizePixel":{"width":640,"height":384}}""");
		await Assert.That(FrameSized.TryParse(resized.ToJson(), out var readSize)).IsTrue();
		await Assert.That(readSize.SizePixel).IsEqualTo(new FrameSize(640, 384));
		await Assert.That(readSize.Package).IsEqualTo("mudstd.frame.opened");

		await Assert.That(FrameClosed.TryParse("""{"id":"topleft","reason":"user"}""", out var closed)).IsTrue();
		await Assert.That(closed).IsEqualTo(new FrameClosed("topleft", "user"));
	}

	[Test]
	public async Task TilemapReadsThePagesExamples()
	{
		await Assert.That(TilemapTilesets.TryParse("""
			{ "mobs": { "url": "http://example.com/Mobs.png", "sizeX": 32, "sizeY": 32, "anim": {} },
			  "terrain": { "url": "http://example.com/Terrain.png", "sizeX": 32, "sizeY": 32, "anim": { "50": 4, "54": "4" } } }
			""", out var tilesets)).IsTrue();
		await Assert.That(tilesets.Tilesets.Count).IsEqualTo(2);
		await Assert.That(tilesets.Tilesets["terrain"].Anim!["54"]).IsEqualTo(4L);

		const string info = """{ "tileWidth": 32, "tileHeight": 32, "mapWidth": 11, "mapHeight": 11, "range": { "1": "terrain", "257": "immobiles" } }""";
		await Assert.That(TilemapInfo.TryParse(info, out var map)).IsTrue();
		await Assert.That(map.Range["257"]).IsEqualTo("immobiles");
		await AssertJsonEqual(map.ToJson(), info);

		await Assert.That(TilemapUpdate.TryParse("""{ "data": [ [ [], [119], [10,539,556] ], [ [10], 7 ] ] }""", out var update)).IsTrue();
		await Assert.That(update.Data[0][2]).IsEquivalentTo(new List<long> { 10, 539, 556 });
		await Assert.That(update.Data[1][1]).IsEquivalentTo(new List<long> { 7 });
		await AssertJsonEqual(update.ToJson(), """{"data":[[[],[119],[10,539,556]],[[10],[7]]]}""");
		await Assert.That(TilemapUpdate.TryParse("{}", out _)).IsFalse();
	}

	private static async Task AssertJsonEqual(string actual, string expected)
	{
		var same = JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected));
		await Assert.That(same).IsTrue().Because($"{actual} should equal {expected}");
	}
}
