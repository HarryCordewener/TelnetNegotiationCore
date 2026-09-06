using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Functional;
using TelnetNegotiationCore.Handlers;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// MSDP against its specification (https://tintin.mudhalla.net/protocols/msdp/), in both
/// directions: every byte sequence the document shows is decoded into what it means, and written
/// back out from that meaning.
/// </summary>
/// <remarks>
/// The specification is short and entirely by example, so these tests are its examples. Each one
/// quotes the line it comes from; where a rule is stated in prose rather than shown, the quote is
/// the rule.
/// </remarks>
public class MSDPSpecificationTests : BaseTest
{
	private static readonly Encoding Encoding = Encoding.ASCII;

	#region The specification's messages, both directions

	/// <summary>
	/// Every example message in the specification: the bytes, and the JSON they mean.
	/// </summary>
	/// <remarks>
	/// <c>SortedKeys</c> says whether the payload's variables are already in the order a scan puts
	/// them in (ordinal by name). MSDP fixes no order for the variables in a table, so a message
	/// whose variables arrive in another order means the same thing and is written back out sorted.
	/// </remarks>
	public static IEnumerable<(string Description, byte[] Payload, string Json, bool SortedKeys)> SpecificationMessages()
	{
		// "client - IAC SB MSDP MSDP_VAR "LIST" MSDP_VAL "COMMANDS" IAC SE"
		yield return ("a client asking for a list",
			Payload(Trigger.MSDP_VAR, "LIST", Trigger.MSDP_VAL, "COMMANDS"),
			"""{"LIST":"COMMANDS"}""", true);

		// "server - IAC SB MSDP MSDP_VAR "COMMANDS" MSDP_VAL MSDP_ARRAY_OPEN MSDP_VAL "LIST"
		//           MSDP_VAL "REPORT" MSDP_VAL "SEND" MSDP_ARRAY_CLOSE IAC SE"
		yield return ("a server answering with a list of commands",
			Payload(Trigger.MSDP_VAR, "COMMANDS",
				Trigger.MSDP_VAL, Trigger.MSDP_ARRAY_OPEN,
				Trigger.MSDP_VAL, "LIST",
				Trigger.MSDP_VAL, "REPORT",
				Trigger.MSDP_VAL, "SEND",
				Trigger.MSDP_ARRAY_CLOSE),
			"""{"COMMANDS":["LIST","REPORT","SEND"]}""", true);

		// "server - IAC SB MSDP MSDP_VAR "REPORTABLE_VARIABLES" MSDP_VAL "HINT" IAC SE"
		yield return ("a list of one, sent as a plain value",
			Payload(Trigger.MSDP_VAR, "REPORTABLE_VARIABLES", Trigger.MSDP_VAL, "HINT"),
			"""{"REPORTABLE_VARIABLES":"HINT"}""", true);

		// "client - IAC SB MSDP MSDP_VAR "SEND" MSDP_VAL "HINT" IAC SE"
		yield return ("a client asking for a variable",
			Payload(Trigger.MSDP_VAR, "SEND", Trigger.MSDP_VAL, "HINT"),
			"""{"SEND":"HINT"}""", true);

		// "server - IAC SB MSDP MSDP_VAR "HINT" MSDP_VAL "THE GAME" IAC SE"
		yield return ("a server answering with the variable's value",
			Payload(Trigger.MSDP_VAR, "HINT", Trigger.MSDP_VAL, "THE GAME"),
			"""{"HINT":"THE GAME"}""", true);

		// "IAC SB MSDP MSDP_VAR "REPORTABLE_VARIABLES" MSDP_VAL MSDP_ARRAY_OPEN MSDP_VAL "HEALTH"
		//  MSDP_VAL "HEALTH_MAX" MSDP_VAL "MANA" MSDP_VAL "MANA_MAX" MSDP_ARRAY_CLOSE IAC SE"
		yield return ("the array example",
			Payload(Trigger.MSDP_VAR, "REPORTABLE_VARIABLES",
				Trigger.MSDP_VAL, Trigger.MSDP_ARRAY_OPEN,
				Trigger.MSDP_VAL, "HEALTH",
				Trigger.MSDP_VAL, "HEALTH_MAX",
				Trigger.MSDP_VAL, "MANA",
				Trigger.MSDP_VAL, "MANA_MAX",
				Trigger.MSDP_ARRAY_CLOSE),
			"""{"REPORTABLE_VARIABLES":["HEALTH","HEALTH_MAX","MANA","MANA_MAX"]}""", true);

		// "server - IAC SB MSDP MSDP_VAR "CONFIGURABLE_VARIABLES" MSDP_VAL MSDP_ARRAY_OPEN
		//           MSDP_VAL "UTF_8" MSDP_VAL "XTERM_256_COLORS" MSDP_ARRAY_CLOSE IAC SE"
		yield return ("the configurable variables a server offers",
			Payload(Trigger.MSDP_VAR, "CONFIGURABLE_VARIABLES",
				Trigger.MSDP_VAL, Trigger.MSDP_ARRAY_OPEN,
				Trigger.MSDP_VAL, "UTF_8",
				Trigger.MSDP_VAL, "XTERM_256_COLORS",
				Trigger.MSDP_ARRAY_CLOSE),
			"""{"CONFIGURABLE_VARIABLES":["UTF_8","XTERM_256_COLORS"]}""", true);

		// "client - IAC SB MSDP MSDP_VAR "UTF_8" MSDP_VAL "0" MSDP_VAR "XTERM_256_COLORS"
		//           MSDP_VAL "1" IAC SE"
		yield return ("a client setting two configurable variables at once",
			Payload(Trigger.MSDP_VAR, "UTF_8", Trigger.MSDP_VAL, "0",
				Trigger.MSDP_VAR, "XTERM_256_COLORS", Trigger.MSDP_VAL, "1"),
			"""{"UTF_8":"0","XTERM_256_COLORS":"1"}""", true);

		// "server - IAC SB MSDP MSDP_VAR "MUD_TIME" MSDP_VAL "14:00" IAC SE"
		yield return ("a reported variable",
			Payload(Trigger.MSDP_VAR, "MUD_TIME", Trigger.MSDP_VAL, "14:00"),
			"""{"MUD_TIME":"14:00"}""", true);

		// "server - IAC SB MSDP MSDP_VAR "AREA_NAME" MSDP_VAL "Tower of Entropy"
		//           MSDP_VAR "ROOM_NAME" MSDP_VAL "Tower Pinnacle" IAC SE"
		yield return ("two variables answered at once",
			Payload(Trigger.MSDP_VAR, "AREA_NAME", Trigger.MSDP_VAL, "Tower of Entropy",
				Trigger.MSDP_VAR, "ROOM_NAME", Trigger.MSDP_VAL, "Tower Pinnacle"),
			"""{"AREA_NAME":"Tower of Entropy","ROOM_NAME":"Tower Pinnacle"}""", true);

		// "IAC SB MSDP MSDP_VAR "ROOM" MSDP_VAL MSDP_TABLE_OPEN MSDP_VAR "VNUM" MSDP_VAL "6008"
		//  MSDP_VAR "NAME" MSDP_VAL "The forest clearing" MSDP_VAR "AREA" MSDP_VAL "Haon Dor"
		//  MSDP_VAR "TERRAIN" MSDP_VAL "forest" MSDP_VAR "EXITS" MSDP_VAL MSDP_TABLE_OPEN
		//  MSDP_VAR "n" MSDP_VAL "6011" MSDP_VAR "e" MSDP_VAL "6007" MSDP_TABLE_CLOSE
		//  MSDP_TABLE_CLOSE IAC SE"
		yield return ("the table example",
			RoomPayload(),
			"""{"ROOM":{"AREA":"Haon Dor","EXITS":{"e":"6007","n":"6011"},"NAME":"The forest clearing","TERRAIN":"forest","VNUM":"6008"}}""",
			false);
	}

