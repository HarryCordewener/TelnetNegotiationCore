using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// A TNC server and a TNC client wired to each other, so that each change in
/// <see cref="MudStandardsConformanceTests"/> is checked against a real peer rather than against bytes
/// written by hand: NEW-ENVIRON in the MNES direction, a copyover with compression running both ways,
/// and a second copyover after the first.
/// </summary>
public class MudStandardsLinkTests : BaseTest
{
	/// <summary>
	/// Two interpreters joined back to back. What one writes is queued for the other and delivered by
	/// <see cref="SettleAsync"/>, so nothing is fed into an interpreter from inside another's write.
	/// </summary>
	private sealed class Link : IAsyncDisposable
	{
		private readonly Queue<byte[]> _toClient = new();
		private readonly Queue<byte[]> _toServer = new();

		public TelnetInterpreter Server { get; private set; } = null!;
		public TelnetInterpreter Client { get; private set; } = null!;

		/// <summary>Every write the server made, as it went on the wire.</summary>
		public List<byte[]> ServerWire { get; } = [];

		/// <summary>Every write the client made, as it went on the wire.</summary>
		public List<byte[]> ClientWire { get; } = [];

		public List<string> ServerLines { get; } = [];
		public List<string> ClientLines { get; } = [];

		public static async Task<Link> CreateAsync(
			Func<TelnetInterpreterBuilder, TelnetInterpreterBuilder> server,
			Func<TelnetInterpreterBuilder, TelnetInterpreterBuilder> client)
		{
			var link = new Link();
			link.Server = await server(link.Builder(TelnetInterpreter.TelnetMode.Server)).BuildAsync();
			link.Client = await client(link.Builder(TelnetInterpreter.TelnetMode.Client)).BuildAsync();
			await link.SettleAsync();
			return link;
		}

		private TelnetInterpreterBuilder Builder(TelnetInterpreter.TelnetMode mode)
		{
			var server = mode == TelnetInterpreter.TelnetMode.Server;
			var queue = server ? _toClient : _toServer;
			var wire = server ? ServerWire : ClientWire;
			var lines = server ? ServerLines : ClientLines;

			return new TelnetInterpreterBuilder()
				.UseMode(mode)
				.UseLogger(logger)
				.OnSubmit((data, encoding, _) =>
				{
					lock (lines) lines.Add(encoding.GetString(data));
					return ValueTask.CompletedTask;
				})
				.OnNegotiation(data =>
				{
					var bytes = data.ToArray();
					lock (queue)
					{
						queue.Enqueue(bytes);
						wire.Add(bytes);
					}
					return ValueTask.CompletedTask;
				});
		}

		/// <summary>Delivers what each side wrote to the other until neither has anything left to say.</summary>
		public async Task SettleAsync()
		{
			for (var round = 0; round < 100; round++)
			{
				byte[][] toClient, toServer;
				lock (_toClient) { toClient = [.. _toClient]; _toClient.Clear(); }
				lock (_toServer) { toServer = [.. _toServer]; _toServer.Clear(); }

				if (toClient.Length == 0 && toServer.Length == 0)
				{
					return;
				}

				foreach (var chunk in toClient) await Client.InterpretByteArrayAsync(chunk);
				foreach (var chunk in toServer) await Server.InterpretByteArrayAsync(chunk);
				await Client.WaitForProcessingAsync(additionalDelayMs: 10);
				await Server.WaitForProcessingAsync(additionalDelayMs: 10);
			}

			throw new InvalidOperationException("The two sides never stopped talking to each other.");
		}

		public T ServerPlugin<T>() where T : class, Plugins.ITelnetProtocolPlugin => Server.PluginManager!.GetPlugin<T>()!;
		public T ClientPlugin<T>() where T : class, Plugins.ITelnetProtocolPlugin => Client.PluginManager!.GetPlugin<T>()!;

		public async ValueTask DisposeAsync()
		{
			await Client.DisposeAsync();
			await Server.DisposeAsync();
		}
	}

	private static byte[] Frame(Trigger verb, Trigger option) => [(byte)Trigger.IAC, (byte)verb, (byte)option];

	private static int Count(IEnumerable<byte[]> writes, byte[] frame) =>
		writes.Count(w => w.AsSpan().SequenceEqual(frame));

	[Test]
	public async Task ClientNamesItselfToTheServerThroughMnes()
	{
		var received = new List<Dictionary<string, string>>();
		await using var link = await Link.CreateAsync(
			server => server.AddPlugin<NewEnvironProtocol>()
				.OnEnvironmentVariables((env, _) =>
				{
					lock (received) received.Add(new Dictionary<string, string>(env));
					return ValueTask.CompletedTask;
				}),
			client => client.WithClientIdentity("PROBE", "2.5").AddPlugin<NewEnvironProtocol>());

		await Assert.That(received.Count).IsEqualTo(1);
		await Assert.That(received[0]["CLIENT_NAME"]).IsEqualTo("PROBE");
		await Assert.That(received[0]["CLIENT_VERSION"]).IsEqualTo("2.5");

		// Each side said its part once, in the MNES direction, and never the other side's.
		await Assert.That(Count(link.ServerWire, Frame(Trigger.DO, Trigger.NEWENVIRON))).IsEqualTo(1);
		await Assert.That(Count(link.ServerWire, Frame(Trigger.WILL, Trigger.NEWENVIRON))).IsEqualTo(0);
		await Assert.That(Count(link.ClientWire, Frame(Trigger.WILL, Trigger.NEWENVIRON))).IsEqualTo(1);
		await Assert.That(Count(link.ClientWire, Frame(Trigger.DO, Trigger.NEWENVIRON))).IsEqualTo(0);
		await Assert.That(link.ServerPlugin<NewEnvironProtocol>().IsNegotiated).IsTrue();
		await Assert.That(link.ClientPlugin<NewEnvironProtocol>().IsNegotiated).IsTrue();
	}

