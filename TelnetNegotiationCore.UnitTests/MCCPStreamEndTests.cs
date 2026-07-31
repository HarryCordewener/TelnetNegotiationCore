using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// What happens when the peer <i>ends</i> its MCCP stream, which the specification allows at any
/// point: "The server may terminate compression at any point by sending an orderly stream end
/// (Z_FINISH). Following this, the connection continues as a normal telnet connection."
/// </summary>
/// <remarks>
/// Nothing here touches the network. <see cref="ReplayedRomSessionReturnsToPlainTelnet"/> replays a
/// wire capture taken from <c>realms.reichel.net:4000</c> (ROM 2.4), checked in as
/// <c>Fixtures/rom-mccp2-stream-end.bin</c> — see issue #66.
/// </remarks>
public class MCCPStreamEndTests : BaseTest
{
	private static readonly byte[] s_willMccp2 = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2];
	private static readonly byte[] s_startMccp2 = [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP2, (byte)Trigger.IAC, (byte)Trigger.SE];
	private static readonly byte[] s_wontMccp2 = [(byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MCCP2];

	/// <summary>
	/// A server's zlib stream: flushed after every write, and finishable, which is the whole point
	/// here. <see cref="Finish"/> produces the Z_FINISH terminator and the Adler-32 trailer.
	/// </summary>
	private sealed class MccpStreamWriter : IDisposable
	{
		private readonly MemoryStream _sink = new();
		private ZLibStream _deflate;

		public MccpStreamWriter() => _deflate = new ZLibStream(_sink, CompressionLevel.Optimal, leaveOpen: true);

		public byte[] Send(params byte[][] parts)
		{
			foreach (var part in parts)
			{
				_deflate.Write(part, 0, part.Length);
			}

			_deflate.Flush();
			return Drain();
		}

		public byte[] Send(string text) => Send(Encoding.ASCII.GetBytes(text));

		/// <summary>Ends the zlib stream, as a server dropping back to plain telnet does.</summary>
		public byte[] Finish()
		{
			_deflate.Dispose();
			_deflate = null;
			return Drain();
		}

		private byte[] Drain()
		{
			var produced = _sink.ToArray();
			_sink.SetLength(0);
			return produced;
		}

		public void Dispose()
		{
			_deflate?.Dispose();
			_sink.Dispose();
		}
	}

	private static byte[] Concat(params byte[][] parts)
	{
		var result = new byte[parts.Sum(p => p.Length)];
		var at = 0;
		foreach (var part in parts)
		{
			part.CopyTo(result, at);
			at += part.Length;
		}

		return result;
	}

	private static byte[] ReadFixture(string fileName)
	{
		var assembly = typeof(MCCPStreamEndTests).Assembly;
		var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
		using var stream = assembly.GetManifestResourceStream(name)!;
		using var buffer = new MemoryStream();
		stream.CopyTo(buffer);
		return buffer.ToArray();
	}

	[Test]
	public async Task ReplayedRomSessionReturnsToPlainTelnet()
	{
		// The capture, byte for byte: option negotiation, IAC SB COMPRESS2 IAC SE, 475 bytes of
		// zlib ending in Z_FINISH plus its Adler-32, and then 18 bytes of *plain* telnet — the
		// server's option cleanup, ending in IAC WONT COMPRESS2.
		//
		// Before the fix the finished inflater swallowed all 18 of them, so the client never
		// learned compression had ended and every byte for the rest of the connection was lost.
		var capture = ReadFixture("rom-mccp2-stream-end.bin");

		var submitted = new List<string>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				submitted.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled((version, enabled) =>
				{
					compressionEvents.Add((version, enabled));
					return ValueTask.CompletedTask;
				}));

		var plugin = client.PluginManager!.GetPlugin<MCCPProtocol>()!;

		await InterpretAndWaitAsync(client, capture);
		await PollUntilAsync(() => compressionEvents.Count > 1);

		// The whole connect screen inflated, right up to the last line before the stream ended.
		await Assert.That(submitted.Any(line => line.Contains("ROM 2.4 copyright"))).IsTrue();

		// And the plain telnet that followed the stream end was interpreted as telnet: the server's
		// IAC WONT COMPRESS2 reached the state machine.
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true), (2, false) });
		await Assert.That(plugin.IsMCCP2Enabled).IsFalse();

		await client.DisposeAsync();
	}

	[Test]
	public async Task DataSentAfterTheStreamEndArrivesInTheClear()
	{
		// "Following this, the connection continues as a normal telnet connection." Everything the
		// server sends after Z_FINISH is uncompressed, and must be delivered.
		var submitted = new List<string>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var client = await BuildClientAsync(submitted, compressionEvents);

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var server = new MccpStreamWriter();
		var compressed = server.Send("while compressed\n");
		var end = server.Finish();

		await InterpretAndWaitAsync(client, Concat(s_startMccp2, compressed, end));
		await PollUntilAsync(() => compressionEvents.Count > 1);

		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("after the stream ended\n"));
		await PollUntilAsync(() => submitted.Count > 1);

		await Assert.That(submitted).IsEquivalentTo(new[] { "while compressed", "after the stream ended" });

		// The consumer is told, because otherwise it goes on believing compression is live.
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true), (2, false) });

		await client.DisposeAsync();
	}

	[Test]
	public async Task PlainTelnetInTheSameReadAsTheStreamEndIsNotLost()
	{
		// The bytes after Z_FINISH usually arrive in the same TCP segment as the end of the zlib
		// stream, so they are already inside the inflater's reach when it finishes. They belong to
		// the plain telnet connection and must come back out.
		var submitted = new List<string>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var client = await BuildClientAsync(submitted, compressionEvents);

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var server = new MccpStreamWriter();
		var compressed = server.Send("last compressed line\n");
		var end = server.Finish();

		await InterpretAndWaitAsync(client, Concat(
			s_startMccp2,
			compressed,
			end,
			Encoding.ASCII.GetBytes("first clear line\n"),
			s_wontMccp2));

		await PollUntilAsync(() => submitted.Count > 1 && compressionEvents.Count > 1);

		await Assert.That(submitted).IsEquivalentTo(new[] { "last compressed line", "first clear line" });
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true), (2, false) });

		await client.DisposeAsync();
	}

	[Test]
	public async Task TheStreamEndIsNotReportedAsACorruptStream()
	{
		// An orderly end is the peer exercising its right to stop compressing. Reporting it the way
		// a corrupt stream is reported would put a scary error in every consumer's log and, worse,
		// would make the transform refuse the plain telnet that legitimately follows.
		var submitted = new List<string>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var client = await BuildClientAsync(submitted, compressionEvents);

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var server = new MccpStreamWriter();
		await InterpretAndWaitAsync(client, Concat(s_startMccp2, server.Send("a line\n"), server.Finish()));
		await PollUntilAsync(() => compressionEvents.Count > 1);

		// A byte-at-a-time feed after the end, which is what a real socket does.
		foreach (var b in Encoding.ASCII.GetBytes("still talking\n"))
		{
			await client.InterpretByteArrayAsync(new[] { b });
		}

		await client.WaitForProcessingAsync();
		await PollUntilAsync(() => submitted.Count > 1);

		await Assert.That(submitted).IsEquivalentTo(new[] { "a line", "still talking" });

		await client.DisposeAsync();
	}

	[Test]
	public async Task AServerMayStartANewStreamAfterEndingTheLastOne()
	{
		// Once the connection is back to plain telnet there is nothing to stop the server offering
		// MCCP2 again, and a fresh marker then starts a genuinely fresh zlib stream. This is the one
		// case where installing a new inflater is right; a second marker *inside* a live stream is
		// still ignored (MCCPCompressedStreamTests.ASecondCompressionMarkerDoesNotResetTheInflater).
		var submitted = new List<string>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var client = await BuildClientAsync(submitted, compressionEvents);

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var first = new MccpStreamWriter();
		await InterpretAndWaitAsync(client, Concat(s_startMccp2, first.Send("first stream\n"), first.Finish()));
		await PollUntilAsync(() => compressionEvents.Count > 1);

		using var second = new MccpStreamWriter();
		await InterpretAndWaitAsync(client, Concat(s_startMccp2, second.Send("second stream\n")));
		await PollUntilAsync(() => submitted.Count > 1);

		await Assert.That(submitted).IsEquivalentTo(new[] { "first stream", "second stream" });
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true), (2, false), (2, true) });

		await client.DisposeAsync();
	}

	[Test]
	public async Task ARefusalInsideALiveStreamDoesNotTakeTheInflaterOut()
	{
		// A peer can say IAC WONT COMPRESS2 while it is still compressing — the refusal is inside
		// its own compressed stream. It means "I am about to stop", not "the bytes behind this are
		// no longer compressed": only Z_FINISH ends a zlib stream.
		//
		// Taking the inflater out here handed the rest of the stream to the telnet state machine as
		// if it were telnet, and left the plugin believing nothing was running — so the peer's next
		// compression marker installed a fresh inflater onto the middle of the old stream, whose
		// next byte is not a zlib header. That is the InvalidDataException in issue #66.
		var submitted = new List<string>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var client = await BuildClientAsync(submitted, compressionEvents);
		var plugin = client.PluginManager!.GetPlugin<MCCPProtocol>()!;

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var server = new MccpStreamWriter();
		await InterpretAndWaitAsync(client, Concat(s_startMccp2, server.Send("before the refusal\n")));
		await PollUntilAsync(() => submitted.Count > 0);

		// The refusal, compressed, like everything else the peer is sending.
		await InterpretAndWaitAsync(client, server.Send(s_wontMccp2));
		await InterpretAndWaitAsync(client, server.Send("after the refusal\n"));
		await PollUntilAsync(() => submitted.Count > 1);

		await Assert.That(submitted).IsEquivalentTo(new[] { "before the refusal", "after the refusal" });
		await Assert.That(plugin.IsMCCP2Enabled).IsTrue();
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true) });

		// And when the peer does end the stream, that is when it is reported.
		await InterpretAndWaitAsync(client, server.Finish());
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("in the clear\n"));
		await PollUntilAsync(() => submitted.Count > 2);

		await Assert.That(submitted.Count).IsEqualTo(3);
		await Assert.That(submitted[2]).IsEqualTo("in the clear");
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true), (2, false) });

		await client.DisposeAsync();
	}

	[Test]
	public async Task ARefusalWithNothingRunningIsNotReportedAsAStateChange()
	{
		// A peer may refuse an option it never used, and may refuse it twice. Reporting that as
		// compression being turned off has consumers tearing down a state they never had — and
		// after a stream end it would report the same thing a second time.
		var submitted = new List<string>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var client = await BuildClientAsync(submitted, compressionEvents);

		await InterpretAndWaitAsync(client, s_wontMccp2);
		await InterpretAndWaitAsync(client, s_wontMccp2);

		await Assert.That(compressionEvents).IsEmpty();

		await client.DisposeAsync();
	}

	private static async Task<TelnetInterpreter> BuildClientAsync(
		List<string> submitted,
		List<(int Version, bool Enabled)> compressionEvents) =>
		await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				submitted.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled((version, enabled) =>
				{
					compressionEvents.Add((version, enabled));
					return ValueTask.CompletedTask;
				}));
}
