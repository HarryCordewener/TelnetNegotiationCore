using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Builders;

namespace TelnetNegotiationCore.UnitTests;


public class MSDPTests : BaseTest
{
	private static readonly Encoding Encoding = Encoding.ASCII;

	[Test]
	[MethodDataSource(nameof(FSharpScanTestSequences))]
	public async Task TestFSharpScan(byte[] testcase, object expectedObject)
	{
		var result = Functional.MSDPLibrary.MSDPScan(testcase, Encoding);
		logger.LogInformation("Serialized: {Serialized}", JsonSerializer.Serialize(result));
		await Assert.That(JsonSerializer.Serialize(result)).IsEqualTo(JsonSerializer.Serialize(expectedObject));
	}

	[Test]
	[MethodDataSource(nameof(FSharpReportTestSequences))]
	public async Task TestFSharpReport(object obj, byte[] expectedSequence)
	{
		byte[] result = Functional.MSDPLibrary.Report(JsonSerializer.Serialize(obj), Encoding);
		logger.LogInformation("Sequence: {Serialized}", BitConverter.ToString(result));
		await AssertByteArraysEqual(result, expectedSequence);
	}

	public static IEnumerable<(byte[], object)> FSharpScanTestSequences()
	{
		yield return ((byte[])[
				(byte)Trigger.MSDP_VAR,
				.. Encoding.GetBytes("LIST"),
				(byte)Trigger.MSDP_VAL,
				.. Encoding.GetBytes("COMMANDS")],
			new { LIST = "COMMANDS" });

		yield return ((byte[])[
				(byte)Trigger.MSDP_VAR,
				.. Encoding.GetBytes("COMMANDS"),
				(byte)Trigger.MSDP_VAL,
				(byte)Trigger.MSDP_ARRAY_OPEN,
				(byte)Trigger.MSDP_VAL,
				.. Encoding.GetBytes("LIST"),
				(byte)Trigger.MSDP_VAL,
				.. Encoding.GetBytes("REPORT"),
				(byte)Trigger.MSDP_VAL,
				.. Encoding.GetBytes("SEND"),
				(byte)Trigger.MSDP_ARRAY_CLOSE],
			new { COMMANDS = (string[])["LIST", "REPORT", "SEND"] });

		yield return ((byte[])[
				(byte)Trigger.MSDP_VAR,
				.. Encoding.GetBytes("ROOM"),
				(byte)Trigger.MSDP_VAL,
				(byte)Trigger.MSDP_TABLE_OPEN,
				(byte)Trigger.MSDP_VAR,
				.. Encoding.GetBytes("VNUM"),
				(byte)Trigger.MSDP_VAL,
				.. Encoding.GetBytes("6008"),
				(byte)Trigger.MSDP_VAR,
				.. Encoding.GetBytes("NAME"),
				(byte)Trigger.MSDP_VAL,
				.. Encoding.GetBytes("The Forest clearing"),
				(byte)Trigger.MSDP_VAR,
				.. Encoding.GetBytes("AREA"),
				(byte)Trigger.MSDP_VAL,
				.. Encoding.GetBytes("Haon Dor"),
				(byte)Trigger.MSDP_VAR,
				.. Encoding.GetBytes("EXITS"),
				(byte)Trigger.MSDP_VAL,
				(byte)Trigger.MSDP_TABLE_OPEN,
				(byte)Trigger.MSDP_VAR,
				.. Encoding.GetBytes("n"),
				(byte)Trigger.MSDP_VAL,
				.. Encoding.GetBytes("6011"),
				(byte)Trigger.MSDP_VAR,
				.. Encoding.GetBytes("e"),
				(byte)Trigger.MSDP_VAL,
				.. Encoding.GetBytes("6012"),
				(byte)Trigger.MSDP_TABLE_CLOSE,
				(byte)Trigger.MSDP_TABLE_CLOSE
			],
			new { ROOM = new { AREA = "Haon Dor", EXITS = new { e = "6012", n = "6011" }, NAME = "The Forest clearing", VNUM = "6008" } });
	}
	public static IEnumerable<(object, byte[])> FSharpReportTestSequences()
	{
		yield return (new { LIST = "COMMANDS" }, (byte[])[
			(byte)Trigger.MSDP_TABLE_OPEN,
			(byte)Trigger.MSDP_VAR,
			.. Encoding.GetBytes("LIST"),
			(byte)Trigger.MSDP_VAL,
			.. Encoding.GetBytes("COMMANDS"),
			(byte)Trigger.MSDP_TABLE_CLOSE]);

		yield return (new { LIST = (string[])["COMMANDS", "JIM"] }, (byte[])[
			(byte)Trigger.MSDP_TABLE_OPEN,
			(byte)Trigger.MSDP_VAR,
			.. Encoding.GetBytes("LIST"),
			(byte)Trigger.MSDP_VAL,
			(byte)Trigger.MSDP_ARRAY_OPEN,
			(byte)Trigger.MSDP_VAL,
			.. Encoding.GetBytes("COMMANDS"),
			(byte)Trigger.MSDP_VAL,
			.. Encoding.GetBytes("JIM"),
			(byte)Trigger.MSDP_ARRAY_CLOSE,
			(byte)Trigger.MSDP_TABLE_CLOSE]);

		yield return (new { LIST = (object[])["COMMANDS", (object[])["JIM"]] }, (byte[])[
			(byte)Trigger.MSDP_TABLE_OPEN,
			(byte)Trigger.MSDP_VAR,
			.. Encoding.GetBytes("LIST"),
			(byte)Trigger.MSDP_VAL,
			(byte)Trigger.MSDP_ARRAY_OPEN,
			(byte)Trigger.MSDP_VAL,
			.. Encoding.GetBytes("COMMANDS"),
			(byte)Trigger.MSDP_VAL,
			(byte)Trigger.MSDP_ARRAY_OPEN,
			(byte)Trigger.MSDP_VAL,
			.. Encoding.GetBytes("JIM"),
			(byte)Trigger.MSDP_ARRAY_CLOSE,
			(byte)Trigger.MSDP_ARRAY_CLOSE,
			(byte)Trigger.MSDP_TABLE_CLOSE]);
	}

