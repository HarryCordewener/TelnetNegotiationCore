using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Handlers;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="MSDPClientHandler"/> against the requests and answers in the MSDP specification
/// (https://tintin.mudhalla.net/protocols/msdp/).
/// </summary>
public class MSDPClientHandlerTests : BaseTest
{
	private static readonly Encoding Encoding = Encoding.ASCII;

	/// <summary>
	/// "it is allowed to string values together for command-like variables, in this case:
	/// MSDP_VAR "REPORT" MSDP_VAL "HEALTH" MSDP_VAL "HEALTH_MAX"."
	/// </summary>
	[Test]
	public async Task ReportStringsSeveralVariablesTogether()
	{
		var (telnet, sent) = await ClientAsync(Trigger.MSDP);
		var handler = new MSDPClientHandler();

		await handler.ReportAsync(telnet, "HEALTH", "HEALTH_MAX");

		await AssertByteArraysEqual(sent.Single(), Frame(
			Trigger.MSDP_VAR, "REPORT",
			Trigger.MSDP_VAL, "HEALTH",
			Trigger.MSDP_VAL, "HEALTH_MAX"));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// "client - IAC SB MSDP MSDP_VAR "LIST" MSDP_VAL "COMMANDS" IAC SE"
	/// </summary>
	[Test]
	public async Task ListAsksForTheNamedList()
	{
		var (telnet, sent) = await ClientAsync(Trigger.MSDP);

		await new MSDPClientHandler().ListAsync(telnet, "COMMANDS");

		await AssertByteArraysEqual(sent.Single(), Frame(
			Trigger.MSDP_VAR, "LIST",
			Trigger.MSDP_VAL, "COMMANDS"));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// A configurable variable is set by sending it as a variable of its own:
	/// "MSDP_VAR "CLIENT_NAME" MSDP_VAL "Mudlet"".
	/// </summary>
	[Test]
	public async Task SetSendsTheVariableAndItsValue()
	{
		var (telnet, sent) = await ClientAsync(Trigger.MSDP);

		await new MSDPClientHandler().SetAsync(telnet, "CLIENT_NAME", "TNC");

		await AssertByteArraysEqual(sent.Single(), Frame(
			Trigger.MSDP_VAR, "CLIENT_NAME",
			Trigger.MSDP_VAL, "TNC"));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// A server that agreed to GMCP and not to MSDP is asked over GMCP:
	/// "client - IAC SB GMCP 'MSDP {"LIST" : "COMMANDS"}' IAC SE".
	/// </summary>
	[Test]
	public async Task AGMCPOnlyServerIsAskedOverGMCP()
	{
		var (telnet, sent) = await ClientAsync(Trigger.GMCP);
		var handler = new MSDPClientHandler();

		await handler.ListAsync(telnet, "COMMANDS");
		await handler.ReportAsync(telnet, "HEALTH", "HEALTH_MAX");

		await AssertByteArraysEqual(sent[0], GmcpFrame("""MSDP {"LIST":"COMMANDS"}"""));
		await AssertByteArraysEqual(sent[1], GmcpFrame("""MSDP {"REPORT":["HEALTH","HEALTH_MAX"]}"""));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// Every variable in an answer is kept, and a later value replaces an earlier one, which is how
	/// a REPORTed variable stays current.
	/// </summary>
	[Test]
	public async Task VariablesHoldTheLatestValueOfEach()
	{
		var seen = new List<string>();
		var handler = new MSDPClientHandler
		{
			OnVariableAsync = (name, _) =>
			{
				seen.Add(name);
				return ValueTask.CompletedTask;
			}
		};

		await handler.HandleAsync("""{"HEALTH":"100","ROOM":{"VNUM":"6008","EXITS":{"n":"6011"}}}""");
		await handler.HandleAsync("""{"HEALTH":"90"}""");

		await Assert.That(handler.Variables["HEALTH"]!.GetValue<string>()).IsEqualTo("90");
		await Assert.That(handler.Variables["ROOM"]!["EXITS"]!["n"]!.GetValue<string>()).IsEqualTo("6011");
		await Assert.That(seen).IsEquivalentTo(new[] { "HEALTH", "ROOM", "HEALTH" });
	}

	/// <summary>
	/// The whole loop on the wire: a REPORT from the client reaches the server, and the server's
	/// answer lands in the client's table.
	/// </summary>
	[Test]
	public async Task AReportIsAnsweredIntoTheClientsTable()
	{
		var clientHandler = new MSDPClientHandler();
		var serverHandler = new MSDPServerHandler(new MSDPServerModel(_ => default)
		{
			Reportable_Variables = new() { ["HEALTH"] = () => "100" }
		});

		TelnetInterpreter server = null;
		TelnetInterpreter client = null;

		client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(async data => await server.InterpretByteArrayAsync(data.ToArray()))
			.AddPlugin<Protocols.MSDPProtocol>()
				.OnMSDPMessage(clientHandler.HandleAsync)
			.BuildAsync();

		server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(async data => await client.InterpretByteArrayAsync(data.ToArray()))
			.AddPlugin<Protocols.MSDPProtocol>()
				.OnMSDPMessage(serverHandler.HandleAsync)
			.BuildAsync();

		await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP });
		await client.WaitForProcessingAsync();
		await server.WaitForProcessingAsync();

		await clientHandler.ReportAsync(client, "HEALTH");
		await server.WaitForProcessingAsync();
		await client.WaitForProcessingAsync();

		await Assert.That(await PollUntilAsync(() => clientHandler.Variables.ContainsKey("HEALTH"))).IsTrue();
		await Assert.That(clientHandler.Variables["HEALTH"]!.GetValue<string>()).IsEqualTo("100");

		await client.DisposeAsync();
		await server.DisposeAsync();
	}

	/// <summary>
	/// A client whose server has offered <paramref name="offered"/> and nothing else, with what it
	/// writes from then on.
	/// </summary>
	private static async Task<(TelnetInterpreter Telnet, List<byte[]> Sent)> ClientAsync(Trigger offered)
	{
		var sent = new List<byte[]>();

		var telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				sent.Add(data.ToArray());
				return ValueTask.CompletedTask;
			})
			.AddPlugin<Protocols.GMCPProtocol>()
			.AddPlugin<Protocols.MSDPProtocol>()
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)offered });
		await telnet.WaitForProcessingAsync();
		sent.Clear();

		return (telnet, sent);
	}

	private static byte[] GmcpFrame(string text) =>
		[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP, .. Encoding.GetBytes(text), (byte)Trigger.IAC, (byte)Trigger.SE];

	/// <summary>
	/// <c>IAC SB MSDP &lt;parts&gt; IAC SE</c>, where a part is a <see cref="Trigger"/> byte or text.
	/// </summary>
	private static byte[] Frame(params object[] parts)
	{
		var bytes = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSDP };

		foreach (var part in parts)
		{
			bytes.AddRange(part switch
			{
				Trigger trigger => [(byte)trigger],
				string text => Encoding.GetBytes(text),
				_ => throw new System.ArgumentException($"Unsupported part: {part}")
			});
		}

		bytes.Add((byte)Trigger.IAC);
		bytes.Add((byte)Trigger.SE);
		return bytes.ToArray();
	}
}