	/// <summary>
	/// Reading direction: the bytes a peer sends become the message they stand for.
	/// </summary>
	[Test]
	[MethodDataSource(nameof(SpecificationMessages))]
	public async Task DecodesTheSpecificationsMessages(string description, byte[] payload, string json, bool sortedKeys)
	{

		await Assert.That(MSDPLibrary.ScanToJson(payload, Encoding)).IsEqualTo(json);
	}

	/// <summary>
	/// Writing direction: the message becomes the bytes the specification shows.
	/// </summary>
	[Test]
	[MethodDataSource(nameof(SpecificationMessages))]
	public async Task EncodesTheSpecificationsMessages(string description, byte[] payload, string json, bool sortedKeys)
	{

		var written = MSDPLibrary.ReportVariables(json, Encoding);

		if (sortedKeys)
		{
			await AssertByteArraysEqual(written, payload);
		}
		else
		{
			// The variables of a table have no fixed order in MSDP, so this one comes back carrying
			// the same pairs in another order. Reading it again is what proves they are the same.
			await Assert.That(MSDPLibrary.ScanToJson(written, Encoding)).IsEqualTo(json);
		}
	}

	/// <summary>
	/// And back again: what was read writes out to what was read, for every example.
	/// </summary>
	[Test]
	[MethodDataSource(nameof(SpecificationMessages))]
	public async Task RoundTripsTheSpecificationsMessages(string description, byte[] payload, string json, bool sortedKeys)
	{

		var decoded = MSDPLibrary.ScanToJson(payload, Encoding);
		var reencoded = MSDPLibrary.ReportVariables(decoded, Encoding);

		await Assert.That(MSDPLibrary.ScanToJson(reencoded, Encoding)).IsEqualTo(decoded);
	}

