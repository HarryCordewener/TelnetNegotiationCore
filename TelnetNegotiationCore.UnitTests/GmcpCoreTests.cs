using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Gmcp;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The GMCP Core package (https://mudstandards.org/gmcp/core), fed the exact messages real clients
/// send.
/// </summary>
public class GmcpCoreTests : BaseTest
{
	private static readonly Encoding Encoding = Encoding.UTF8;

	/// <summary>
	/// What Mudlet sends as soon as GMCP is agreed (src/ctelnet.cpp), byte for byte through the
	/// interpreter.
	/// </summary>
	[Test]
	public async Task MudletsOpeningIsRead()
	{
		var (telnet, _, gmcp) = await ServerAsync();

		await telnet.InterpretByteArrayAsync(Gmcp("""Core.Hello { "client": "Mudlet", "version": "4.19.1"}"""));
		await telnet.InterpretByteArrayAsync(Gmcp("""Core.Supports.Set [ "Char 1", "Char.Skills 1", "Char.Items 1", "Room 1", "IRE.Rift 1", "IRE.Composer 1", "Client.Media 1", "Char.Login 2"]"""));
		await telnet.WaitForProcessingAsync();
		await PollUntilAsync(() => gmcp.SupportedModules.Count > 0);

		await Assert.That(gmcp.ClientName).IsEqualTo("Mudlet");
		await Assert.That(gmcp.ClientVersion).IsEqualTo("4.19.1");
		await Assert.That(gmcp.SupportedModules["Char.Login"]).IsEqualTo(2);
		await Assert.That(gmcp.Supports("Char.Vitals")).IsTrue();
		await Assert.That(gmcp.Supports("Room.Info")).IsTrue();
		await Assert.That(gmcp.Supports("IRE.Rift.List")).IsTrue();
		await Assert.That(gmcp.Supports("Comm.Channel.Text")).IsFalse();
		await Assert.That(gmcp.Supports("Character")).IsFalse();

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// Blightmud (resources/lua/gmcp.lua) never sends Set: it adds one module at a time, and its
	/// Remove carries a version the specification leaves out.
	/// </summary>
	[Test]
	public async Task BlightmudsAddAndRemoveAreRead()
	{
		var gmcp = new GmcpServerSession((_, _) => default);

		await gmcp.HandleAsync("Core.Hello", """{"client":"blightmud","version":"5.3.1"}""");
		await gmcp.HandleAsync("Core.Supports.Add", """["Char 1"]""");
		await gmcp.HandleAsync("Core.Supports.Add", """["Room 1"]""");
		await gmcp.HandleAsync("Core.Supports.Remove", """["Char 1"]""");

		await Assert.That(gmcp.ClientName).IsEqualTo("blightmud");
		await Assert.That(gmcp.SupportedModules.Keys).IsEquivalentTo(new[] { "Room" });
	}

	/// <summary>
	/// The Core page's own example capitalises the keys; Mudlet does not. Either is read.
	/// </summary>
	[Test]
	public async Task HelloKeysAreReadInEitherCase()
	{
		var gmcp = new GmcpServerSession((_, _) => default);

		await gmcp.HandleAsync("Core.Hello", """{"Client":"Mudlet","Version":"3.0.0"}""");

		await Assert.That(gmcp.ClientName).IsEqualTo("Mudlet");
		await Assert.That(gmcp.ClientVersion).IsEqualTo("3.0.0");
	}

	/// <summary>
	/// "If another Core.Supports.*** package has been received earlier, the list is deleted and
	/// replaced with the new one", and for Add, "the new version number takes precedence ... even if
	/// the newly sent number is lower".
	/// </summary>
	[Test]
	public async Task SetReplacesAndAddOverwritesTheVersion()
	{
		var gmcp = new GmcpServerSession((_, _) => default);

		await gmcp.HandleAsync("Core.Supports.Set", """["Char 2", "Room 1"]""");
		await gmcp.HandleAsync("Core.Supports.Set", """["Char 3"]""");
		await gmcp.HandleAsync("Core.Supports.Add", """["Char 1"]""");

		await Assert.That(gmcp.SupportedModules.Count).IsEqualTo(1);
		await Assert.That(gmcp.SupportedModules["char"]).IsEqualTo(1);
	}

	/// <summary>
	/// "The package name can be case insensitive."
	/// </summary>
	[Test]
	public async Task CorePackageNamesAreMatchedWithoutRegardToCase()
	{
		var gmcp = new GmcpServerSession((_, _) => default);

		await gmcp.HandleAsync("core.hello", """{"client":"x","version":"1"}""");
		await gmcp.HandleAsync("CORE.SUPPORTS.SET", """["Char 1"]""");

		await Assert.That(gmcp.ClientName).IsEqualTo("x");
		await Assert.That(gmcp.Supports("char.vitals")).IsTrue();
	}

	/// <summary>
	/// "The server responds to the request by replying with Core.Ping without a body", and without
	/// the space: "When sending a command without a data section the space should be omitted."
	/// </summary>
	[Test]
	public async Task PingIsAnsweredWithABareCorePing()
	{
		var (telnet, sent, gmcp) = await ServerAsync();

		await telnet.InterpretByteArrayAsync(Gmcp("Core.Ping 42"));
		await telnet.WaitForProcessingAsync();
		await PollUntilAsync(() => sent.Count > 0);

		await AssertByteArraysEqual(sent.Single(), Gmcp("Core.Ping"));
		await Assert.That(gmcp.ReportedRoundTripMilliseconds).IsEqualTo(42d);

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task KeepAliveAndOtherPackagesReachTheirCallbacks()
	{
		var keptAlive = 0;
		var others = new List<(string, string)>();
		var gmcp = new GmcpServerSession((_, _) => default)
		{
			OnKeepAliveAsync = () => { keptAlive++; return default; },
			OnMessageAsync = (package, data) => { others.Add((package, data)); return default; }
		};

		await gmcp.HandleAsync("Core.KeepAlive", "");
		await gmcp.HandleAsync("Char.Login.Credentials", """{"account":"a"}""");

		await Assert.That(keptAlive).IsEqualTo(1);
		await Assert.That(others).IsEquivalentTo(new[] { ("Char.Login.Credentials", """{"account":"a"}""") });
	}

	/// <summary>
	/// A client that never listed a module is not sent it by <see cref="GmcpServerSession.SendIfSupportedAsync"/>,
	/// and is by <see cref="GmcpServerSession.SendAsync"/>.
	/// </summary>
	[Test]
	public async Task SendIfSupportedConsultsTheList()
	{
		var sent = new List<string>();
		var gmcp = new GmcpServerSession((package, _) => { sent.Add(package); return default; });

		await gmcp.HandleAsync("Core.Supports.Set", """["Char 1"]""");

		await Assert.That(await gmcp.SendIfSupportedAsync("Char.Vitals", "{}")).IsTrue();
		await Assert.That(await gmcp.SendIfSupportedAsync("Room.Info", "{}")).IsFalse();
		await gmcp.SendAsync("Room.Info", "{}");

		await Assert.That(sent).IsEquivalentTo(new[] { "Char.Vitals", "Room.Info" });
	}

	/// <summary>
	/// <c>Core.Goodbye "Goodbye, adventurer"</c>, from the specification.
	/// </summary>
	[Test]
	public async Task GoodbyeSendsTheReasonAsAJsonString()
	{
		var (telnet, sent, gmcp) = await ServerAsync();

		await gmcp.GoodbyeAsync("Goodbye, adventurer");

		await AssertByteArraysEqual(sent.Single(), Gmcp("""Core.Goodbye "Goodbye, adventurer" """.TrimEnd()));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// A copyover forgets what the client said.
	/// </summary>
	[Test]
	public async Task ResetForgetsTheClient()
	{
		var gmcp = new GmcpServerSession((_, _) => default);
		await gmcp.HandleAsync("Core.Hello", """{"client":"Mudlet","version":"1"}""");
		await gmcp.HandleAsync("Core.Supports.Set", """["Char 1"]""");

		gmcp.Reset();

		await Assert.That(gmcp.ClientName).IsNull();
		await Assert.That(gmcp.Supports("Char")).IsFalse();
	}

	/// <summary>
	/// "Core.Hello needs to be the first message that the client sends", so a client sends it from
	/// <see cref="GMCPProtocol.OnGMCPNegotiated"/>, and it has to reach the wire after the client's
	/// <c>DO GMCP</c>, not ahead of it.
	/// </summary>
	[Test]
	public async Task AClientIntroducesItselfAfterAgreeingToGMCP()
	{
		var sent = new List<byte[]>();
		TelnetInterpreter telnet = null;
		var gmcp = new GmcpClientSession((package, data) => telnet.SendGMCPCommand(package, data));

		telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { sent.Add(data.ToArray()); return default; })
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(gmcp.HandleAsync)
				.OnGMCPNegotiated(async agreed =>
				{
					if (agreed)
					{
						await gmcp.HelloAsync("TNC", "1.0");
						await gmcp.SetSupportsAsync([new GmcpModule("Char", 1), new GmcpModule("Room", 1)]);
					}
				})
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
		await telnet.WaitForProcessingAsync();
		await PollUntilAsync(() => sent.Count >= 3);

		await AssertByteArraysEqual(sent[0], new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP });
		await AssertByteArraysEqual(sent[1], Gmcp("""Core.Hello {"client":"TNC","version":"1.0"}"""));
		await AssertByteArraysEqual(sent[2], Gmcp("""Core.Supports.Set ["Char 1","Room 1"]"""));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// A TNC client and a TNC server, each with its Core session, measuring a round trip.
	/// </summary>
	[Test]
	public async Task APingMakesTheRoundTripBetweenTwoSessions()
	{
		TelnetInterpreter client = null;
		TelnetInterpreter server = null;
		var clientGmcp = new GmcpClientSession((package, data) => client.SendGMCPCommand(package, data));
		var serverGmcp = new GmcpServerSession((package, data) => server.SendGMCPCommand(package, data));

		client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(async data => await server.InterpretByteArrayAsync(data.ToArray()))
			.AddPlugin<GMCPProtocol>().OnGMCPMessage(clientGmcp.HandleAsync)
			.BuildAsync();

		server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(async data => await client.InterpretByteArrayAsync(data.ToArray()))
			.AddPlugin<GMCPProtocol>().OnGMCPMessage(serverGmcp.HandleAsync)
			.BuildAsync();

		await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
		await client.WaitForProcessingAsync();
		await server.WaitForProcessingAsync();

		await clientGmcp.PingAsync();

		await Assert.That(await PollUntilAsync(() => clientGmcp.RoundTripMilliseconds is not null)).IsTrue();

		await client.DisposeAsync();
		await server.DisposeAsync();
	}

	[Test]
	[Arguments("Char 1", "Char", 1)]
	[Arguments("Char.Login 2", "Char.Login", 2)]
	[Arguments("Char", "Char", 1)]
	[Arguments("  Room   3 ", "Room", 3)]
	[Arguments("Room zero", "Room", 1)]
	public async Task ModuleEntriesAreRead(string entry, string name, int version)
	{
		await Assert.That(GmcpModule.TryParse(entry, out var module)).IsTrue();
		await Assert.That(module).IsEqualTo(new GmcpModule(name, version));
	}

	private static async Task<(TelnetInterpreter Telnet, List<byte[]> Sent, GmcpServerSession Gmcp)> ServerAsync()
	{
		var sent = new List<byte[]>();
		TelnetInterpreter telnet = null;
		var gmcp = new GmcpServerSession((package, data) => telnet.SendGMCPCommand(package, data));

		telnet = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { sent.Add(data.ToArray()); return default; })
			.AddPlugin<GMCPProtocol>().OnGMCPMessage(gmcp.HandleAsync)
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP });
		await telnet.WaitForProcessingAsync();
		sent.Clear();

		return (telnet, sent, gmcp);
	}

	/// <summary>
	/// <c>IAC SB GMCP &lt;text&gt; IAC SE</c>.
	/// </summary>
	private static byte[] Gmcp(string text) =>
		[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP, .. Encoding.GetBytes(text), (byte)Trigger.IAC, (byte)Trigger.SE];
}
