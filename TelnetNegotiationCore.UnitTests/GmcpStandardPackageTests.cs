using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TelnetNegotiationCore.Gmcp;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The typed GMCP messages beyond Core, against the examples in their specifications and the field
/// names Mudlet reads.
/// </summary>
public class GmcpStandardPackageTests : BaseTest
{
	/// <summary>
	/// The specification's full example, field for field.
	/// </summary>
	[Test]
	public async Task MediaPlayWritesEveryFieldItIsGiven()
	{
		var play = new MediaPlay("80_Blacksmith_Shoppe.mp3")
		{
			Url = "https://www.example.com/media/",
			Type = MediaType.Music,
			Tag = "environment",
			Volume = 25,
			FadeIn = 5000,
			FadeOut = 7000,
			Start = 1000,
			Finish = 20000,
			Loops = 3,
			Priority = 60,
			Continue = true,
			Key = "area-background-music",
			Caption = "Blacksmith Hammering"
		};

		await AssertJsonEqual(play.ToJson(), """
			{"name":"80_Blacksmith_Shoppe.mp3","url":"https://www.example.com/media/","type":"music",
			 "tag":"environment","volume":25,"fadein":5000,"fadeout":7000,"start":1000,"finish":20000,
			 "loops":3,"priority":60,"continue":true,"key":"area-background-music","caption":"Blacksmith Hammering"}
			""");
		await Assert.That(play.Package).IsEqualTo("Client.Media.Play");
	}

	/// <summary>
	/// A field that is not set is left out, so the client applies its own default.
	/// </summary>
	[Test]
	public async Task MediaPlayLeavesOutWhatIsNotSet()
	{
		await AssertJsonEqual(new MediaPlay("sword1.mp3").ToJson(), """{"name":"sword1.mp3"}""");
	}

	[Test]
	public async Task MediaPlayReadsBack()
	{
		await Assert.That(MediaPlay.TryParse("""{"name":"city.mp3","type":"MUSIC","volume":"40","loops":-1}""", out var play)).IsTrue();
		await Assert.That(play.Type).IsEqualTo(MediaType.Music);
		await Assert.That(play.Volume).IsEqualTo(40);
		await Assert.That(play.Loops).IsEqualTo(-1);
		await Assert.That(MediaPlay.TryParse("""{"url":"x"}""", out _)).IsFalse();
	}

	/// <summary>
	/// "An empty body will stop all media."
	/// </summary>
	[Test]
	public async Task AnEmptyMediaStopStopsEverything()
	{
		await Assert.That(new MediaStop().ToJson()).IsEqualTo("{}");
		await Assert.That(MediaStop.TryParse("", out var stop)).IsTrue();
		await Assert.That(stop).IsEqualTo(new MediaStop());
	}

	[Test]
	public async Task ClientGuiAndMapUseMudletsKeys()
	{
		await AssertJsonEqual(new ClientGui("39", "http://www.stickmud.com/mudwww/StickMUD.mpackage").ToJson(),
			"""{"version":"39","url":"http://www.stickmud.com/mudwww/StickMUD.mpackage"}""");
		await AssertJsonEqual(new ClientMap("https://example.com/map.xml").ToJson(), """{"url":"https://example.com/map.xml"}""");

		// "May be a string or an integer value."
		await Assert.That(ClientGui.TryParse("""{"version":39,"url":"u"}""", out var gui)).IsTrue();
		await Assert.That(gui.Version).IsEqualTo("39");
	}

	/// <summary>
	/// The specification's two examples of <c>Char.Login.Default</c>.
	/// </summary>
	[Test]
	public async Task LoginDefaultMatchesTheSpecification()
	{
		await AssertJsonEqual(LoginDefault.Password.ToJson(), """{"type": ["password-credentials"]}""");
		await AssertJsonEqual(
			new LoginDefault([LoginDefault.OAuth, LoginDefault.PasswordCredentials], "https://example.com/.well-known/openid-configuration").ToJson(),
			"""{"type": ["oauth", "password-credentials"], "location": "https://example.com/.well-known/openid-configuration" }""");
	}