	/// <summary>
	/// A copyover with every option a MUD typically runs, compression on in both directions, done
	/// twice. After each withdrawal the two sides still understand each other in the clear; after
	/// each renewal they are back where a fresh connection would be.
	/// </summary>
	[Test]
	public async Task CopyoverTwiceWithCompressionBothWays()
	{
		var gmcp = new List<string>();
		var environ = 0;
		var ttypes = new List<string[]>();

		await using var link = await Link.CreateAsync(
			server => server
				.AddPlugin<MCCPProtocol>()
				.AddPlugin<GMCPProtocol>()
				.AddPlugin<TerminalTypeProtocol>()
					.OnTerminalTypes(types =>
					{
						lock (ttypes) ttypes.Add([.. types]);
						return ValueTask.CompletedTask;
					})
				.AddPlugin<NewEnvironProtocol>()
					.OnEnvironmentVariables((_, _) =>
					{
						environ++;
						return ValueTask.CompletedTask;
					})
				.AddPlugin<NAWSProtocol>(),
			client => client
				.WithClientIdentity("PROBE", "1.0")
				.AddPlugin<MCCPProtocol>()
				.AddPlugin<GMCPProtocol>()
					.OnGMCPMessage(message =>
					{
						lock (gmcp) gmcp.Add(message.Package);
						return ValueTask.CompletedTask;
					})
				.AddPlugin<TerminalTypeProtocol>()
				.AddPlugin<NewEnvironProtocol>()
				.AddPlugin<NAWSProtocol>());

		var fresh = ttypes.Last();
		await AssertConnectedAsync(link, gmcp, "first connection");
		await Assert.That(environ).IsEqualTo(1);

		for (var copyover = 1; copyover <= 2; copyover++)
		{
			await link.Server.UnannounceSupportAsync();
			await link.SettleAsync();

			await Assert.That(link.ServerPlugin<MCCPProtocol>().IsMCCP2Enabled).IsFalse();
			await Assert.That(link.ClientPlugin<MCCPProtocol>().IsMCCP2Enabled).IsFalse();
			await Assert.That(link.ClientPlugin<MCCPProtocol>().IsMCCP3Enabled).IsFalse();
			await Assert.That(link.ClientPlugin<GMCPProtocol>().IsNegotiated).IsFalse();

			// In the clear, both ways, while the new process starts.
			await link.Client.SendAsync(Encoding.ASCII.GetBytes($"during {copyover}"));
			await link.Server.SendAsync(Encoding.ASCII.GetBytes($"wait {copyover}"));
			await link.SettleAsync();
			await Assert.That(link.ServerLines).Contains($"during {copyover}");
			await Assert.That(link.ClientLines).Contains($"wait {copyover}");

			// The inflater can only tell the client's stream is over from the first byte after it,
			// so the server sees MCCP3 end once the client has said something in the clear.
			await Assert.That(link.ServerPlugin<MCCPProtocol>().IsMCCP3Enabled).IsFalse();

			await link.Server.AnnounceSupportAsync();
			await link.SettleAsync();

			await AssertConnectedAsync(link, gmcp, $"copyover {copyover}");
			await Assert.That(environ).IsEqualTo(1 + copyover);

			// The client started its terminal-type cycle over, so the server read the same list.
			await Assert.That(ttypes.Last()).IsEquivalentTo(fresh);
		}
	}

	private static async Task AssertConnectedAsync(Link link, List<string> gmcp, string when)
	{
		await Assert.That(link.ServerPlugin<MCCPProtocol>().IsMCCP2Enabled).IsTrue().Because(when);
		await Assert.That(link.ServerPlugin<MCCPProtocol>().IsMCCP3Enabled).IsTrue().Because(when);
		await Assert.That(link.ClientPlugin<MCCPProtocol>().IsMCCP2Enabled).IsTrue().Because(when);
		await Assert.That(link.ClientPlugin<MCCPProtocol>().IsMCCP3Enabled).IsTrue().Because(when);
		await Assert.That(link.ServerPlugin<GMCPProtocol>().IsNegotiated).IsTrue().Because(when);
		await Assert.That(link.ClientPlugin<NewEnvironProtocol>().IsNegotiated).IsTrue().Because(when);

		var line = $"look {when}";
		await link.Client.SendAsync(Encoding.ASCII.GetBytes(line));
		var package = $"Room.Info.{gmcp.Count}";
		await link.Server.SendGMCPCommand(package, "{}");
		await link.SettleAsync();

		await Assert.That(link.ServerLines).Contains(line).Because(when);
		await Assert.That(gmcp).Contains(package).Because(when);
	}

	/// <summary>
	/// A negotiation callback that writes to a second interpreter, as a proxy does, must not have the
	/// second interpreter's writes counted among the first one's offers.
	/// </summary>
	[Test]
	public async Task OffersAreNotTakenFromAnotherInterpretersWrites()
	{
		var downstream = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.BuildAsync();

		var proxy = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => downstream.WriteToNetworkAsync(data))
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<NAWSProtocol>()
			.BuildAsync();

		await Assert.That(proxy.InitialOffers.Count).IsEqualTo(2);
		await Assert.That(downstream.InitialOffers.Count).IsEqualTo(0);

		await proxy.UnannounceSupportAsync();
		await proxy.AnnounceSupportAsync();
		await Assert.That(proxy.InitialOffers.Count).IsEqualTo(2);
		await Assert.That(downstream.InitialOffers.Count).IsEqualTo(0);

		await proxy.DisposeAsync();
		await downstream.DisposeAsync();
	}
}
