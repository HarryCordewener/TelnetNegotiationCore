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
/// <see cref="GmcpSessionExtensions"/>: a Core session wired to the GMCP plugin in one call.
/// </summary>
public class GmcpSessionSetupTests : BaseTest
{
	private static readonly Encoding Encoding = Encoding.UTF8;

	private static readonly byte[] WillGmcp = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP];

	private static readonly byte[] DoGmcp = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP];

	/// <summary>
	/// The name and version come from WithClientIdentity, with Mudlet's lowercase keys, after the
	/// client's DO and ahead of Core.Supports.Set.
	/// </summary>
	[Test]
	public async Task HelloCarriesTheClientIdentity()
	{
		var sent = new List<byte[]>();

		var telnet = await ClientBuilder(sent)
			.WithClientIdentity("MyClient", "1.0")
			.AddPlugin<GMCPProtocol>()
				.UseGmcpClientSession(out _, new GmcpModule("Char", 1), new GmcpModule("Room", 1))
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(WillGmcp);
		await telnet.WaitForProcessingAsync();
		await PollUntilAsync(() => sent.Count >= 3);

		await Assert.That(sent.Count).IsEqualTo(3);
		await AssertByteArraysEqual(sent[0], DoGmcp);
		await AssertByteArraysEqual(sent[1], Gmcp("""Core.Hello {"client":"MyClient","version":"1.0"}"""));
		await AssertByteArraysEqual(sent[2], Gmcp("""Core.Supports.Set ["Char 1","Room 1"]"""));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// An identity with no version sends no version key, as NEW-ENVIRON sends no CLIENT_VERSION.
	/// No modules, no Core.Supports.Set.
	/// </summary>
	[Test]
	public async Task HelloLeavesOutAMissingVersion()
	{
		var sent = new List<byte[]>();

		var telnet = await ClientBuilder(sent)
			.WithClientIdentity("MyClient")
			.AddPlugin<GMCPProtocol>().UseGmcpClientSession(out _)
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(WillGmcp);
		await telnet.WaitForProcessingAsync();
		await PollUntilAsync(() => sent.Count >= 2);
		await telnet.WaitForProcessingAsync();

		await Assert.That(sent.Count).IsEqualTo(2);
		await AssertByteArraysEqual(sent[1], Gmcp("""Core.Hello {"client":"MyClient"}"""));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// Without an identity there is no name to give, so there is no Core.Hello.
	/// </summary>
	[Test]
	public async Task NoIdentityNoHello()
	{
		var sent = new List<byte[]>();

		var telnet = await ClientBuilder(sent)
			.AddPlugin<GMCPProtocol>().UseGmcpClientSession(out _, new GmcpModule("Char", 1))
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(WillGmcp);
		await telnet.WaitForProcessingAsync();
		await PollUntilAsync(() => sent.Count >= 2);

		await Assert.That(sent.Count).IsEqualTo(2);
		await AssertByteArraysEqual(sent[0], DoGmcp);
		await AssertByteArraysEqual(sent[1], Gmcp("""Core.Supports.Set ["Char 1"]"""));

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// The application's own callbacks still run, whether set before or after the session, and
	/// what the application sends on agreement goes out after Core.Hello.
	/// </summary>
	[Test]
	[Arguments(true)]
	[Arguments(false)]
	public async Task TheApplicationsCallbacksStillRun(bool callbacksFirst)
	{
		var sent = new List<byte[]>();
		var negotiated = new List<bool>();
		var messages = new List<(string, string)>();
		var goodbye = "";
		TelnetInterpreter telnet = null;

		var gmcp = ClientBuilder(sent)
			.WithClientIdentity("MyClient", "1.0")
			.AddPlugin<GMCPProtocol>();

		GmcpClientSession session;
		if (callbacksFirst)
		{
			gmcp = AppCallbacks(gmcp).UseGmcpClientSession(out session);
		}
		else
		{
			gmcp = AppCallbacks(gmcp.UseGmcpClientSession(out session));
		}

		session.OnGoodbyeAsync = reason => { goodbye = reason; return default; };
		telnet = await gmcp.BuildAsync();

		await telnet.InterpretByteArrayAsync(WillGmcp);
		await telnet.InterpretByteArrayAsync(Gmcp("""Core.Goodbye "Bye" """.TrimEnd()));
		await telnet.WaitForProcessingAsync();
		await PollUntilAsync(() => sent.Count >= 3 && messages.Count >= 1);

		await AssertByteArraysEqual(sent[1], Gmcp("""Core.Hello {"client":"MyClient","version":"1.0"}"""));
		await AssertByteArraysEqual(sent[2], Gmcp("Char.Ready"));
		await Assert.That(negotiated).IsEquivalentTo(new[] { true });
		await Assert.That(messages).IsEquivalentTo(new[] { ("Core.Goodbye", "\"Bye\"") });
		await Assert.That(goodbye).IsEqualTo("Bye");

		await telnet.DisposeAsync();

		PluginConfigurationContext<GMCPProtocol> AppCallbacks(PluginConfigurationContext<GMCPProtocol> context) =>
			context
				.OnGMCPNegotiated(async agreed =>
				{
					negotiated.Add(agreed);
					if (agreed)
					{
						await telnet.SendGMCPCommand("Char.Ready", "");
					}
				})
				.OnGMCPMessage(message => { messages.Add(message); return default; });
	}

	/// <summary>
	/// A TNC client and a TNC server, each set up in one call: the server learns who the client is
	/// and what it supports, and a ping makes the round trip.
	/// </summary>
	[Test]
	public async Task AClientAndAServerSetUpInOneCallEach()
	{
		TelnetInterpreter client = null;
		TelnetInterpreter server = null;

		client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(async data => await server.InterpretByteArrayAsync(data.ToArray()))
			.WithClientIdentity("MyClient", "2.5")
			.AddPlugin<GMCPProtocol>().UseGmcpClientSession(out var clientGmcp, new GmcpModule("Char", 1))
			.BuildAsync();

		server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(async data => await client.InterpretByteArrayAsync(data.ToArray()))
			.AddPlugin<GMCPProtocol>().UseGmcpServerSession(out var serverGmcp)
			.BuildAsync();

		await client.InterpretByteArrayAsync(WillGmcp);
		await client.WaitForProcessingAsync();
		await server.WaitForProcessingAsync();

		await Assert.That(await PollUntilAsync(() => serverGmcp.SupportedModules.Count > 0)).IsTrue();
		await Assert.That(serverGmcp.ClientName).IsEqualTo("MyClient");
		await Assert.That(serverGmcp.ClientVersion).IsEqualTo("2.5");
		await Assert.That(serverGmcp.Supports("Char.Vitals")).IsTrue();

		await clientGmcp.PingAsync();
		await Assert.That(await PollUntilAsync(() => clientGmcp.RoundTripMilliseconds is not null)).IsTrue();

		await client.DisposeAsync();
		await server.DisposeAsync();
	}

	private static TelnetInterpreterBuilder ClientBuilder(List<byte[]> sent) =>
		new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { lock (sent) { sent.Add(data.ToArray()); } return default; });

	/// <summary>
	/// <c>IAC SB GMCP &lt;text&gt; IAC SE</c>.
	/// </summary>
	private static byte[] Gmcp(string text) =>
		[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP, .. Encoding.GetBytes(text), (byte)Trigger.IAC, (byte)Trigger.SE];
}