	/// <summary>
	/// "The value of the SEND command should be a list of variables the client wants returned." The
	/// specification writes that list two ways — repeated values, and an array — and they mean the
	/// same thing.
	/// </summary>
	[Test]
	public async Task BothSpellingsOfAListOfValuesMeanTheSame()
	{
		var repeated = Payload(Trigger.MSDP_VAR, "SEND",
			Trigger.MSDP_VAL, "AREA_NAME",
			Trigger.MSDP_VAL, "ROOM_NAME");

		var array = Payload(Trigger.MSDP_VAR, "SEND",
			Trigger.MSDP_VAL, Trigger.MSDP_ARRAY_OPEN,
			Trigger.MSDP_VAL, "AREA_NAME",
			Trigger.MSDP_VAL, "ROOM_NAME",
			Trigger.MSDP_ARRAY_CLOSE);

		await Assert.That(MSDPLibrary.ScanToJson(repeated, Encoding))
			.IsEqualTo("""{"SEND":["AREA_NAME","ROOM_NAME"]}""");
		await Assert.That(MSDPLibrary.ScanToJson(array, Encoding))
			.IsEqualTo(MSDPLibrary.ScanToJson(repeated, Encoding));

		// Written back out, a list is the array form: it is the one that cannot be misread.
		await AssertByteArraysEqual(
			MSDPLibrary.ReportVariables("""{"SEND":["AREA_NAME","ROOM_NAME"]}""", Encoding), array);
	}

	/// <summary>
	/// "Tables ... are nested ... functioning as objects in the JSON standard", so a table inside an
	/// array and an array inside a table both stand.
	/// </summary>
	[Test]
	public async Task TablesAndArraysNestInEachOther()
	{
		const string json = """{"GROUP":[{"NAME":"Ardan","HEALTH":"50"},{"NAME":"Kordan","HEALTH":"70"}]}""";

		var written = MSDPLibrary.ReportVariables(json, Encoding);

		await AssertByteArraysEqual(written, Payload(
			Trigger.MSDP_VAR, "GROUP",
			Trigger.MSDP_VAL, Trigger.MSDP_ARRAY_OPEN,
			Trigger.MSDP_VAL, Trigger.MSDP_TABLE_OPEN,
			Trigger.MSDP_VAR, "NAME", Trigger.MSDP_VAL, "Ardan",
			Trigger.MSDP_VAR, "HEALTH", Trigger.MSDP_VAL, "50",
			Trigger.MSDP_TABLE_CLOSE,
			Trigger.MSDP_VAL, Trigger.MSDP_TABLE_OPEN,
			Trigger.MSDP_VAR, "NAME", Trigger.MSDP_VAL, "Kordan",
			Trigger.MSDP_VAR, "HEALTH", Trigger.MSDP_VAL, "70",
			Trigger.MSDP_TABLE_CLOSE,
			Trigger.MSDP_ARRAY_CLOSE));

		await Assert.That(MSDPLibrary.ScanToJson(written, Encoding))
			.IsEqualTo("""{"GROUP":[{"HEALTH":"50","NAME":"Ardan"},{"HEALTH":"70","NAME":"Kordan"}]}""");
	}

	#endregion

	#region What a variable or a value may contain

