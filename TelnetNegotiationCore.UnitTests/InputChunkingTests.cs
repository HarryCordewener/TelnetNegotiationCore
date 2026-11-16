using System;
using System.Collections.Generic;
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
/// Input is queued for the processing loop in chunks of at most <see cref="TelnetInterpreter.MachineBatchSize"/>
/// bytes, and text and subnegotiation payloads are copied a run at a time. Nothing a peer sends may be cut,
/// reordered or merged by where those chunk and run boundaries fall.
/// </summary>
public class InputChunkingTests : BaseTest
{
	private static async Task<(TelnetInterpreter Interpreter, List<string> Lines, List<(string Package, string Info)> Gmcp)> ServerAsync()
	{
		var lines = new List<string>();
		var gmcp = new List<(string Package, string Info)>();

		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				lock (lines) lines.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<GMCPProtocol>()
				.OnGMCPMessage(message =>
				{
					lock (gmcp) gmcp.Add(message);
					return ValueTask.CompletedTask;
				}));

		await InterpretAndWaitAsync(interpreter, [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.GMCP]);
		return (interpreter, lines, gmcp);
	}

	private static byte[] Gmcp(string package, string json) =>
		[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.GMCP, .. Encoding.UTF8.GetBytes($"{package} {json}"), (byte)Trigger.IAC, (byte)Trigger.SE];

	/// <summary>
	/// One read several chunks long, with lines of every length and a GMCP message laid across the first chunk
	/// boundary, comes out exactly as a byte-at-a-time reading would have it.
	/// </summary>
	[Test]
	public async Task AReadSpanningSeveralChunksIsDeliveredWholeAndInOrder()
	{
		var (interpreter, lines, gmcp) = await ServerAsync();

		var expectedLines = new List<string>();
		var wire = new List<byte>();
		var random = new Random(7);
		var gmcpPlaced = false;

		while (wire.Count < TelnetInterpreter.MachineBatchSize * 3 + 123)
		{
			if (!gmcpPlaced && wire.Count > TelnetInterpreter.MachineBatchSize - 20)
			{
				wire.AddRange(Gmcp("Char.Vitals", "{\"hp\":" + new string('9', 60) + "}"));
				gmcpPlaced = true;
				continue;
			}

			var line = new string((char)('a' + expectedLines.Count % 26), random.Next(0, 200));
			expectedLines.Add(line);
			wire.AddRange(Encoding.ASCII.GetBytes(line + "\r\n"));
		}

		await InterpretAndWaitAsync(interpreter, wire.ToArray());
		await PollUntilAsync(() => lines.Count >= expectedLines.Count && gmcp.Count > 0);

		await Assert.That(lines).IsEquivalentTo(expectedLines, CollectionOrdering.Matching);
		await Assert.That(gmcp.Count).IsEqualTo(1);
		await Assert.That(gmcp[0].Package).IsEqualTo("Char.Vitals");
		await Assert.That(gmcp[0].Info).IsEqualTo("{\"hp\":" + new string('9', 60) + "}");

		await interpreter.DisposeAsync();
	}

	/// <summary>
	/// The interpreter keeps its own copy of what it was handed: a read loop reuses its buffer as soon as the
	/// call returns, long before the processing loop has looked at the bytes.
	/// </summary>
	[Test]
	public async Task TheCallersBufferCanBeReusedAsSoonAsTheCallReturns()
	{
		var (interpreter, lines, _) = await ServerAsync();

		var buffer = Encoding.ASCII.GetBytes("first line\r\n");
		await interpreter.InterpretByteArrayAsync(buffer);
		Encoding.ASCII.GetBytes("XXXXXXXXXX\r\n").CopyTo(buffer, 0);

		await interpreter.WaitForProcessingAsync(additionalDelayMs: 0);
		await PollUntilAsync(() => lines.Count > 0);

		await Assert.That(lines).IsEquivalentTo(new[] { "first line" });

		await interpreter.DisposeAsync();
	}

	/// <summary>
	/// Bytes fed one call at a time and bytes fed in one call reach the same lines.
	/// </summary>
	[Test]
	public async Task SingleByteCallsAndOneLargeCallAgree()
	{
		var (interpreter, lines, _) = await ServerAsync();

		foreach (var b in Encoding.ASCII.GetBytes("one\r\ntw"))
		{
			await interpreter.InterpretAsync(b);
		}

		await InterpretAndWaitAsync(interpreter, Encoding.ASCII.GetBytes("o\r\nthree\r\n"));
		await PollUntilAsync(() => lines.Count >= 3);

		await Assert.That(lines).IsEquivalentTo(new[] { "one", "two", "three" }, CollectionOrdering.Matching);

		await interpreter.DisposeAsync();
	}

	/// <summary>
	/// <see cref="SubnegotiationBuffer.AddRange"/> ends in the same state as <see cref="SubnegotiationBuffer.Add"/>
	/// once per byte, below, at and past the ceiling.
	/// </summary>
	[Test]
	[Arguments(5)]
	[Arguments(10)]
	[Arguments(11)]
	[Arguments(40)]
	public async Task AddRangeMatchesAddingEachByte(int length)
	{
		var payload = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
		var byByte = new SubnegotiationBuffer(maxMessageSize: 10);
		var byRange = new SubnegotiationBuffer(maxMessageSize: 10);

		byRange.Add(200);
		byByte.Add(200);
		foreach (var b in payload) byByte.Add(b);
		byRange.AddRange(payload);

		await Assert.That(byRange.Bytes).IsEquivalentTo(byByte.Bytes, CollectionOrdering.Matching);
		await Assert.That(byRange.Overflowed).IsEqualTo(byByte.Overflowed);
		await Assert.That(byRange.ReceivedBytes).IsEqualTo(byByte.ReceivedBytes);
	}
}
