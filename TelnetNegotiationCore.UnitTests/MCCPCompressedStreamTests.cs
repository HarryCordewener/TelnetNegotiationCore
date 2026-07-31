using Microsoft.Extensions.Logging;
using TUnit.Core;
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

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Tests that MCCP actually compresses and decompresses the byte stream, rather than only
/// negotiating about it. Everything here asserts on payload — what reaches OnSubmit, what
/// reaches the negotiation callback — never on the negotiation bytes alone.
/// </summary>
public class MCCPCompressedStreamTests : BaseTest
{
	private static readonly byte[] s_willMccp2 = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2];
	private static readonly byte[] s_startMccp2 = [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP2, (byte)Trigger.IAC, (byte)Trigger.SE];
	private static readonly byte[] s_willMccp3 = [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP3];

	/// <summary>
	/// The server side of an MCCP stream: one zlib stream for the life of the connection, flushed
	/// after every write so the peer can decode what has been sent so far. This is what a MUD does.
	/// </summary>
	private sealed class MccpStreamWriter : IDisposable
	{
		private readonly MemoryStream _sink = new();
		private readonly ZLibStream _deflate;

		public MccpStreamWriter() => _deflate = new ZLibStream(_sink, CompressionLevel.Optimal, leaveOpen: true);

		/// <summary>Compresses one more piece of the stream and returns just the bytes it added.</summary>
		public byte[] Send(params byte[][] parts)
		{
			foreach (var part in parts)
			{
				_deflate.Write(part, 0, part.Length);
			}

			_deflate.Flush();
			var produced = _sink.ToArray();
			_sink.SetLength(0);
			return produced;
		}

		public byte[] Send(string text) => Send(Encoding.ASCII.GetBytes(text));

		public void Dispose()
		{
			_deflate.Dispose();
			_sink.Dispose();
		}
	}

	private static int CountOccurrences(byte[] haystack, byte[] needle)
	{
		var found = 0;
		for (var i = 0; i + needle.Length <= haystack.Length; i++)
		{
			if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
			{
				found++;
			}
		}

		return found;
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

	#region MCCP2: the client inflates what the server sends

	[Test]
	public async Task ClientDeliversInflatedTextAfterMCCP2Marker()
	{
		var submitted = new List<string>();
		var client = await BuildClientAsync(submitted, _ => ValueTask.CompletedTask);

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var server = new MccpStreamWriter();
		var compressed = server.Send("By what name do you wish to be known?\n");

		await InterpretAndWaitAsync(client, Concat(s_startMccp2, compressed));
		await PollUntilAsync(() => submitted.Count > 0);

		await Assert.That(submitted).IsEquivalentTo(new[] { "By what name do you wish to be known?" });

		await client.DisposeAsync();
	}

	[Test]
	public async Task ClientInflatesAPayloadSplitAcrossReads()
	{
		// MCCP is one continuous zlib stream for the life of the connection, not one per message:
		// a fresh inflater per read fails on the second chunk, and a chunk that ends mid-symbol
		// must be carried into the next read.
		var submitted = new List<string>();
		var client = await BuildClientAsync(submitted, _ => ValueTask.CompletedTask);

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var server = new MccpStreamWriter();
		var first = server.Send("the first line\n");
		var second = server.Send("the second line\n");

		await InterpretAndWaitAsync(client, Concat(s_startMccp2, first));
		await PollUntilAsync(() => submitted.Count > 0);
		await Assert.That(submitted).IsEquivalentTo(new[] { "the first line" });

		await InterpretAndWaitAsync(client, second);
		await PollUntilAsync(() => submitted.Count > 1);
		await Assert.That(submitted).IsEquivalentTo(new[] { "the first line", "the second line" });

		await client.DisposeAsync();
	}

	[Test]
	public async Task ClientInflatesAcrossAReadThatEndsMidSymbol()
	{
		// The network splits wherever it likes, including inside a deflate symbol. Feed the same
		// flushed chunk one byte at a time and the result must be identical.
		var submitted = new List<string>();
		var client = await BuildClientAsync(submitted, _ => ValueTask.CompletedTask);

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var server = new MccpStreamWriter();
		var compressed = server.Send("split every single byte\n");

		await InterpretAndWaitAsync(client, s_startMccp2);
		foreach (var b in compressed)
		{
			await client.InterpretByteArrayAsync(new[] { b });
		}

		await client.WaitForProcessingAsync();
		await PollUntilAsync(() => submitted.Count > 0);

		await Assert.That(submitted).IsEquivalentTo(new[] { "split every single byte" });

		await client.DisposeAsync();
	}

	[Test]
	public async Task CompressedBytesThatLookLikeIACAreNotParsedAsTelnet()
	{
		// Deflate output is arbitrary bytes; roughly one in 256 is 0xFF. Before the fix those
		// reached the state machine and drove it into negotiation states, corrupting everything
		// that followed.
		var text = IncompressibleAsciiText(seed: 1950, length: 4000);
		var submitted = new List<string>();
		var client = await BuildClientAsync(submitted, _ => ValueTask.CompletedTask);

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var server = new MccpStreamWriter();
		var compressed = server.Send(text + "\n");

		await Assert.That(compressed).Contains((byte)Trigger.IAC,
			"this test is only meaningful if the compressed form contains an IAC byte");

		await InterpretAndWaitAsync(client, Concat(s_startMccp2, compressed));
		await PollUntilAsync(() => submitted.Count > 0);

		await Assert.That(submitted).IsEquivalentTo(new[] { text });

		await client.DisposeAsync();
	}

	[Test]
	public async Task TelnetNegotiationInsideTheCompressedStreamIsStillInterpreted()
	{
		// MCCP2 compresses the whole stream, negotiation included. Once inflated, the bytes must
		// go through the telnet state machine exactly as if they had arrived in the clear.
		var submitted = new List<string>();
		byte[] negotiated = null;
		var client = await BuildClientAsync(submitted, data =>
		{
			negotiated = data.ToArray();
			return ValueTask.CompletedTask;
		}, withCharset: true);

		await InterpretAndWaitAsync(client, s_willMccp2);
		negotiated = null;

		using var server = new MccpStreamWriter();
		var compressed = server.Send(
			Encoding.ASCII.GetBytes("before negotiation\n"),
			[(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET],
			Encoding.ASCII.GetBytes("after negotiation\n"));

		await InterpretAndWaitAsync(client, Concat(s_startMccp2, compressed));
		await PollUntilAsync(() => submitted.Count > 1 && negotiated != null);

		await Assert.That(submitted).IsEquivalentTo(new[] { "before negotiation", "after negotiation" });
		await AssertByteArraysEqual(negotiated, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET]);

		await client.DisposeAsync();
	}

	[Test]
	public async Task EscapedIACInsideTheCompressedStreamArrivesAsOneDataByte()
	{
		var submitted = new List<byte[]>();
		var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, _, _) =>
			{
				submitted.Add(data);
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<MCCPProtocol>());

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var server = new MccpStreamWriter();
		var compressed = server.Send([(byte)'a', 255, 255, (byte)'b', (byte)'\n']);

		await InterpretAndWaitAsync(client, Concat(s_startMccp2, compressed));
		await PollUntilAsync(() => submitted.Count > 0);

		await Assert.That(submitted.Count).IsEqualTo(1);
		await AssertByteArraysEqual(submitted[0], [(byte)'a', 255, (byte)'b']);

		await client.DisposeAsync();
	}

	[Test]
	public async Task ClientReportsMCCP2EnabledOnlyWhenItCanActuallyInflate()
	{
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled((version, enabled) =>
				{
					compressionEvents.Add((version, enabled));
					return ValueTask.CompletedTask;
				}));

		var plugin = client.PluginManager!.GetPlugin<MCCPProtocol>()!;

		await InterpretAndWaitAsync(client, s_willMccp2);
		await Assert.That(plugin.IsMCCP2Enabled).IsFalse();

		await InterpretAndWaitAsync(client, s_startMccp2);
		await PollUntilAsync(() => compressionEvents.Count > 0);

		await Assert.That(plugin.IsMCCP2Enabled).IsTrue();
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true) });

		await client.DisposeAsync();
	}

	[Test]
	public async Task AStreamThatIsNotValidZlibStopsDecompressionInsteadOfDeliveringGarbage()
	{
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

		await InterpretAndWaitAsync(client, s_willMccp2);
		await InterpretAndWaitAsync(client, s_startMccp2);
		await PollUntilAsync(() => plugin.IsMCCP2Enabled);

		// A zlib header this is not: the peer is broken, or is not compressing at all.
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("this is not compressed at all\n"));
		await PollUntilAsync(() => !plugin.IsMCCP2Enabled);

		await Assert.That(plugin.IsMCCP2Enabled).IsFalse();
		await Assert.That(submitted).IsEmpty();

		// The consumer has to be told, or it goes on believing compression is live while the
		// library has quietly given up on the connection's input.
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true), (2, false) });

		// And it stays stopped rather than throwing on every subsequent byte.
		await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("still not compressed\n"));
		await Assert.That(submitted).IsEmpty();

		await client.DisposeAsync();
	}

	[Test]
	public async Task ASecondCompressionMarkerDoesNotResetTheInflater()
	{
		// A peer chooses when markers arrive. Obeying a second one would throw away the deflate
		// window the rest of its stream is encoded against, and every byte after it would be lost.
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

		await InterpretAndWaitAsync(client, s_willMccp2);

		using var server = new MccpStreamWriter();
		await InterpretAndWaitAsync(client, Concat(s_startMccp2, server.Send("before the repeat\n")));
		await PollUntilAsync(() => submitted.Count > 0);

		// The marker again, mid-stream. It is compressed now, like everything else the server sends.
		await InterpretAndWaitAsync(client, server.Send(s_startMccp2));
		await InterpretAndWaitAsync(client, server.Send("after the repeat\n"));
		await PollUntilAsync(() => submitted.Count > 1);

		await Assert.That(submitted).IsEquivalentTo(new[] { "before the repeat", "after the repeat" });
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true) });

		await client.DisposeAsync();
	}

	[Test]
	public async Task ARepeatedDoMCCP2DoesNotResendTheMarkerOrResetTheDeflater()
	{
		var fromServer = new List<byte>();
		var compressionEvents = new List<(int Version, bool Enabled)>();
		var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				fromServer.AddRange(data.ToArray());
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MCCPProtocol>()
				.OnCompressionEnabled((version, enabled) =>
				{
					compressionEvents.Add((version, enabled));
					return ValueTask.CompletedTask;
				}));

		fromServer.Clear();
		await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2]);
		await PollUntilAsync(() => fromServer.Count >= s_startMccp2.Length);
		await server.WriteToNetworkAsync(Encoding.ASCII.GetBytes("first line\n"));
		await PollUntilAsync(() => fromServer.Count > s_startMccp2.Length);

		// A second DO for an option already in effect must be ignored (RFC 854 loop avoidance),
		// and must certainly not restart the deflater the client is decoding against.
		await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2]);
		await server.WriteToNetworkAsync(Encoding.ASCII.GetBytes("second line\n"));

		var onTheWire = fromServer.ToArray();
		await Assert.That(compressionEvents).IsEquivalentTo(new[] { (2, true) });

		// One marker, at the front, and everything after it is one continuous zlib stream.
		await AssertByteArraysEqual(onTheWire.Take(s_startMccp2.Length).ToArray(), s_startMccp2);
		await Assert.That(CountOccurrences(onTheWire, s_startMccp2)).IsEqualTo(1);

		var submitted = new List<string>();
		var client = await BuildClientAsync(submitted, _ => ValueTask.CompletedTask);
		await InterpretAndWaitAsync(client, s_willMccp2);
		await InterpretAndWaitAsync(client, onTheWire);
		await PollUntilAsync(() => submitted.Count > 1);

		await Assert.That(submitted).IsEquivalentTo(new[] { "first line", "second line" });

		await client.DisposeAsync();
		await server.DisposeAsync();
	}

	[Test]
	public async Task AHighRatioPayloadDoesNotGrowTheDecodersBuffer()
	{
		// The inflater's output buffer is bounded only because the interpreter feeds it one byte
		// per call: DEFLATE expands at most 1032:1 from a single input byte, so the buffer plateaus
		// at 2 KiB however compressible the payload is. Batch the feed and that ceiling becomes
		// 1032 x batch size, chosen by the peer. This test fails if anyone batches it.
		var submitted = new List<byte[]>();
		var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, _, _) =>
			{
				submitted.Add(data);
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<MCCPProtocol>());

		await InterpretAndWaitAsync(client, s_willMccp2);

		const int payloadSize = 256 * 1024;
		var payload = new byte[payloadSize + 1];
		Array.Fill(payload, (byte)'a', 0, payloadSize);
		payload[payloadSize] = (byte)'\n';

		using var server = new MccpStreamWriter();
		var compressed = server.Send(payload);

		// A ratio this high is exactly the shape of a decompression bomb.
		await Assert.That(compressed.Length).IsLessThan(2048);

		await InterpretAndWaitAsync(client, Concat(s_startMccp2, compressed));
		await PollUntilAsync(() => submitted.Count > 0, timeoutMs: 60000);

		await Assert.That(submitted.Count).IsEqualTo(1);
		await Assert.That(submitted[0].Length).IsEqualTo(payloadSize);

		await client.DisposeAsync();
	}

	#endregion

	#region MCCP2: the server deflates what it sends

	[Test]
	public async Task ServerCompressesEverythingItSendsAfterTheMCCP2Marker()
	{
		var fromServer = new List<byte>();
		var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				fromServer.AddRange(data.ToArray());
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MCCPProtocol>());

		fromServer.Clear();
		await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2]);
		await PollUntilAsync(() => fromServer.Count >= s_startMccp2.Length);

		// The marker itself is sent in the clear; everything after it is compressed.
		await AssertByteArraysEqual(fromServer.Take(s_startMccp2.Length).ToArray(), s_startMccp2);
		fromServer.Clear();

		var banner = Encoding.ASCII.GetBytes("welcome to the game\n");
		await server.WriteToNetworkAsync(banner);
		await PollUntilAsync(() => fromServer.Count > 0);

		var onTheWire = fromServer.ToArray();
		// The banner must not appear on the wire in the clear.
		await Assert.That(onTheWire.AsSpan().IndexOf(banner.AsSpan()) < 0).IsTrue();

		// A client with no help from this library must be able to inflate it.
		var submitted = new List<string>();
		var client = await BuildClientAsync(submitted, _ => ValueTask.CompletedTask);
		await InterpretAndWaitAsync(client, s_willMccp2);
		await InterpretAndWaitAsync(client, Concat(s_startMccp2, onTheWire));
		await PollUntilAsync(() => submitted.Count > 0);

		await Assert.That(submitted).IsEquivalentTo(new[] { "welcome to the game" });

		await client.DisposeAsync();
		await server.DisposeAsync();
	}

	#endregion

	#region MCCP3: the client deflates what it sends, the server inflates it

	[Test]
	public async Task ClientCompressedOutputRoundTripsToAServerOverMCCP3()
	{
		var submitted = new List<string>();
		var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				submitted.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<MCCPProtocol>());

		var toServer = new List<byte>();
		var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				toServer.AddRange(data.ToArray());
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MCCPProtocol>());

		toServer.Clear();

		// The server offered MCCP3; the client accepts, says it is about to start, and from the
		// marker onward compresses everything.
		await InterpretAndWaitAsync(client, s_willMccp3);
		await PollUntilAsync(() => toServer.Count >= 8);
		await InterpretAndWaitAsync(server, toServer.ToArray());
		toServer.Clear();

		await client.WriteToNetworkAsync(Encoding.ASCII.GetBytes("who\n"));
		await PollUntilAsync(() => toServer.Count > 0);

		var onTheWire = toServer.ToArray();
		// The command must not appear on the wire in the clear.
		await Assert.That(onTheWire.AsSpan().IndexOf("who\n"u8) < 0).IsTrue();

		await InterpretAndWaitAsync(server, onTheWire);
		await PollUntilAsync(() => submitted.Count > 0);

		await Assert.That(submitted).IsEquivalentTo(new[] { "who" });

		await client.DisposeAsync();
		await server.DisposeAsync();
	}

	#endregion

	private static async Task<TelnetInterpreter> BuildClientAsync(
		List<string> submitted,
		Func<ReadOnlyMemory<byte>, ValueTask> onNegotiation,
		bool withCharset = false)
	{
		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				submitted.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(onNegotiation);

		if (withCharset)
		{
			return await BuildAndWaitAsync(builder
				.AddPlugin<CharsetProtocol>()
					.WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
				.AddPlugin<MCCPProtocol>());
		}

		return await BuildAndWaitAsync(builder.AddPlugin<MCCPProtocol>());
	}

	/// <summary>
	/// Printable ASCII with no structure, so deflate cannot shrink it and its output is dense
	/// enough to contain 0xFF bytes. Seeded, so the byte pattern is the same on every run.
	/// </summary>
	private static string IncompressibleAsciiText(int seed, int length)
	{
		const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
		var random = new Random(seed);
		var chars = new char[length];
		for (var i = 0; i < length; i++)
		{
			chars[i] = alphabet[random.Next(alphabet.Length)];
		}

		return new string(chars);
	}
}