	/// <summary>
	/// "Variables and values cannot contain the NUL, MSDP_VAL, MSDP_VAR, MSDP_TABLE_OPEN,
	/// MSDP_TABLE_CLOSE, MSDP_ARRAY_OPEN, MSDP_ARRAY_CLOSE or IAC byte." Writing one into a value
	/// would not carry that byte to the peer - it would forge a marker and change the shape of the
	/// message, so it is refused before it reaches the wire.
	/// </summary>
	[Test]
	[Arguments((byte)0)]
	[Arguments((byte)Trigger.MSDP_VAR)]
	[Arguments((byte)Trigger.MSDP_VAL)]
	[Arguments((byte)Trigger.MSDP_TABLE_OPEN)]
	[Arguments((byte)Trigger.MSDP_TABLE_CLOSE)]
	[Arguments((byte)Trigger.MSDP_ARRAY_OPEN)]
	[Arguments((byte)Trigger.MSDP_ARRAY_CLOSE)]
	public async Task AValueCarryingAMarkerIsRefused(byte marker)
	{
		var message = new JsonObject { ["HINT"] = $"THE{(char)marker}GAME" };

		await Assert.That(() => MSDPLibrary.ReportVariables(message, Encoding)).Throws<InvalidDataException>();
	}

	/// <summary>
	/// The same rule for the name of a variable.
	/// </summary>
	[Test]
	[Arguments((byte)0)]
	[Arguments((byte)Trigger.MSDP_VAR)]
	[Arguments((byte)Trigger.MSDP_VAL)]
	[Arguments((byte)Trigger.MSDP_TABLE_OPEN)]
	[Arguments((byte)Trigger.MSDP_TABLE_CLOSE)]
	[Arguments((byte)Trigger.MSDP_ARRAY_OPEN)]
	[Arguments((byte)Trigger.MSDP_ARRAY_CLOSE)]
	public async Task AVariableNameCarryingAMarkerIsRefused(byte marker)
	{
		var message = new JsonObject { [$"HI{(char)marker}NT"] = "THE GAME" };

		await Assert.That(() => MSDPLibrary.ReportVariables(message, Encoding)).Throws<InvalidDataException>();
	}

	/// <summary>
	/// <c>IAC</c> is the exception among those bytes: a character set other than ASCII can encode a
	/// perfectly ordinary character to 0xFF, and RFC 854 says such a byte is doubled in data. So it
	/// goes out doubled and comes back single, and a value carrying one survives the round trip.
	/// </summary>
	[Test]
	public async Task ALiteralIACInAValueIsDoubledOnTheWayOutAndUndoubledOnTheWayIn()
	{
		byte[] sent = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				sent = data.ToArray();
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MSDPProtocol>()
			.BuildAsync();

		// 'A' 0xFF 'B' as the value of TEST.
		await client.SendMSDPCommand(Encoding.GetBytes("TEST"), [0x41, 0xFF, 0x42]);

		await Assert.That(sent).IsNotNull();
		await AssertByteArraysEqual(sent, [
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSDP,
			(byte)Trigger.MSDP_VAR, .. Encoding.GetBytes("TEST"),
			(byte)Trigger.MSDP_VAL, 0x41, 0xFF, 0xFF, 0x42,
			(byte)Trigger.IAC, (byte)Trigger.SE]);

		// The other direction: those same bytes, read by a server, still carry the byte between the
		// A and the B. (What character it decodes to is the connection's encoding's business; that
		// it is still there is MSDP's.)
		var received = await ReceiveAsync(sent);
		var value = JsonNode.Parse(received)!["TEST"]!.GetValue<string>();

		await Assert.That(value.Length).IsEqualTo(3);
		await Assert.That(value[0]).IsEqualTo('A');
		await Assert.That(value[2]).IsEqualTo('B');

		await client.DisposeAsync();
	}

	#endregion

	#region The handshake, end to end

