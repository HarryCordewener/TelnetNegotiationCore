using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
/// Behaviour that the MUD specifications collected at mudstandards.org ask for and that MTH-based
/// servers, Mudlet and TinTin++ rely on: the direction of NEW-ENVIRON, the MTTS cycle reset, ending
/// an MCCP stream in order, and withdrawing and renewing offers around a copyover.
/// </summary>
public class MudStandardsConformanceTests : BaseTest
{
	private static readonly byte[] s_sendNewEnviron =
		[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE];

	private static readonly byte[] s_sendTtype =
		[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE];

	private static byte[] Frame(Trigger verb, Trigger option) => [(byte)Trigger.IAC, (byte)verb, (byte)option];

	private static TelnetInterpreterBuilder Builder(TelnetInterpreter.TelnetMode mode, List<byte[]> sent) =>
		new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				lock (sent) sent.Add(data.ToArray());
				return ValueTask.CompletedTask;
			});

	private static byte[][] Snapshot(List<byte[]> sent)
	{
		lock (sent) return [.. sent];
	}

	private static bool Contains(IEnumerable<byte[]> frames, byte[] frame) =>
		frames.Any(f => f.AsSpan().SequenceEqual(frame));

	[Test]
	public async Task ServerAsksForNewEnvironWithDo()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<NewEnvironProtocol>());

		await AssertByteArraysEqual(Snapshot(sent).Single(), Frame(Trigger.DO, Trigger.NEWENVIRON));
		await server.DisposeAsync();
	}

	[Test]
	public async Task ServerAnswersTheClientsWillWithSendOnly()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<NewEnvironProtocol>());
		lock (sent) sent.Clear();

		await InterpretAndWaitAsync(server, Frame(Trigger.WILL, Trigger.NEWENVIRON));

		// The WILL answers this side's DO: an agreement is not answered again.
		await AssertByteArraysEqual(Snapshot(sent).Single(), s_sendNewEnviron);
		await server.DisposeAsync();
	}

	[Test]
	public async Task ServerRefusesToSendVariablesOfItsOwn()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<NewEnvironProtocol>());
		lock (sent) sent.Clear();

		await InterpretAndWaitAsync(server, Frame(Trigger.DO, Trigger.NEWENVIRON));

		await AssertByteArraysEqual(Snapshot(sent).Single(), Frame(Trigger.WONT, Trigger.NEWENVIRON));
		await server.DisposeAsync();
	}

	[Test]
	public async Task ClientAnswersDoWithWillAndWaitsForSend()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.WithClientIdentity("PROBE", "1.0")
			.AddPlugin<NewEnvironProtocol>());
		lock (sent) sent.Clear();

		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.NEWENVIRON));
		await AssertByteArraysEqual(Snapshot(sent).Single(), Frame(Trigger.WILL, Trigger.NEWENVIRON));

		// A repeated DO is the server agreeing again, not asking again.
		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.NEWENVIRON));
		await Assert.That(Snapshot(sent).Length).IsEqualTo(0);

		await InterpretAndWaitAsync(client, s_sendNewEnviron);
		var reply = Snapshot(sent).Single();
		await Assert.That(reply[3]).IsEqualTo((byte)Trigger.IS);
		await Assert.That(Encoding.ASCII.GetString(reply)).Contains("CLIENT_NAME");
		await client.DisposeAsync();
	}

	[Test]
	public async Task ClientStillAcceptsAServerThatAnnouncesWill()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.AddPlugin<NewEnvironProtocol>());
		lock (sent) sent.Clear();

		await InterpretAndWaitAsync(client, Frame(Trigger.WILL, Trigger.NEWENVIRON));

		await AssertByteArraysEqual(Snapshot(sent).Single(), Frame(Trigger.DO, Trigger.NEWENVIRON));
		await client.DisposeAsync();
	}

	[Test]
	public async Task MttsCanClaimSsl()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.WithClientIdentity(new ClientIdentity("PROBE") { Mtts = MttsCapabilities.Ansi | MttsCapabilities.Ssl })
			.AddPlugin<TerminalTypeProtocol>());
		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.TTYPE));

		var reports = new List<string>();
		for (var i = 0; i < 3; i++)
		{
			lock (sent) sent.Clear();
			await InterpretAndWaitAsync(client, s_sendTtype);
			var frame = Snapshot(sent).Single();
			reports.Add(Encoding.ASCII.GetString(frame, 4, frame.Length - 6));
		}

		var mtts = int.Parse(reports.Last()["MTTS ".Length..]);
		await Assert.That(mtts & 2048).IsEqualTo(2048);
		await Assert.That(MttsCapabilityNames.Expand((MttsCapabilities)mtts)).Contains("SSL");
		await client.DisposeAsync();
	}

	[Test]
	public async Task DontTtypeRestartsTheCycle()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.WithClientIdentity("PROBE", "1.0")
			.AddPlugin<TerminalTypeProtocol>());
		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.TTYPE));

		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(client, s_sendTtype);
		var first = Snapshot(sent).Single();
		await InterpretAndWaitAsync(client, s_sendTtype);

		// MTH sends exactly this after its first three requests, and again around a copyover.
		await InterpretAndWaitAsync(client, Frame(Trigger.DONT, Trigger.TTYPE));
		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.TTYPE));
		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(client, s_sendTtype);

		await AssertByteArraysEqual(Snapshot(sent).Last(), first);
		await client.DisposeAsync();
	}

	/// <summary>
	/// An MCCP3 client told <c>WONT MCCP3</c> ends its zlib stream (final block and Adler-32) before
	/// sending anything in the clear, so the server's inflater sees the end it is waiting for.
	/// </summary>
	[Test]
	public async Task Mccp3ClientEndsItsStreamOnWont()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.AddPlugin<MCCPProtocol>());

		await InterpretAndWaitAsync(client, Frame(Trigger.WILL, Trigger.MCCP3));
		lock (sent) sent.Clear();

		await client.SendAsync(Encoding.ASCII.GetBytes("look\n"));
		await InterpretAndWaitAsync(client, Frame(Trigger.WONT, Trigger.MCCP3));
		var compressed = Snapshot(sent).SelectMany(f => f).ToArray();

		lock (sent) sent.Clear();
		await client.SendAsync(Encoding.ASCII.GetBytes("north\n"));
		var after = Snapshot(sent).SelectMany(f => f).ToArray();

		await Assert.That(InflateComplete(compressed, out var text)).IsTrue();
		await Assert.That(text).StartsWith("look\n");
		await Assert.That(Encoding.ASCII.GetString(after)).StartsWith("north");
		await client.DisposeAsync();
	}

	[Test]
	public async Task Mccp2ServerEndsItsStreamOnDont()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<MCCPProtocol>());

		await InterpretAndWaitAsync(server, Frame(Trigger.DO, Trigger.MCCP2));
		lock (sent) sent.Clear();

		await server.SendAsync(Encoding.ASCII.GetBytes("Hello\n"));
		await InterpretAndWaitAsync(server, Frame(Trigger.DONT, Trigger.MCCP2));
		var compressed = Snapshot(sent).SelectMany(f => f).ToArray();

		await Assert.That(InflateComplete(compressed, out var text)).IsTrue();
		await Assert.That(text).StartsWith("Hello");
		await server.DisposeAsync();
	}

	[Test]
	public async Task UnannounceWithdrawsEveryInitialOfferAndAnnounceRepeatsThem()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<MSDPProtocol>()
			.AddPlugin<TerminalTypeProtocol>()
			.AddPlugin<NewEnvironProtocol>()
			.AddPlugin<NAWSProtocol>());

		var offers = server.InitialOffers;
		await Assert.That(offers).Contains(((byte)Trigger.WILL, (byte)Trigger.GMCP));
		await Assert.That(offers).Contains(((byte)Trigger.WILL, (byte)Trigger.MSDP));
		await Assert.That(offers).Contains(((byte)Trigger.DO, (byte)Trigger.TTYPE));
		await Assert.That(offers).Contains(((byte)Trigger.DO, (byte)Trigger.NEWENVIRON));
		await Assert.That(offers).Contains(((byte)Trigger.DO, (byte)Trigger.NAWS));

		lock (sent) sent.Clear();
		await server.UnannounceSupportAsync();
		var withdrawn = Snapshot(sent);

		await Assert.That(withdrawn.Length).IsEqualTo(offers.Count);
		await Assert.That(Contains(withdrawn, Frame(Trigger.WONT, Trigger.GMCP))).IsTrue();
		await Assert.That(Contains(withdrawn, Frame(Trigger.WONT, Trigger.MSDP))).IsTrue();
		await Assert.That(Contains(withdrawn, Frame(Trigger.DONT, Trigger.TTYPE))).IsTrue();
		await Assert.That(Contains(withdrawn, Frame(Trigger.DONT, Trigger.NEWENVIRON))).IsTrue();
		await Assert.That(Contains(withdrawn, Frame(Trigger.DONT, Trigger.NAWS))).IsTrue();

		lock (sent) sent.Clear();
		await server.AnnounceSupportAsync();
		var renewed = Snapshot(sent);

		await Assert.That(Contains(renewed, Frame(Trigger.WILL, Trigger.GMCP))).IsTrue();
		await Assert.That(Contains(renewed, Frame(Trigger.DO, Trigger.TTYPE))).IsTrue();
		await Assert.That(server.InitialOffers.Count).IsEqualTo(offers.Count);
		await server.DisposeAsync();
	}

	/// <summary>
	/// What the client side of a copyover sees from a TNC server: the outbound MCCP2 stream ends
	/// before the <c>WONT</c>s, which go out uncompressed.
	/// </summary>
	[Test]
	public async Task UnannounceEndsCompressionBeforeTheRefusals()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<MCCPProtocol>());

		await InterpretAndWaitAsync(server, Frame(Trigger.DO, Trigger.MCCP2));
		lock (sent) sent.Clear();
		await server.SendAsync(Encoding.ASCII.GetBytes("Copyover in progress\n"));
		await server.UnannounceSupportAsync();
		var frames = Snapshot(sent);

		var refusals = frames.TakeLast(2).ToArray();
		await AssertByteArraysEqual(refusals[0], Frame(Trigger.WONT, Trigger.MCCP2));
		await AssertByteArraysEqual(refusals[1], Frame(Trigger.WONT, Trigger.MCCP3));

		var compressed = frames.SkipLast(2).SelectMany(f => f).ToArray();
		await Assert.That(InflateComplete(compressed, out var text)).IsTrue();
		await Assert.That(text).StartsWith("Copyover in progress");

		var mccp = server.PluginManager!.GetPlugin<MCCPProtocol>()!;
		await Assert.That(mccp.IsMCCP2Enabled).IsFalse();
		await server.DisposeAsync();
	}

	[Test]
	public async Task ServerReadsTheTerminalTypeCycleAgainAfterACopyover()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<TerminalTypeProtocol>());

		static byte[] Is(string type) =>
			[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, .. Encoding.ASCII.GetBytes(type), (byte)Trigger.IAC, (byte)Trigger.SE];

		await InterpretAndWaitAsync(server, Frame(Trigger.WILL, Trigger.TTYPE));
		await InterpretAndWaitAsync(server, Is("MUDLET"));
		await InterpretAndWaitAsync(server, Is("ANSI"));
		await InterpretAndWaitAsync(server, Is("ANSI"));

		await server.UnannounceSupportAsync();
		await Assert.That(server.TerminalTypes.Count).IsEqualTo(0);
		await server.AnnounceSupportAsync();

		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(server, Frame(Trigger.WILL, Trigger.TTYPE));
		await InterpretAndWaitAsync(server, Is("TINTIN++"));

		// A fresh list, so the first name is a new entry and the server asks for the next one.
		await Assert.That(server.TerminalTypes[0]).IsEqualTo("TINTIN++");
		await Assert.That(Contains(Snapshot(sent), s_sendTtype)).IsTrue();
		await server.DisposeAsync();
	}

	/// <summary>
	/// Inflates a zlib stream and reports whether it reached its orderly end, which a stream cut off
	/// at a sync flush does not.
	/// </summary>
	private static bool InflateComplete(byte[] zlib, out string text)
	{
		using var input = new MemoryStream(zlib);
		using var inflater = new ZLibStream(input, CompressionMode.Decompress);
		using var output = new MemoryStream();
		try
		{
			inflater.CopyTo(output);
		}
		catch (InvalidDataException)
		{
			text = string.Empty;
			return false;
		}

		text = Encoding.ASCII.GetString(output.ToArray());

		// The final block plus the four-byte Adler-32 of the inflated bytes ends a complete stream.
		var adler = Adler32(output.ToArray());
		var tail = zlib.AsSpan(zlib.Length - 4);
		return tail[0] == (byte)(adler >> 24) && tail[1] == (byte)(adler >> 16)
			&& tail[2] == (byte)(adler >> 8) && tail[3] == (byte)adler;
	}

	private static uint Adler32(byte[] data)
	{
		uint a = 1, b = 0;
		foreach (var d in data)
		{
			a = (a + d) % 65521;
			b = (b + a) % 65521;
		}
		return (b << 16) | a;
	}
}