	/// <summary>
	/// What Mudlet sends (src/GMCPAuthenticator.cpp), extra fields included, and the empty object a
	/// client sends when it knows neither.
	/// </summary>
	[Test]
	public async Task CredentialsAreReadAsMudletSendsThem()
	{
		await Assert.That(LoginCredentials.TryParse("""{"account":"Olad","password":"hunter2","version":2,"token_storage":true}""", out var credentials)).IsTrue();
		await Assert.That(credentials.Account).IsEqualTo("Olad");
		await Assert.That(credentials.Password).IsEqualTo("hunter2");
		await Assert.That(credentials.IsEmpty).IsFalse();
		await Assert.That(credentials.ToString()).DoesNotContain("hunter2");

		await Assert.That(LoginCredentials.TryParse("{}", out var empty)).IsTrue();
		await Assert.That(empty.IsEmpty).IsTrue();
	}

	/// <summary>
	/// <c>success</c> "can be a string value for compatibility with aged MUD drivers".
	/// </summary>
	[Test]
	[Arguments("""{"success":true}""", true)]
	[Arguments("""{"success":"true"}""", true)]
	[Arguments("""{"success":1}""", true)]
	[Arguments("""{"success":false,"message":"Invalid credentials"}""", false)]
	[Arguments("""{"success":"0"}""", false)]
	public async Task LoginResultReadsEveryEncodingOfSuccess(string data, bool success)
	{
		await Assert.That(LoginResult.TryParse(data, out var result)).IsTrue();
		await Assert.That(result.Success).IsEqualTo(success);
	}

	[Test]
	public async Task LoginResultWritesTheMessageOnlyWhenThereIsOne()
	{
		await AssertJsonEqual(new LoginResult(true).ToJson(), """{"success":true}""");
		await AssertJsonEqual(new LoginResult(false, "Invalid credentials").ToJson(), """{"success":false,"message":"Invalid credentials"}""");
	}

	/// <summary>
	/// Mudlet sends <c>External.Discord.Hello</c> with no body, and reads <c>Info</c> and
	/// <c>Status</c> by these lowercase keys (src/Host.cpp).
	/// </summary>
	[Test]
	public async Task DiscordUsesMudletsKeys()
	{
		await Assert.That(new DiscordHello().ToJson()).IsEqualTo("");
		await Assert.That(DiscordHello.TryParse("", out var hello)).IsTrue();
		await Assert.That(hello.User).IsNull();
		await Assert.That(DiscordHello.TryParse("""{"user":"person#1234","private":true}""", out var named)).IsTrue();
		await Assert.That(named.Private).IsTrue();

		await AssertJsonEqual(new DiscordInfo("https://discord.gg/abc", "123").ToJson(),
			"""{"inviteurl":"https://discord.gg/abc","applicationid":"123"}""");

		var status = new DiscordStatus
		{
			Game = "Achaea",
			Details = "Details String",
			State = "State String",
			SmallImage = ["iconname", "iconname2"],
			SmallImageText = "Icon hover text",
			StartTime = 1700000000,
			PartySize = 0,
			PartyMax = 10
		};

		await AssertJsonEqual(status.ToJson(), """
			{"game":"Achaea","details":"Details String","state":"State String","smallimage":["iconname","iconname2"],
			 "smallimagetext":"Icon hover text","starttime":1700000000,"partysize":0,"partymax":10}
			""");
		await Assert.That(DiscordStatus.TryParse(status.ToJson(), out var read)).IsTrue();
		await Assert.That(read.SmallImage).IsEquivalentTo(new[] { "iconname", "iconname2" });
		await Assert.That(read.StartTime).IsEqualTo(1700000000L);
	}

	/// <summary>
	/// The Iron Realms example from the Room package page, which is what Mudlet's mapper script
	/// reads (num, area, environment, exits).
	/// </summary>
	[Test]
	public async Task RoomInfoMatchesTheIronRealmsExample()
	{
		var room = new RoomInfo(12345, "On a hill")
		{
			Area = "Barren hills",
			Environment = "Hills",
			Coords = "45,5,4,3",
			Map = "www.imperian.com/itex/maps/clientmap.php?map=45&level=3 5 4",
			Exits = new Dictionary<string, long> { ["n"] = 12344, ["se"] = 12336 },
			Details = ["shop", "bank"]
		};

		await AssertJsonEqual(room.ToJson(), """
			{"num":12345,"name":"On a hill","area":"Barren hills","environment":"Hills","coords":"45,5,4,3",
			 "map":"www.imperian.com/itex/maps/clientmap.php?map=45&level=3 5 4","exits":{"n":12344,"se":12336},
			 "details":["shop","bank"]}
			""");
	}