	/// <summary>
	/// The specification's handshake, run between a real client and a real server with the bytes of
	/// each going into the other:
	/// <code>
	/// server - IAC WILL MSDP
	/// client - IAC   DO MSDP
	/// client - IAC   SB MSDP MSDP_VAR "LIST" MSDP_VAL "COMMANDS" IAC SE
	/// server - IAC   SB MSDP MSDP_VAR "COMMANDS" MSDP_VAL MSDP_ARRAY_OPEN … MSDP_ARRAY_CLOSE IAC SE
	/// client - IAC   SB MSDP MSDP_VAR "LIST" MSDP_VAL "REPORTABLE_VARIABLES" IAC SE
	/// server - IAC   SB MSDP MSDP_VAR "REPORTABLE_VARIABLES" MSDP_VAL … IAC SE
	/// client - IAC   SB MSDP MSDP_VAR "SEND" MSDP_VAL "HINT" IAC SE
	/// server - IAC   SB MSDP MSDP_VAR "HINT" MSDP_VAL "THE GAME" IAC SE
	/// </code>
	/// </summary>
	[Test]
	public async Task TheSpecificationsHandshakeRunsBetweenAClientAndAServer()
	{
		var fromServer = new List<string>();

		var model = new MSDPServerModel(_ => default)
		{
			Commands = () => ["LIST", "REPORT", "SEND"],
			Reportable_Variables = new() { ["HINT"] = () => "THE GAME" },
			Sendable_Variables = new() { ["HINT"] = () => "THE GAME" }
		};
		var handler = new MSDPServerHandler(model, logger);

		var (client, server) = await ConnectAsync(handler, fromServer);

		// "server - IAC WILL MSDP", answered by the client's "IAC DO MSDP", which is what tells the
		// server it may speak MSDP at all.
		await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP });
		await SettleAsync(client, server);

		await client.SendMSDPCommand("LIST", "COMMANDS");
		await SettleAsync(client, server);
		await PollUntilAsync(() => fromServer.Count >= 1);

		await client.SendMSDPCommand("LIST", "REPORTABLE_VARIABLES");
		await SettleAsync(client, server);
		await PollUntilAsync(() => fromServer.Count >= 2);

		await client.SendMSDPCommand("SEND", "HINT");
		await SettleAsync(client, server);
		await PollUntilAsync(() => fromServer.Count >= 3);

		await Assert.That(fromServer[0]).IsEqualTo("""{"COMMANDS":["LIST","REPORT","SEND"]}""");
		await Assert.That(fromServer[1]).IsEqualTo("""{"REPORTABLE_VARIABLES":["HINT"]}""");
		await Assert.That(fromServer[2]).IsEqualTo("""{"HINT":"THE GAME"}""");

		await client.DisposeAsync();
		await server.DisposeAsync();
	}

	/// <summary>
	/// "the server should send the requested variables to the client, and re-send each individual
	/// variable whenever it changes" — the same two interpreters, watching a variable change.
	/// </summary>
	[Test]
	public async Task AReportedVariableReachesTheClientAgainWhenItChanges()
	{
		var fromServer = new List<string>();
		var time = "14:00";

		var model = new MSDPServerModel(_ => default)
		{
			Reportable_Variables = new() { ["MUD_TIME"] = () => time }
		};
		var handler = new MSDPServerHandler(model, logger);

		var (client, server) = await ConnectAsync(handler, fromServer);

		await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP });
		await SettleAsync(client, server);

		await client.SendMSDPCommand("REPORT", "MUD_TIME");
		await SettleAsync(client, server);
		await PollUntilAsync(() => fromServer.Count >= 1);

		time = "15:00";
		await model.NotifyChangeAsync("MUD_TIME");
		await SettleAsync(client, server);
		await PollUntilAsync(() => fromServer.Count >= 2);

		await Assert.That(fromServer[0]).IsEqualTo("""{"MUD_TIME":"14:00"}""");
		await Assert.That(fromServer[1]).IsEqualTo("""{"MUD_TIME":"15:00"}""");

		await client.DisposeAsync();
		await server.DisposeAsync();
	}

	#endregion

	#region Carrying a type of your own

	/// <summary>
	/// A room, as the specification's table example describes one. Written in the order the example
	/// lists its variables.
	/// </summary>
	public sealed class Room
	{
		[System.Text.Json.Serialization.JsonPropertyName("VNUM")]
		public int Vnum { get; set; }

		[System.Text.Json.Serialization.JsonPropertyName("NAME")]
		public string Name { get; set; } = "";

		[System.Text.Json.Serialization.JsonPropertyName("AREA")]
		public string Area { get; set; } = "";

		[System.Text.Json.Serialization.JsonPropertyName("TERRAIN")]
		public string Terrain { get; set; } = "";

		[System.Text.Json.Serialization.JsonPropertyName("EXITS")]
		public Dictionary<string, string> Exits { get; set; } = [];
	}

	/// <summary>
	/// The variables of one message, as a type: one property per variable.
	/// </summary>
	public sealed class RoomMessage
	{
		[System.Text.Json.Serialization.JsonPropertyName("ROOM")]
		public Room Room { get; set; } = new();
	}

	/// <summary>
	/// A type of your own goes both ways through its serializer contract, which the source generator
	/// writes at compile time — no reflection, so a server built this way still compiles ahead of
	/// time.
	/// </summary>
	[Test]
	public async Task ATypeOfYourOwnIsWrittenAsTheSpecificationsTable()
	{
		var message = new RoomMessage
		{
			Room = new Room
			{
				Vnum = 6008,
				Name = "The forest clearing",
				Area = "Haon Dor",
				Terrain = "forest",
				Exits = new() { { "n", "6011" }, { "e", "6007" } }
			}
		};

		var written = MSDPLibrary.ReportVariables(message, Encoding, MsdpTestJsonContext.Default.RoomMessage);

		await AssertByteArraysEqual(written, RoomPayload());
	}

	/// <summary>
	/// And read back into it. MSDP has no numbers, so <c>VNUM</c> arrives as text and the context
	/// asks for numbers to be read from strings — the one thing a type crossing MSDP has to say.
	/// </summary>
	[Test]
	public async Task AMessageIsReadBackIntoATypeOfYourOwn()
	{
		var room = MSDPLibrary.Scan(RoomPayload(), Encoding, MsdpTestJsonContext.Default.RoomMessage)!.Room;

		await Assert.That(room.Vnum).IsEqualTo(6008);
		await Assert.That(room.Name).IsEqualTo("The forest clearing");
		await Assert.That(room.Area).IsEqualTo("Haon Dor");
		await Assert.That(room.Terrain).IsEqualTo("forest");
		await Assert.That(room.Exits["n"]).IsEqualTo("6011");
		await Assert.That(room.Exits["e"]).IsEqualTo("6007");
	}

	/// <summary>
	/// Out and back: the object that goes on the wire is the object that comes off it.
	/// </summary>
	[Test]
	public async Task ATypeOfYourOwnRoundTrips()
	{
		var message = new RoomMessage
		{
			Room = new Room
			{
				Vnum = 6008,
				Name = "The forest clearing",
				Area = "Haon Dor",
				Terrain = "forest",
				Exits = new() { { "n", "6011" }, { "e", "6007" } }
			}
		};

		var written = MSDPLibrary.ReportVariables(message, Encoding, MsdpTestJsonContext.Default.RoomMessage);
		var read = MSDPLibrary.Scan(written, Encoding, MsdpTestJsonContext.Default.RoomMessage)!;

		await Assert.That(read.Room.Vnum).IsEqualTo(message.Room.Vnum);
		await Assert.That(read.Room.Name).IsEqualTo(message.Room.Name);
		await Assert.That(read.Room.Area).IsEqualTo(message.Room.Area);
		await Assert.That(read.Room.Terrain).IsEqualTo(message.Room.Terrain);
		await Assert.That(read.Room.Exits).IsEquivalentTo(message.Room.Exits);
	}

	/// <summary>
	/// One value on its own, rather than a whole message.
	/// </summary>
	[Test]
	public async Task ATypeOfYourOwnCanBeOneValue()
	{
		var exits = new Dictionary<string, string> { { "n", "6011" }, { "e", "6007" } };

		var written = MSDPLibrary.Report(exits, Encoding, MsdpTestJsonContext.Default.DictionaryStringString);

		await AssertByteArraysEqual(written, Payload(
			Trigger.MSDP_TABLE_OPEN,
			Trigger.MSDP_VAR, "n", Trigger.MSDP_VAL, "6011",
			Trigger.MSDP_VAR, "e", Trigger.MSDP_VAL, "6007",
			Trigger.MSDP_TABLE_CLOSE));
	}

	#endregion

	#region Helpers

	/// <summary>
	/// The specification's table example, as bytes.
	/// </summary>
	private static byte[] RoomPayload() => Payload(
		Trigger.MSDP_VAR, "ROOM",
		Trigger.MSDP_VAL, Trigger.MSDP_TABLE_OPEN,
		Trigger.MSDP_VAR, "VNUM", Trigger.MSDP_VAL, "6008",
		Trigger.MSDP_VAR, "NAME", Trigger.MSDP_VAL, "The forest clearing",
		Trigger.MSDP_VAR, "AREA", Trigger.MSDP_VAL, "Haon Dor",
		Trigger.MSDP_VAR, "TERRAIN", Trigger.MSDP_VAL, "forest",
		Trigger.MSDP_VAR, "EXITS", Trigger.MSDP_VAL, Trigger.MSDP_TABLE_OPEN,
		Trigger.MSDP_VAR, "n", Trigger.MSDP_VAL, "6011",
		Trigger.MSDP_VAR, "e", Trigger.MSDP_VAL, "6007",
		Trigger.MSDP_TABLE_CLOSE,
		Trigger.MSDP_TABLE_CLOSE);

	/// <summary>
	/// An MSDP payload, from <see cref="Trigger"/> markers and text.
	/// </summary>
	private static byte[] Payload(params object[] parts)
	{
		var bytes = new List<byte>();

		foreach (var part in parts)
		{
			switch (part)
			{
				case Trigger trigger:
					bytes.Add((byte)trigger);
					break;
				case string text:
					bytes.AddRange(Encoding.GetBytes(text));
					break;
				default:
					throw new ArgumentException($"Unsupported part: {part}");
			}
		}

		return bytes.ToArray();
	}

	/// <summary>
	/// Feeds a whole <c>IAC SB MSDP … IAC SE</c> subnegotiation to a server and returns the message
	/// it reports.
	/// </summary>
	private static async Task<string> ReceiveAsync(byte[] subnegotiation)
	{
		string received = null;

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<MSDPProtocol>()
				.OnMSDPMessage((_, message) =>
				{
					received = message;
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP]);
		await InterpretAndWaitAsync(server, subnegotiation);
		await PollUntilAsync(() => received != null);

		await server.DisposeAsync();

		await Assert.That(received).IsNotNull();
		return received;
	}

	/// <summary>
	/// A client and a server wired to each other: what one writes as negotiation is read by the
	/// other, which is as close to a connection as a test gets without a socket.
	/// </summary>
	/// <remarks>
	/// Whichever is built first has no one to write to yet — a server announces <c>IAC WILL MSDP</c>
	/// as it comes up — so those first bytes are held and delivered once both ends exist.
	/// </remarks>
	private static async Task<(TelnetInterpreter Client, TelnetInterpreter Server)> ConnectAsync(
		MSDPServerHandler handler, List<string> fromServer)
	{
		var pendingToClient = new List<byte[]>();
		TelnetInterpreter client = null;

		ValueTask ToClient(ReadOnlyMemory<byte> data)
		{
			if (client is null)
			{
				pendingToClient.Add(data.ToArray());
				return ValueTask.CompletedTask;
			}

			return client.InterpretByteArrayAsync(data.ToArray());
		}

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(ToClient)
			.AddPlugin<MSDPProtocol>()
				.OnMSDPMessage(handler.HandleAsync)
			.BuildAsync();

		client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => server.InterpretByteArrayAsync(data.ToArray()))
			.AddPlugin<MSDPProtocol>()
				.OnMSDPMessage((_, message) =>
				{
					lock (fromServer)
					{
						fromServer.Add(message);
					}

					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		foreach (var held in pendingToClient)
		{
			await client.InterpretByteArrayAsync(held);
		}

		await SettleAsync(client, server);
		return (client, server);
	}

	/// <summary>
	/// Lets both sides finish what the other's bytes started. Each interpreter's negotiation output
	/// is fed straight into the other, so a single exchange can take a few passes to come to rest.
	/// </summary>
	private static async Task SettleAsync(TelnetInterpreter client, TelnetInterpreter server)
	{
		for (var pass = 0; pass < 4; pass++)
		{
			await client.WaitForProcessingAsync();
			await server.WaitForProcessingAsync();
		}
	}

	#endregion
}

/// <summary>
/// The serializer contracts for the types above, written by the source generator at compile time —
/// which is what lets MSDP carry them without reflecting over anything.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
	NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
[System.Text.Json.Serialization.JsonSerializable(typeof(MSDPSpecificationTests.RoomMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MSDPSpecificationTests.Room))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class MsdpTestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
