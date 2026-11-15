using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Helpers;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Assertions.Enums;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The send paths build each frame in one buffer, escaping straight into it, and
/// <see cref="TelnetInterpreter.SendLinesAsync"/> sends several lines as one write. The bytes on the wire
/// are the ones the older two-step paths produced.
/// </summary>
public class OutboundWriteTests : BaseTest
{
	private static async Task<(TelnetInterpreter Interpreter, List<byte[]> Writes)> ServerAsync(
		Func<TelnetInterpreterBuilder, TelnetInterpreterBuilder> configure = null)
	{
		var writes = new List<byte[]>();
		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				lock (writes) writes.Add(data.ToArray());
				return ValueTask.CompletedTask;
			});

		var interpreter = await BuildAndWaitAsync(configure is null ? builder : configure(builder));
		lock (writes) writes.Clear();
		return (interpreter, writes);
	}

	[Test]
	[Arguments(new byte[0])]
	[Arguments(new byte[] { 255 })]
	[Arguments(new byte[] { 1, 255, 2, 255, 255, 3 })]
	[Arguments(new byte[] { 255, 255 })]
	public async Task EscapingIntoABufferMatchesTheEscapedCopy(byte[] input)
	{
		var expected = SubnegotiationEscaping.Escaped(input);
		var length = SubnegotiationEscaping.EscapedLength(input);
		var output = new byte[length];
		var written = SubnegotiationEscaping.CopyEscaped(input, output);

		await Assert.That(length).IsEqualTo(expected.Length);
		await Assert.That(written).IsEqualTo(expected.Length);
		await Assert.That(output).IsEquivalentTo(expected, CollectionOrdering.Matching);
	}

	[Test]
	public async Task SendAsyncEscapesAndEndsTheLine()
	{
		var (interpreter, writes) = await ServerAsync();

		await interpreter.SendAsync([(byte)'a', 255, (byte)'b']);

		await Assert.That(writes.Count).IsEqualTo(1);
		await Assert.That(writes[0]).IsEquivalentTo(new byte[] { (byte)'a', 255, 255, (byte)'b', 13, 10 }, CollectionOrdering.Matching);

		await interpreter.DisposeAsync();
	}

	[Test]
	public async Task SendLinesAsyncIsOneWriteOfWhatSendAsyncWouldSend()
	{
		var (interpreter, writes) = await ServerAsync();
		var lines = new[]
		{
			Encoding.ASCII.GetBytes("You are in a small room."),
			Array.Empty<byte>(),
			new byte[] { (byte)'x', 255, (byte)'y' },
		};

		foreach (var line in lines)
		{
			await interpreter.SendAsync(line);
		}

		var separately = writes.SelectMany(w => w).ToArray();
		writes.Clear();

		await interpreter.SendLinesAsync(lines);

		await Assert.That(writes.Count).IsEqualTo(1);
		await Assert.That(writes[0]).IsEquivalentTo(separately, CollectionOrdering.Matching);

		await interpreter.DisposeAsync();
	}

	[Test]
	public async Task SendLinesAsyncWithNoLinesWritesNothing()
	{
		var (interpreter, writes) = await ServerAsync();

		await interpreter.SendLinesAsync([]);

		await Assert.That(writes).IsEmpty();

		await interpreter.DisposeAsync();
	}

	[Test]
	[Arguments("Char.Vitals", "{\"hp\":1000,\"name\":\"Ünïcødé ✓\"}")]
	[Arguments("Core.Ping", "")]
	[Arguments("Room.Info", "{\"exits\":{\"n\":1}}")]
	public async Task TheStringFormOfSendGMCPCommandMatchesTheByteForm(string package, string json)
	{
		var (interpreter, writes) = await ServerAsync();

		await interpreter.SendGMCPCommand(package, json);
		await interpreter.SendGMCPCommand(Encoding.UTF8.GetBytes(package), Encoding.UTF8.GetBytes(json));

		await Assert.That(writes.Count).IsEqualTo(2);
		await Assert.That(writes[0]).IsEquivalentTo(writes[1], CollectionOrdering.Matching);

		await interpreter.DisposeAsync();
	}

	[Test]
	public async Task TheByteFormOfSendGMCPCommandStillEscapesIac()
	{
		var (interpreter, writes) = await ServerAsync();

		await interpreter.SendGMCPCommand([(byte)'P'], [1, 255, 2]);

		await Assert.That(writes[0]).IsEquivalentTo(
			new byte[] { 255, 250, 201, (byte)'P', (byte)' ', 1, 255, 255, 2, 255, 240 },
			CollectionOrdering.Matching);

		await interpreter.DisposeAsync();
	}

	[Test]
	[Arguments(CompressionLevel.Fastest)]
	[Arguments(CompressionLevel.Optimal)]
	[Arguments(CompressionLevel.NoCompression)]
	public async Task EveryCompressionLevelIsAStreamThePeerCanRead(CompressionLevel level)
	{
		var (interpreter, writes) = await ServerAsync(b => b.AddPlugin<MCCPProtocol>().WithCompressionLevel(level));

		// The client agrees to MCCP2; the server starts compressing everything after the marker.
		await InterpretAndWaitAsync(interpreter, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2]);
		await PollUntilAsync(() => interpreter.PluginManager!.GetPlugin<MCCPProtocol>()!.IsMCCP2Enabled);

		var marker = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP2, (byte)Trigger.IAC, (byte)Trigger.SE };
		var wire = writes.SelectMany(w => w).ToArray();
		var start = IndexOf(wire, marker) + marker.Length;
		writes.Clear();

		await interpreter.SendLinesAsync([Encoding.ASCII.GetBytes("first"), Encoding.ASCII.GetBytes("second")]);
		var compressed = wire[start..].Concat(writes.SelectMany(w => w)).ToArray();

		using var inflate = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
		var buffer = new byte[64];
		var read = 0;
		int n;
		while (read < 15 && (n = inflate.Read(buffer, read, buffer.Length - read)) > 0) read += n;

		await Assert.That(Encoding.ASCII.GetString(buffer, 0, read)).IsEqualTo("first\r\nsecond\r\n");

		await interpreter.DisposeAsync();
	}

	private static int IndexOf(byte[] haystack, byte[] needle)
	{
		for (var i = 0; i + needle.Length <= haystack.Length; i++)
		{
			if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
		}

		return -1;
	}
}
