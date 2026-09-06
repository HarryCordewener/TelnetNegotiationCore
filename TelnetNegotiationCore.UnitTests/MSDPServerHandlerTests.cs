using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Handlers;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="MSDPServerHandler"/> against the examples in the MSDP specification
/// (https://tintin.mudhalla.net/protocols/msdp/).
/// </summary>
/// <remarks>
/// Every server response in that document has the same shape:
/// <c>IAC SB MSDP MSDP_VAR "&lt;name&gt;" MSDP_VAL "&lt;value&gt;" IAC SE</c> - a subnegotiation whose
/// payload is one or more variable/value pairs, the value being text, a table or an array. These
/// tests assert those byte sequences, because they are the contract a client on the other end reads.
/// </remarks>
public class MSDPServerHandlerTests : BaseTest
{
	private static readonly Encoding Encoding = Encoding.ASCII;

	/// <summary>
	/// "client - IAC SB MSDP MSDP_VAR "LIST" MSDP_VAL "COMMANDS" IAC SE"
	/// "server - IAC SB MSDP MSDP_VAR "COMMANDS" MSDP_VAL MSDP_ARRAY_OPEN MSDP_VAL "LIST" ... IAC SE"
	/// The list the client asked for comes back named. Answering with a bare array - no
	/// <c>MSDP_VAR "COMMANDS"</c> - leaves the client holding values it cannot attribute to anything.
	/// </summary>
	[Test]
	public async Task ListAnswersWithTheNamedListAsAnArray()
	{
		var (telnet, sent, handler) = await ServerAsync(new MSDPServerModel(NoResetAsync)
		{
			Commands = () => ["LIST"]
		});

		await handler.HandleAsync(telnet, """{"LIST":"COMMANDS"}""");

		await AssertByteArraysEqual(OnlyMessage(sent), Frame(
			Trigger.MSDP_VAR, "COMMANDS",
			Trigger.MSDP_VAL, Trigger.MSDP_ARRAY_OPEN,
			Trigger.MSDP_VAL, "LIST",
			Trigger.MSDP_ARRAY_CLOSE));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// The whole list arrives, whatever order the set enumerates in.
	/// </summary>
	[Test]
	public async Task ListAnswersWithEveryNameInTheList()
	{
		var (telnet, sent, handler) = await ServerAsync(new MSDPServerModel(NoResetAsync)
		{
			Commands = () => ["LIST", "REPORT", "SEND"]
		});

		await handler.HandleAsync(telnet, """{"LIST":"COMMANDS"}""");

		var answer = ParsePayload(OnlyMessage(sent));
		await Assert.That(answer["COMMANDS"]!.AsArray().Select(x => x!.GetValue<string>()).OrderBy(x => x))
			.IsEquivalentTo(new[] { "LIST", "REPORT", "SEND" });

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// "LISTS - Request an array of lists supported by the server." The handler's own documentation
	/// has promised this one since it was written.
	/// </summary>
	[Test]
	public async Task ListLISTSAnswersWithTheListsTheServerSupports()
	{
		var (telnet, sent, handler) = await ServerAsync(new MSDPServerModel(NoResetAsync));

		await handler.HandleAsync(telnet, """{"LIST":"LISTS"}""");

		var answer = ParsePayload(OnlyMessage(sent));
		await Assert.That(answer["LISTS"]!.AsArray().Select(x => x!.GetValue<string>()))
			.Contains("SENDABLE_VARIABLES");

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// A model is configured through an object initialiser, which runs after the constructor. The
	/// list groups have to read the properties when the client asks, not capture whatever delegates
	/// happened to be set while the constructor was still running - or every list answers empty no
	/// matter what the consumer configured.
	/// </summary>
	[Test]
	public async Task ListSeesVariablesAssignedAfterTheConstructorRan()
	{
		var (telnet, sent, handler) = await ServerAsync(new MSDPServerModel(NoResetAsync)
		{
			Sendable_Variables = new() { ["ROOM"] = () => "6008" }
		});

		await handler.HandleAsync(telnet, """{"LIST":"SENDABLE_VARIABLES"}""");

		var answer = ParsePayload(OnlyMessage(sent));
		await Assert.That(answer["SENDABLE_VARIABLES"]!.AsArray().Select(x => x!.GetValue<string>()))
			.Contains("ROOM");

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// "client - IAC SB MSDP MSDP_VAR "SEND" MSDP_VAL "HINT" IAC SE"
	/// "server - IAC SB MSDP MSDP_VAR "HINT" MSDP_VAL "THE GAME" IAC SE"
	/// The answer to SEND is the variable's current value.
	/// </summary>
	[Test]
	public async Task SendAnswersWithTheVariablesCurrentValue()
	{
		var (telnet, sent, handler) = await ServerAsync(new MSDPServerModel(NoResetAsync)
		{
			Sendable_Variables = new() { ["HINT"] = () => "THE GAME" }
		});

		await handler.HandleAsync(telnet, """{"SEND":"HINT"}""");

		await AssertByteArraysEqual(OnlyMessage(sent), Frame(
			Trigger.MSDP_VAR, "HINT",
			Trigger.MSDP_VAL, "THE GAME"));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// "The value of the SEND command should be a list of variables the client wants returned."
	/// "client - IAC SB MSDP MSDP_VAR "SEND" MSDP_VAL "AREA_NAME" MSDP_VAL "ROOM_NAME" IAC SE"
	/// "server - IAC SB MSDP MSDP_VAR "AREA_NAME" MSDP_VAL "Tower of Entropy"
	///                       MSDP_VAR "ROOM_NAME" MSDP_VAL "Tower Pinnacle" IAC SE"
	/// Several variables asked for at once are answered in one subnegotiation, in the order asked.
	/// </summary>
	[Test]
	public async Task SendAnswersSeveralVariablesInOneSubnegotiation()
	{
		var (telnet, sent, handler) = await ServerAsync(new MSDPServerModel(NoResetAsync)
		{
			Sendable_Variables = new()
			{
				["AREA_NAME"] = () => "Tower of Entropy",
				["ROOM_NAME"] = () => "Tower Pinnacle"
			}
		});

		await handler.HandleAsync(telnet, """{"SEND":["AREA_NAME","ROOM_NAME"]}""");

		await AssertByteArraysEqual(OnlyMessage(sent), Frame(
			Trigger.MSDP_VAR, "AREA_NAME",
			Trigger.MSDP_VAL, "Tower of Entropy",
			Trigger.MSDP_VAR, "ROOM_NAME",
			Trigger.MSDP_VAL, "Tower Pinnacle"));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// The specification's table example, produced from a variable whose value is an object:
	/// "IAC SB MSDP MSDP_VAR "ROOM" MSDP_VAL MSDP_TABLE_OPEN MSDP_VAR "VNUM" MSDP_VAL "6008" ... IAC SE"
	/// </summary>
	[Test]
	public async Task SendAnswersATableValuedVariableAsATable()
	{
		var (telnet, sent, handler) = await ServerAsync(new MSDPServerModel(NoResetAsync)
		{
			Sendable_Variables = new()
			{
				["ROOM"] = () => new
				{
					VNUM = "6008",
					NAME = "The forest clearing",
					EXITS = new { n = "6011", e = "6007" }
				}
			}
		});

		await handler.HandleAsync(telnet, """{"SEND":"ROOM"}""");

		await AssertByteArraysEqual(OnlyMessage(sent), Frame(
			Trigger.MSDP_VAR, "ROOM",
			Trigger.MSDP_VAL, Trigger.MSDP_TABLE_OPEN,
			Trigger.MSDP_VAR, "VNUM", Trigger.MSDP_VAL, "6008",
			Trigger.MSDP_VAR, "NAME", Trigger.MSDP_VAL, "The forest clearing",
			Trigger.MSDP_VAR, "EXITS", Trigger.MSDP_VAL, Trigger.MSDP_TABLE_OPEN,
			Trigger.MSDP_VAR, "n", Trigger.MSDP_VAL, "6011",
			Trigger.MSDP_VAR, "e", Trigger.MSDP_VAL, "6007",
			Trigger.MSDP_TABLE_CLOSE,
			Trigger.MSDP_TABLE_CLOSE));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// A variable the server never offered is not answered. There is nothing to say about it: the
	/// specification gives no spelling for "no such variable", and inventing one - the name as its own
	/// value, an empty string - tells the client something untrue.
	/// </summary>
	[Test]
	public async Task SendIgnoresAVariableTheServerDoesNotOffer()
	{
		var (telnet, sent, handler) = await ServerAsync(new MSDPServerModel(NoResetAsync));

		await handler.HandleAsync(telnet, """{"SEND":"NOT_A_VARIABLE"}""");

		await Assert.That(Messages(sent)).IsEmpty();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// "Upon receiving this command the server should send the requested variables to the client, and
	/// re-send each individual variable whenever it changes."
	/// "client - IAC SB MSDP MSDP_VAR "REPORT" MSDP_VAL "MUD_TIME" IAC SE"
	/// "server - IAC SB MSDP MSDP_VAR "MUD_TIME" MSDP_VAL "14:00" IAC SE"
	/// "server - IAC SB MSDP MSDP_VAR "MUD_TIME" MSDP_VAL "15:00" IAC SE"
	/// </summary>
	[Test]
	public async Task ReportSendsTheValueNowAndAgainOnEveryChange()
	{
		var time = "14:00";
		var model = new MSDPServerModel(NoResetAsync)
		{
			Reportable_Variables = new() { ["MUD_TIME"] = () => time }
		};
		var (telnet, sent, handler) = await ServerAsync(model);

		await handler.HandleAsync(telnet, """{"REPORT":"MUD_TIME"}""");

		time = "15:00";
		await model.NotifyChangeAsync("MUD_TIME");

		var messages = Messages(sent);
		await Assert.That(messages.Count).IsEqualTo(2);
		await AssertByteArraysEqual(messages[0], Frame(Trigger.MSDP_VAR, "MUD_TIME", Trigger.MSDP_VAL, "14:00"));
		await AssertByteArraysEqual(messages[1], Frame(Trigger.MSDP_VAR, "MUD_TIME", Trigger.MSDP_VAL, "15:00"));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// A reported variable appears in the REPORTED_VARIABLES list while it is being reported, which is
	/// what makes that list answerable at all.
	/// </summary>
	[Test]
	public async Task ReportedVariablesListsWhatIsBeingReported()
	{
		var model = new MSDPServerModel(NoResetAsync)
		{
			Reportable_Variables = new() { ["MUD_TIME"] = () => "14:00" }
		};
		var (telnet, sent, handler) = await ServerAsync(model);

		await handler.HandleAsync(telnet, """{"REPORT":"MUD_TIME"}""");
		sent.Clear();

		await handler.HandleAsync(telnet, """{"LIST":"REPORTED_VARIABLES"}""");

		var answer = ParsePayload(OnlyMessage(sent));
		await Assert.That(answer["REPORTED_VARIABLES"]!.AsArray().Select(x => x!.GetValue<string>()))
			.Contains("MUD_TIME");

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// "The UNREPORT command is used to remove the report status of variables after the use of the
	/// REPORT command." After it, a change is no longer news.
	/// </summary>
	[Test]
	public async Task UnReportStopsTheUpdates()
	{
		var model = new MSDPServerModel(NoResetAsync)
		{
			Reportable_Variables = new() { ["MUD_TIME"] = () => "14:00" }
		};
		var (telnet, sent, handler) = await ServerAsync(model);

		await handler.HandleAsync(telnet, """{"REPORT":"MUD_TIME"}""");
		await handler.HandleAsync(telnet, """{"UNREPORT":"MUD_TIME"}""");
		sent.Clear();

		await model.NotifyChangeAsync("MUD_TIME");

		await Assert.That(Messages(sent)).IsEmpty();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// "The RESET command works like the LIST command, and can be used to reset groups of variables to
	/// their initial state." The consumer is told which group, since only the game knows what the
	/// initial state of its own variables is.
	/// </summary>
	[Test]
	public async Task ResetTellsTheConsumerWhichGroupToReset()
	{
		var groupsReset = new List<string>();
		var model = new MSDPServerModel(group =>
		{
			groupsReset.Add(group);
			return default;
		});
		var (telnet, _, handler) = await ServerAsync(model);

		await handler.HandleAsync(telnet, """{"RESET":"REPORTABLE_VARIABLES"}""");

		await Assert.That(groupsReset).IsEquivalentTo(new[] { "REPORTABLE_VARIABLES" });

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// The initial state of REPORTED_VARIABLES is that nothing is being reported, so resetting that
	/// group clears the registrations rather than only telling the consumer about it.
	/// </summary>
	[Test]
	public async Task ResetOfReportedVariablesStopsReporting()
	{
		var model = new MSDPServerModel(NoResetAsync)
		{
			Reportable_Variables = new() { ["MUD_TIME"] = () => "14:00" }
		};
		var (telnet, sent, handler) = await ServerAsync(model);

		await handler.HandleAsync(telnet, """{"REPORT":"MUD_TIME"}""");
		await handler.HandleAsync(telnet, """{"RESET":"REPORTED_VARIABLES"}""");
		sent.Clear();

		await model.NotifyChangeAsync("MUD_TIME");

		await Assert.That(Messages(sent)).IsEmpty();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// "Configurable variables are variables on the server that can be altered by the client."
	/// "client - IAC SB MSDP MSDP_VAR "UTF_8" MSDP_VAL "0" MSDP_VAR "XTERM_256_COLORS" MSDP_VAL "1" IAC SE"
	/// A variable that is not one of the five commands is the client setting one of the variables the
	/// server advertised in CONFIGURABLE_VARIABLES.
	/// </summary>
	[Test]
	public async Task AConfigurableVariableSetByTheClientReachesTheConsumer()
	{
		var set = new List<(string Variable, string Value)>();
		var model = new MSDPServerModel(NoResetAsync)
		{
			Configurable_Variables = () => ["UTF_8", "XTERM_256_COLORS"],
			SetCallbackAsync = (variable, value) =>
			{
				set.Add((variable, value));
				return default;
			}
		};
		var (telnet, _, handler) = await ServerAsync(model);

		await handler.HandleAsync(telnet, """{"UTF_8":"0","XTERM_256_COLORS":"1"}""");

		await Assert.That(set).IsEquivalentTo(new[] { ("UTF_8", "0"), ("XTERM_256_COLORS", "1") });

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// A variable the server never advertised as configurable is not one, whatever the client calls it.
	/// </summary>
	[Test]
	public async Task AVariableTheServerDidNotAdvertiseAsConfigurableIsIgnored()
	{
		var set = new List<string>();
		var model = new MSDPServerModel(NoResetAsync)
		{
			SetCallbackAsync = (variable, _) =>
			{
				set.Add(variable);
				return default;
			}
		};
		var (telnet, _, handler) = await ServerAsync(model);

		await handler.HandleAsync(telnet, """{"UTF_8":"0"}""");

		await Assert.That(set).IsEmpty();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// The whole exchange on the wire, from the client's bytes to the server's: the specification's
	/// handshake, steps 7 and 8. The response has to be inside <c>IAC SB MSDP … IAC SE</c> - MSDP
	/// bytes written bare into the stream are not MSDP, they are the connection's text with control
	/// bytes in it.
	/// </summary>
	[Test]
	public async Task AClientsRequestIsAnsweredAsAnMSDPSubnegotiation()
	{
		var (telnet, sent, handler) = await ServerAsync(new MSDPServerModel(NoResetAsync)
		{
			Sendable_Variables = new() { ["HINT"] = () => "THE GAME" }
		});

		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP });
		await telnet.WaitForProcessingAsync();
		sent.Clear();

		await telnet.InterpretByteArrayAsync(Frame(Trigger.MSDP_VAR, "SEND", Trigger.MSDP_VAL, "HINT"));
		await telnet.WaitForProcessingAsync();
		await PollUntilAsync(() => Messages(sent).Count > 0);

		await AssertByteArraysEqual(OnlyMessage(sent), Frame(
			Trigger.MSDP_VAR, "HINT",
			Trigger.MSDP_VAL, "THE GAME"));

		await telnet.DisposeAsync();
	}

	private static ValueTask NoResetAsync(string group) => default;

	private static async Task<(TelnetInterpreter Telnet, List<byte[]> Sent, MSDPServerHandler Handler)> ServerAsync(MSDPServerModel model)
	{
		var sent = new List<byte[]>();
		var handler = new MSDPServerHandler(model);

		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				sent.Add(data.ToArray());
				return ValueTask.CompletedTask;
			})
			.AddPlugin<Protocols.MSDPProtocol>()
				.OnMSDPMessage(handler.HandleAsync)
			.BuildAsync();

		sent.Clear();
		return (telnet, sent, handler);
	}

	/// <summary>
	/// The MSDP subnegotiations among everything written to the network.
	/// </summary>
	private static List<byte[]> Messages(List<byte[]> sent) =>
		sent.Where(x => x.Length > 3
			&& x[0] == (byte)Trigger.IAC
			&& x[1] == (byte)Trigger.SB
			&& x[2] == (byte)Trigger.MSDP).ToList();

	private static byte[] OnlyMessage(List<byte[]> sent)
	{
		var messages = Messages(sent);
		return messages.Count == 1
			? messages[0]
			: throw new Exception($"Expected exactly one MSDP subnegotiation, got {messages.Count}.");
	}

	/// <summary>
	/// Reads a subnegotiation's payload back into JSON, for assertions whose order is not fixed.
	/// </summary>
	private static System.Text.Json.Nodes.JsonObject ParsePayload(byte[] message)
	{
		var payload = message.Skip(3).Take(message.Length - 5).ToArray();
		var scanned = Functional.MSDPLibrary.MSDPScan(payload, Encoding);
		return System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(scanned))!.AsObject();
	}

	/// <summary>
	/// <c>IAC SB MSDP &lt;parts&gt; IAC SE</c>, where a part is a <see cref="Trigger"/> byte or text.
	/// </summary>
	private static byte[] Frame(params object[] parts)
	{
		var bytes = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSDP };

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

		bytes.Add((byte)Trigger.IAC);
		bytes.Add((byte)Trigger.SE);
		return bytes.ToArray();
	}
}
