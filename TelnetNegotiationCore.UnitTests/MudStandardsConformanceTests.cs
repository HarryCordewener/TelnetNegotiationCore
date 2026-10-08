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

	[Test]
	public async Task ServerAsksAgainWhenAClientThatRefusedChangesItsMind()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<NewEnvironProtocol>());
		lock (sent) sent.Clear();

		await InterpretAndWaitAsync(server, Frame(Trigger.WONT, Trigger.NEWENVIRON));
		await Assert.That(Snapshot(sent).Length).IsEqualTo(0);
		await Assert.That(server.PluginManager!.GetPlugin<NewEnvironProtocol>()!.IsNegotiated).IsFalse();

		// The refusal cleared this side's DO, so the client's WILL is a new request to agree to.
		await InterpretAndWaitAsync(server, Frame(Trigger.WILL, Trigger.NEWENVIRON));
		var frames = Snapshot(sent);
		await Assert.That(frames.Length).IsEqualTo(2);
		await AssertByteArraysEqual(frames[0], Frame(Trigger.DO, Trigger.NEWENVIRON));
		await AssertByteArraysEqual(frames[1], s_sendNewEnviron);
		await server.DisposeAsync();
	}

	[Test]
	public async Task ServerNeverRepeatsItsDoForARepeatedWill()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<NewEnvironProtocol>());

		for (var i = 0; i < 3; i++)
		{
			await InterpretAndWaitAsync(server, Frame(Trigger.WILL, Trigger.NEWENVIRON));
		}

		await Assert.That(Snapshot(sent).Count(f => f.AsSpan().SequenceEqual(Frame(Trigger.DO, Trigger.NEWENVIRON))))
			.IsEqualTo(1);
		await server.DisposeAsync();
	}

	[Test]
	public async Task ClientAgreesAgainAfterTheServerSaysDont()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.AddPlugin<NewEnvironProtocol>());
		lock (sent) sent.Clear();

		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.NEWENVIRON));
		await InterpretAndWaitAsync(client, Frame(Trigger.DONT, Trigger.NEWENVIRON));
		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.NEWENVIRON));

		var wills = Snapshot(sent).Count(f => f.AsSpan().SequenceEqual(Frame(Trigger.WILL, Trigger.NEWENVIRON)));
		await Assert.That(wills).IsEqualTo(2);
		await client.DisposeAsync();
	}

	/// <summary>
	/// RFC 1143: once the client has agreed, a <c>DONT</c> is acknowledged with <c>WONT</c>; a
	/// <c>DONT</c> for an option already off is not answered, which is what keeps two sides from
	/// looping.
	/// </summary>
	[Test]
	public async Task ClientAcknowledgesDontOnlyWhileItHadAgreed()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.AddPlugin<NewEnvironProtocol>());

		await InterpretAndWaitAsync(client, Frame(Trigger.DONT, Trigger.NEWENVIRON));
		await Assert.That(Snapshot(sent).Length).IsEqualTo(0);

		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.NEWENVIRON));
		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(client, Frame(Trigger.DONT, Trigger.NEWENVIRON));
		await AssertByteArraysEqual(Snapshot(sent).Single(), Frame(Trigger.WONT, Trigger.NEWENVIRON));
		await Assert.That(client.PluginManager!.GetPlugin<NewEnvironProtocol>()!.IsNegotiated).IsFalse();

		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(client, Frame(Trigger.DONT, Trigger.NEWENVIRON));
		await Assert.That(Snapshot(sent).Length).IsEqualTo(0);
		await client.DisposeAsync();
	}

	/// <summary>An older TNC server announces <c>WILL</c> and then asks; the client still answers.</summary>
	[Test]
	public async Task ClientAnswersALegacyServersSend()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.WithClientIdentity("PROBE", "1.0")
			.AddPlugin<NewEnvironProtocol>());

		await InterpretAndWaitAsync(client, Frame(Trigger.WILL, Trigger.NEWENVIRON));
		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(client, s_sendNewEnviron);

		var reply = Snapshot(sent).Single();
		await Assert.That(reply[3]).IsEqualTo((byte)Trigger.IS);
		await Assert.That(Encoding.ASCII.GetString(reply)).Contains("PROBE");
		await client.DisposeAsync();
	}

	[Test]
	public async Task MttsLeavesSslClearUnlessClaimed()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.WithClientIdentity(new ClientIdentity("PROBE") { Mtts = MttsCapabilities.Ansi | MttsCapabilities.Truecolor })
			.AddPlugin<TerminalTypeProtocol>());
		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.TTYPE));

		string last = null;
		for (var i = 0; i < 3; i++)
		{
			lock (sent) sent.Clear();
			await InterpretAndWaitAsync(client, s_sendTtype);
			var frame = Snapshot(sent).Single();
			last = Encoding.ASCII.GetString(frame, 4, frame.Length - 6);
		}

		await Assert.That(int.Parse(last["MTTS ".Length..]) & 2048).IsEqualTo(0);
		await client.DisposeAsync();
	}

	[Test]
	public async Task DontTtypeBeforeAnySendIsHarmless()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.AddPlugin<TerminalTypeProtocol>()
				.WithTerminalTypes("FIRST", "SECOND"));

		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.TTYPE));
		await InterpretAndWaitAsync(client, Frame(Trigger.DONT, Trigger.TTYPE));
		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.TTYPE));
		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(client, s_sendTtype);

		await Assert.That(Encoding.ASCII.GetString(Snapshot(sent).Single())).Contains("FIRST");
		await client.DisposeAsync();
	}

	/// <summary>
	/// Past the end of its list a client repeats the last entry. <c>DONT TTYPE</c> there still goes
	/// back to the first, for every position in the cycle.
	/// </summary>
	[Test]
	[Arguments(1)]
	[Arguments(2)]
	[Arguments(3)]
	[Arguments(4)]
	[Arguments(7)]
	public async Task DontTtypeRestartsTheCycleFromAnyPosition(int sendsBefore)
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.AddPlugin<TerminalTypeProtocol>()
				.WithTerminalTypes("FIRST", "SECOND", "THIRD"));
		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.TTYPE));

		for (var i = 0; i < sendsBefore; i++)
		{
			await InterpretAndWaitAsync(client, s_sendTtype);
		}

		await InterpretAndWaitAsync(client, Frame(Trigger.DONT, Trigger.TTYPE));
		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.TTYPE));

		var answers = new List<string>();
		for (var i = 0; i < 4; i++)
		{
			lock (sent) sent.Clear();
			await InterpretAndWaitAsync(client, s_sendTtype);
			var frame = Snapshot(sent).Single();
			answers.Add(Encoding.ASCII.GetString(frame, 4, frame.Length - 6));
		}

		await Assert.That(answers).IsEquivalentTo(new[] { "FIRST", "SECOND", "THIRD", "THIRD" });
		await client.DisposeAsync();
	}

	/// <summary>A client's own copyover-style withdrawal keeps its configured list and restarts it.</summary>
	[Test]
	public async Task ClientKeepsItsTerminalTypesAcrossUnannounce()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.AddPlugin<TerminalTypeProtocol>()
				.WithTerminalTypes("FIRST", "SECOND"));
		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.TTYPE));
		await InterpretAndWaitAsync(client, s_sendTtype);

		await client.UnannounceSupportAsync();
		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(client, s_sendTtype);

		await Assert.That(Encoding.ASCII.GetString(Snapshot(sent).Single())).Contains("FIRST");
		await client.DisposeAsync();
	}

	/// <summary>
	/// Compression stopped and started again is a new zlib stream with its own header, ended in the
	/// same orderly way as the first.
	/// </summary>
	[Test]
	public async Task Mccp2RestartsWithAFreshStream()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<MCCPProtocol>());

		await InterpretAndWaitAsync(server, Frame(Trigger.DO, Trigger.MCCP2));
		await server.SendAsync(Encoding.ASCII.GetBytes("one"));
		await InterpretAndWaitAsync(server, Frame(Trigger.DONT, Trigger.MCCP2));

		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(server, Frame(Trigger.DO, Trigger.MCCP2));
		await server.SendAsync(Encoding.ASCII.GetBytes("two"));
		await InterpretAndWaitAsync(server, Frame(Trigger.DONT, Trigger.MCCP2));
		var frames = Snapshot(sent);

		// The marker goes out in the clear, then the second stream.
		await AssertByteArraysEqual(frames[0],
			[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP2, (byte)Trigger.IAC, (byte)Trigger.SE]);
		var second = frames.Skip(1).SelectMany(f => f).ToArray();
		await Assert.That(InflateComplete(second, out var text)).IsTrue();
		await Assert.That(text).IsEqualTo("two\r\n");
		await server.DisposeAsync();
	}

	[Test]
	public async Task StoppingBeforeAnythingIsSentEndsAnEmptyStream()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<MCCPProtocol>());

		await InterpretAndWaitAsync(server, Frame(Trigger.DO, Trigger.MCCP2));
		lock (sent) sent.Clear();
		await InterpretAndWaitAsync(server, Frame(Trigger.DONT, Trigger.MCCP2));

		await Assert.That(InflateComplete(Snapshot(sent).SelectMany(f => f).ToArray(), out var text)).IsTrue();
		await Assert.That(text).IsEqualTo(string.Empty);
		await server.DisposeAsync();
	}

	/// <summary>
	/// Writes racing the stop each land once, either inside the stream before its end or in the clear
	/// after it, never split across the end and never lost.
	/// </summary>
	[Test]
	[Repeat(5)]
	public async Task WritesRacingTheStopAreEachDeliveredOnce()
	{
		const int messages = 200;
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<MCCPProtocol>());

		await InterpretAndWaitAsync(server, Frame(Trigger.DO, Trigger.MCCP2));
		lock (sent) sent.Clear();

		var writers = Enumerable.Range(0, messages)
			.Select(i => Task.Run(async () => await server.SendAsync(Encoding.ASCII.GetBytes($"m{i}"))))
			.ToList();
		await Task.Delay(1);
		await server.InterpretByteArrayAsync(Frame(Trigger.DONT, Trigger.MCCP2));
		await Task.WhenAll(writers);
		await server.WaitForProcessingAsync();

		var frames = Snapshot(sent);
		var cut = -1;
		for (var k = 1; k <= frames.Length; k++)
		{
			if (InflateComplete(frames.Take(k).SelectMany(f => f).ToArray(), out _))
			{
				await Assert.That(cut).IsEqualTo(-1).Because("only one prefix can end the stream");
				cut = k;
			}
		}

		await Assert.That(cut).IsNotEqualTo(-1);
		InflateComplete(frames.Take(cut).SelectMany(f => f).ToArray(), out var compressed);
		var clear = Encoding.ASCII.GetString(frames.Skip(cut).SelectMany(f => f).ToArray());

		var lines = (compressed + clear).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		await Assert.That(lines.Length).IsEqualTo(messages);
		await Assert.That(lines.Distinct().Count()).IsEqualTo(messages);
		await server.DisposeAsync();
	}

	/// <summary>
	/// If the write carrying the stream's end fails (the socket went away, say), the finished encoder
	/// must not stay installed: it cannot encode again, so the next write would throw instead of going
	/// out in the clear.
	/// </summary>
	[Test]
	public async Task AFailedEndingWriteLeavesTheConnectionWritable()
	{
		var sent = new List<byte[]>();
		var failNext = false;
		var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				if (failNext)
				{
					failNext = false;
					throw new IOException("connection reset");
				}

				lock (sent) sent.Add(data.ToArray());
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MCCPProtocol>());

		await InterpretAndWaitAsync(server, Frame(Trigger.DO, Trigger.MCCP2));
		failNext = true;
		await Assert.That(async () => await server.UnannounceSupportAsync()).Throws<IOException>();
		await Assert.That(failNext).IsFalse();
		await Assert.That(server.PluginManager!.GetPlugin<MCCPProtocol>()!.IsMCCP2Enabled).IsFalse();

		lock (sent) sent.Clear();
		await server.SendAsync(Encoding.ASCII.GetBytes("still here"));
		await Assert.That(Encoding.ASCII.GetString(Snapshot(sent).Single())).IsEqualTo("still here\r\n");
		await server.DisposeAsync();
	}

	/// <summary>
	/// An offer whose write failed was never made, so it is not withdrawn later; the ones that went
	/// out before the failure are.
	/// </summary>
	[Test]
	public async Task OnlyOffersThatWentOutAreRecorded()
	{
		var writes = 0;
		var failAt = -1;
		var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ =>
			{
				if (++writes == failAt) throw new IOException("connection reset");
				return ValueTask.CompletedTask;
			})
			.AddPlugin<GMCPProtocol>()
			.AddPlugin<NAWSProtocol>());

		var all = server.InitialOffers;
		await Assert.That(all.Count).IsEqualTo(2);
		await server.UnannounceSupportAsync();

		failAt = writes + 2;
		await Assert.That(async () => await server.AnnounceSupportAsync()).Throws<IOException>();
		await Assert.That(server.InitialOffers).IsEquivalentTo(new[] { all[0] });
		await server.DisposeAsync();
	}

	[Test]
	public async Task UnannounceWithNoOffersWritesNothing()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent));
		lock (sent) sent.Clear();

		await server.UnannounceSupportAsync();

		await Assert.That(server.InitialOffers.Count).IsEqualTo(0);
		await Assert.That(Snapshot(sent).Length).IsEqualTo(0);
		await server.DisposeAsync();
	}

	/// <summary>
	/// Only what this side offered on its own is withdrawn. An answer to the peer (the client's WILL
	/// for the server's DO NEW-ENVIRON) is not an offer, even though it has the same shape.
	/// </summary>
	[Test]
	public async Task ClientWithdrawsItsOffersButNotItsAnswers()
	{
		var sent = new List<byte[]>();
		var client = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Client, sent)
			.AddPlugin<NAWSProtocol>()
			.AddPlugin<NewEnvironProtocol>());

		await InterpretAndWaitAsync(client, Frame(Trigger.DO, Trigger.NEWENVIRON));
		await Assert.That(client.InitialOffers).IsEquivalentTo(new[] { ((byte)Trigger.WILL, (byte)Trigger.NAWS) });

		lock (sent) sent.Clear();
		await client.UnannounceSupportAsync();
		await AssertByteArraysEqual(Snapshot(sent).Single(), Frame(Trigger.WONT, Trigger.NAWS));
		await client.DisposeAsync();
	}

	/// <summary>
	/// A second withdrawal in a row has no compression left to end, so it is only the refusals, in
	/// the clear.
	/// </summary>
	[Test]
	public async Task UnannouncingTwiceSendsOnlyRefusalsTheSecondTime()
	{
		var sent = new List<byte[]>();
		var server = await BuildAndWaitAsync(Builder(TelnetInterpreter.TelnetMode.Server, sent)
			.AddPlugin<MCCPProtocol>()
			.AddPlugin<GMCPProtocol>());
		await InterpretAndWaitAsync(server, Frame(Trigger.DO, Trigger.MCCP2));

		await server.UnannounceSupportAsync();
		lock (sent) sent.Clear();
		await server.UnannounceSupportAsync();

		var frames = Snapshot(sent);
		await Assert.That(frames.Length).IsEqualTo(3);
		await AssertByteArraysEqual(frames[0], Frame(Trigger.WONT, Trigger.MCCP2));
		await AssertByteArraysEqual(frames[1], Frame(Trigger.WONT, Trigger.MCCP3));
		await AssertByteArraysEqual(frames[2], Frame(Trigger.WONT, Trigger.GMCP));
		await server.DisposeAsync();
	}

	/// <summary>
	/// The counterexample <c>MudStandardsProperties.OfferScanFindsExactlyTheOffers</c> found: a
	/// subnegotiation for option 255 whose payload starts with the WILL byte was read as an offer,
	/// and a <c>WONT</c> for option 255 swallowed the <c>IAC</c> of the <c>DO</c> after it.
	/// </summary>
	[Test]
	public async Task OptionByte255IsNotTakenForTheStartOfACommand()
	{
		byte[] write = [0xFF, 0xFA, 0xFF, 0xFB, 0xFB, 0x02, 0xFF, 0xF0, 0xFF, 0xFC, 0xFF, 0xFF, 0xFD, 0x18];
		var found = new List<(byte Verb, byte Option)>();

		TelnetInterpreter.CollectOffers(write, found);

		await Assert.That(found).IsEquivalentTo(new[] { ((byte)Trigger.DO, (byte)Trigger.TTYPE) });
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