	/// <summary>
	/// Aardwolf's <c>room.info</c> from the same page reads too: zone for area, terrain for
	/// environment.
	/// </summary>
	[Test]
	public async Task RoomInfoReadsAardwolfsShape()
	{
		await Assert.That(RoomInfo.TryParse("""
			{"num":5922,"name":"At the entrance of the park","zone":"zoo","terrain":"city","details":"",
			 "exits":{"e":5920,"s":5916,"w":12611},"coord":{"id":0,"x":37,"y":19,"cont":0}}
			""", out var room)).IsTrue();

		await Assert.That(room.Num).IsEqualTo(5922L);
		await Assert.That(room.Area).IsEqualTo("zoo");
		await Assert.That(room.Environment).IsEqualTo("city");
		await Assert.That(room.Exits!["w"]).IsEqualTo(12611L);
		await Assert.That(RoomInfo.TryParse("""{"name":"no number"}""", out _)).IsFalse();
	}

	/// <summary>
	/// The keys Mudlet's base UI builds its gauges from (src/packages/mudlet-base-ui), and Iron
	/// Realms numbers sent as strings.
	/// </summary>
	[Test]
	public async Task CharVitalsUsesTheKeysMudletsBaseUIReads()
	{
		var vitals = new CharVitals
		{
			Hp = 350,
			MaxHp = 400,
			Mp = 10,
			MaxMp = 20,
			Nl = 42,
			Additional = new Dictionary<string, long> { ["ep"] = 600, ["hp"] = 1 }
		};

		await AssertJsonEqual(vitals.ToJson(), """{"hp":350,"maxhp":400,"mp":10,"maxmp":20,"nl":42,"ep":600}""");

		await Assert.That(CharVitals.TryParse("""{"hp":"350","maxhp":"350","nl":"5"}""", out var read)).IsTrue();
		await Assert.That(read.Hp).IsEqualTo(350L);
		await Assert.That(read.Nl).IsEqualTo(5L);
	}

	[Test]
	public async Task CommChannelTextUsesTheIronRealmsKeys()
	{
		await AssertJsonEqual(new CommChannelText("ct", "Olad", "(Clan): Olad says, \"Hi.\"").ToJson(),
			"""{"channel":"ct","talker":"Olad","text":"(Clan): Olad says, \"Hi.\""}""");
	}

	/// <summary>
	/// A typed message goes out under its own package, and the support check sees the package.
	/// </summary>
	[Test]
	public async Task SessionsSendTypedMessages()
	{
		var sent = new List<(string Package, string Data)>();
		var server = new GmcpServerSession((package, data) => { sent.Add((package, data)); return default; });
		var client = new GmcpClientSession((package, data) => { sent.Add((package, data)); return default; });

		await server.HandleAsync("Core.Supports.Set", """["Client.Media 1"]""");
		await server.SendAsync(new ClientMap("https://example.com/map.xml"));
		await Assert.That(await server.SendIfSupportedAsync(new MediaDefault("https://example.com/media/"))).IsTrue();
		await Assert.That(await server.SendIfSupportedAsync(new RoomInfo(1, "x"))).IsFalse();
		await client.SendAsync(new DiscordHello());

		await Assert.That(sent).IsEquivalentTo(new[]
		{
			("Client.Map", """{"url":"https://example.com/map.xml"}"""),
			("Client.Media.Default", """{"url":"https://example.com/media/"}"""),
			("External.Discord.Hello", "")
		});
	}

	private static async Task AssertJsonEqual(string actual, string expected)
	{
		var same = JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected));
		await Assert.That(same).IsTrue().Because($"{actual} should equal {expected}");
	}
}