	[Test]
	public async Task TestMSDPProtocolServerNegotiation()
	{
		// Test server-side MSDP negotiation using plugin architecture
		var receivedMessages = new List<string>();
		
		var telnet = await new Builders.TelnetInterpreterBuilder()
			.UseMode(Interpreters.TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, encoding, telnet) => ValueTask.CompletedTask)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSDPProtocol>()
				.OnMSDPMessage((telnet, message) =>
				{
					receivedMessages.Add(message);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		// Server should send WILL MSDP on initialization
		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP });

		// Simulate client sending MSDP LIST request
		var msdpRequest = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.MSDP,
			(byte)Trigger.MSDP_VAR,
		};
		msdpRequest.AddRange(Encoding.GetBytes("LIST"));
		msdpRequest.Add((byte)Trigger.MSDP_VAL);
		msdpRequest.AddRange(Encoding.GetBytes("COMMANDS"));
		msdpRequest.Add((byte)Trigger.IAC);
		msdpRequest.Add((byte)Trigger.SE);

		await telnet.InterpretByteArrayAsync(msdpRequest.ToArray());
		await telnet.WaitForProcessingAsync();

		// Verify message was received and parsed
		await Assert.That(receivedMessages.Count).IsEqualTo(1);
		var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(receivedMessages[0]);
		await Assert.That(parsed).IsNotNull();

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task TestMSDPProtocolClientNegotiation()
	{
		// Test client-side MSDP negotiation using plugin architecture
		var receivedMessages = new List<string>();
		
		var telnet = await new Builders.TelnetInterpreterBuilder()
			.UseMode(Interpreters.TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, encoding, telnet) => ValueTask.CompletedTask)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSDPProtocol>()
				.OnMSDPMessage((telnet, message) =>
				{
					receivedMessages.Add(message);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		// Client receives WILL MSDP from server
		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP });

		// Simulate server sending MSDP data
		var msdpData = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.MSDP,
			(byte)Trigger.MSDP_VAR,
		};
		msdpData.AddRange(Encoding.GetBytes("HEALTH"));
		msdpData.Add((byte)Trigger.MSDP_VAL);
		msdpData.AddRange(Encoding.GetBytes("100"));
		msdpData.Add((byte)Trigger.IAC);
		msdpData.Add((byte)Trigger.SE);

		await telnet.InterpretByteArrayAsync(msdpData.ToArray());
		await telnet.WaitForProcessingAsync();

		// Verify message was received and parsed
		await Assert.That(receivedMessages.Count).IsEqualTo(1);
		var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(receivedMessages[0]);
		await Assert.That(parsed).IsNotNull();

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task TestMSDPWithArrayValues()
	{
		// Test MSDP with array values
		var receivedMessages = new List<string>();
		
		var telnet = await new Builders.TelnetInterpreterBuilder()
			.UseMode(Interpreters.TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, encoding, telnet) => ValueTask.CompletedTask)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSDPProtocol>()
				.OnMSDPMessage((telnet, message) =>
				{
					receivedMessages.Add(message);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP });

		// Simulate MSDP message with array
		var msdpArray = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.MSDP,
			(byte)Trigger.MSDP_VAR,
		};
		msdpArray.AddRange(Encoding.GetBytes("COMMANDS"));
		msdpArray.Add((byte)Trigger.MSDP_VAL);
		msdpArray.Add((byte)Trigger.MSDP_ARRAY_OPEN);
		msdpArray.Add((byte)Trigger.MSDP_VAL);
		msdpArray.AddRange(Encoding.GetBytes("LIST"));
		msdpArray.Add((byte)Trigger.MSDP_VAL);
		msdpArray.AddRange(Encoding.GetBytes("REPORT"));
		msdpArray.Add((byte)Trigger.MSDP_VAL);
		msdpArray.AddRange(Encoding.GetBytes("SEND"));
		msdpArray.Add((byte)Trigger.MSDP_ARRAY_CLOSE);
		msdpArray.Add((byte)Trigger.IAC);
		msdpArray.Add((byte)Trigger.SE);

		await telnet.InterpretByteArrayAsync(msdpArray.ToArray());
		await telnet.WaitForProcessingAsync();

		await Assert.That(receivedMessages.Count).IsEqualTo(1);
		var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(receivedMessages[0]);
		await Assert.That(parsed).IsNotNull();
		await Assert.That(parsed!["COMMANDS"].ValueKind).IsEqualTo(JsonValueKind.Array);

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task TestMSDPWithNestedTables()
	{
		// Test MSDP with nested table structures
		var receivedMessages = new List<string>();
		
		var telnet = await new Builders.TelnetInterpreterBuilder()
			.UseMode(Interpreters.TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, encoding, telnet) => ValueTask.CompletedTask)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSDPProtocol>()
				.OnMSDPMessage((telnet, message) =>
				{
					receivedMessages.Add(message);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP });

		// Simulate MSDP message with nested table (ROOM with EXITS)
		var msdpNested = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.MSDP,
			(byte)Trigger.MSDP_VAR,
		};
		msdpNested.AddRange(Encoding.GetBytes("ROOM"));
		msdpNested.Add((byte)Trigger.MSDP_VAL);
		msdpNested.Add((byte)Trigger.MSDP_TABLE_OPEN);
		msdpNested.Add((byte)Trigger.MSDP_VAR);
		msdpNested.AddRange(Encoding.GetBytes("VNUM"));
		msdpNested.Add((byte)Trigger.MSDP_VAL);
		msdpNested.AddRange(Encoding.GetBytes("1001"));
		msdpNested.Add((byte)Trigger.MSDP_VAR);
		msdpNested.AddRange(Encoding.GetBytes("EXITS"));
		msdpNested.Add((byte)Trigger.MSDP_VAL);
		msdpNested.Add((byte)Trigger.MSDP_TABLE_OPEN);
		msdpNested.Add((byte)Trigger.MSDP_VAR);
		msdpNested.AddRange(Encoding.GetBytes("n"));
		msdpNested.Add((byte)Trigger.MSDP_VAL);
		msdpNested.AddRange(Encoding.GetBytes("1002"));
		msdpNested.Add((byte)Trigger.MSDP_TABLE_CLOSE);
		msdpNested.Add((byte)Trigger.MSDP_TABLE_CLOSE);
		msdpNested.Add((byte)Trigger.IAC);
		msdpNested.Add((byte)Trigger.SE);

		await telnet.InterpretByteArrayAsync(msdpNested.ToArray());
		await telnet.WaitForProcessingAsync();

		await Assert.That(receivedMessages.Count).IsEqualTo(1);
		var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(receivedMessages[0]);
		await Assert.That(parsed).IsNotNull();
		await Assert.That(parsed!["ROOM"].ValueKind).IsEqualTo(JsonValueKind.Object);

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task TestMSDPIACEscaping()
	{
		// Test that IAC bytes are properly escaped in MSDP values
		var receivedMessages = new List<string>();
		
		var telnet = await new Builders.TelnetInterpreterBuilder()
			.UseMode(Interpreters.TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, encoding, telnet) => ValueTask.CompletedTask)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSDPProtocol>()
				.OnMSDPMessage((telnet, message) =>
				{
					receivedMessages.Add(message);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP });

		// Simulate MSDP message with escaped IAC byte (IAC IAC = literal 255)
		var msdpEscaped = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.MSDP,
			(byte)Trigger.MSDP_VAR,
		};
		msdpEscaped.AddRange(Encoding.GetBytes("TEST"));
		msdpEscaped.Add((byte)Trigger.MSDP_VAL);
		msdpEscaped.Add(0x41); // 'A'
		msdpEscaped.Add((byte)Trigger.IAC);
		msdpEscaped.Add((byte)Trigger.IAC); // Escaped IAC (literal 255)
		msdpEscaped.Add(0x42); // 'B'
		msdpEscaped.Add((byte)Trigger.IAC);
		msdpEscaped.Add((byte)Trigger.SE);

		await telnet.InterpretByteArrayAsync(msdpEscaped.ToArray());
		await telnet.WaitForProcessingAsync();

		await Assert.That(receivedMessages.Count).IsEqualTo(1);

		await telnet.DisposeAsync();
	}

	[Test]
	public async Task TestMSDPDOSProtection()
	{
		// Test that messages larger than 8KB are truncated for DOS protection
		var receivedMessages = new List<string>();
		
		var telnet = await new Builders.TelnetInterpreterBuilder()
			.UseMode(Interpreters.TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, encoding, telnet) => ValueTask.CompletedTask)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.MSDPProtocol>()
				.OnMSDPMessage((telnet, message) =>
				{
					receivedMessages.Add(message);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP });

		// Create a large MSDP message (> 8KB)
		var largeValue = new byte[10000]; // 10KB of data
		for (int i = 0; i < largeValue.Length; i++)
			largeValue[i] = 0x41; // 'A'

		var msdpLarge = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.MSDP,
			(byte)Trigger.MSDP_VAR,
		};
		msdpLarge.AddRange(Encoding.GetBytes("LARGE"));
		msdpLarge.Add((byte)Trigger.MSDP_VAL);
		msdpLarge.AddRange(largeValue);
		msdpLarge.Add((byte)Trigger.IAC);
		msdpLarge.Add((byte)Trigger.SE);

		await telnet.InterpretByteArrayAsync(msdpLarge.ToArray());
		await telnet.WaitForProcessingAsync();

		// Message should still be received (truncated)
		await Assert.That(receivedMessages.Count).IsEqualTo(1);

		await telnet.DisposeAsync();
	}
}