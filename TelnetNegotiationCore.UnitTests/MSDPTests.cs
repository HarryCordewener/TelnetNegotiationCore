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

	/// <summary>
	/// Polls for a condition with timeout, useful for async callback assertions
	/// </summary>
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

	/// <summary>
	/// The client half of MSDP's request vocabulary: <c>SendMSDPCommand</c> is what a client calls to
	/// ask a server to <c>SEND</c>, <c>REPORT</c> or <c>LIST</c> a variable - the half
	/// <see cref="Handlers.MSDPServerHandler"/> answers but nothing built the asking side of, until now.
	/// </summary>
	[Test]
	public async Task ClientCanSendMSDPCommand()
	{
		byte[] negotiationOutput = null;

		ValueTask WriteBackToNegotiate(ReadOnlyMemory<byte> arg1)
		{
			negotiationOutput = arg1.ToArray();
			return ValueTask.CompletedTask;
		}

		var client_ti = await new Builders.TelnetInterpreterBuilder()
			.UseMode(Interpreters.TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, encoding, telnet) => ValueTask.CompletedTask)
			.OnNegotiation(WriteBackToNegotiate)
			.AddPlugin<Protocols.MSDPProtocol>()
			.BuildAsync();

		await client_ti.SendMSDPCommand("SEND", "PLAYERS");

		await Assert.That(negotiationOutput).IsNotNull();
		await AssertByteArraysEqual(negotiationOutput, MSDPFrame("SEND", "PLAYERS"));

		await client_ti.DisposeAsync();
	}

	/// <summary>
	/// The request round-trips through a real server-mode interpreter exactly as
	/// <see cref="Handlers.MSDPServerHandler.HandleAsync"/> expects it: a <c>SEND</c> command naming
	/// the variable the client wants, delivered to <c>OnMSDPMessage</c> like any other MSDP payload.
	/// </summary>
	[Test]
	public async Task SentMSDPCommandArrivesAsAnOrdinaryMSDPMessage()
	{
		var receivedMessages = new List<string>();

		var server_ti = await new Builders.TelnetInterpreterBuilder()
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

		await server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP });

		await server_ti.InterpretByteArrayAsync(MSDPFrame("SEND", "PLAYERS"));
		await server_ti.WaitForProcessingAsync();

		var gotMessage = await PollUntilAsync(() => receivedMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for the MSDP message callback. Count: {receivedMessages.Count}");
		}

		await Assert.That(receivedMessages.Count).IsEqualTo(1);
		var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(receivedMessages[0]);
		await Assert.That(parsed!["SEND"]).IsEqualTo("PLAYERS");

		await server_ti.DisposeAsync();
	}

	/// <summary>
	/// A literal <c>IAC</c> (0xFF) byte in a value must be doubled before it goes on the wire, or the
	/// peer's state machine reads it as the start of a command and the frame desyncs. Reuses
	/// <c>TelnetSafeBytes</c> - the same escaping <c>SendAsync</c>/<c>SendPromptAsync</c> already
	/// apply - rather than a second IAC-doubling implementation.
	/// </summary>
	[Test]
	public async Task SendMSDPCommandEscapesALiteralIACByte()
	{
		byte[] negotiationOutput = null;

		var client_ti = await new Builders.TelnetInterpreterBuilder()
			.UseMode(Interpreters.TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, encoding, telnet) => ValueTask.CompletedTask)
			.OnNegotiation((data) =>
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			})
			.AddPlugin<Protocols.MSDPProtocol>()
			.BuildAsync();

		var valueWithIac = new byte[] { 0x41, (byte)Trigger.IAC, 0x42 }; // 'A' IAC 'B'
		await client_ti.SendMSDPCommand(Encoding.GetBytes("SEND"), valueWithIac);

		await Assert.That(negotiationOutput).IsNotNull();

		var expected = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.MSDP,
			(byte)Trigger.MSDP_VAR,
		};
		expected.AddRange(Encoding.GetBytes("SEND"));
		expected.Add((byte)Trigger.MSDP_VAL);
		expected.Add(0x41);
		expected.Add((byte)Trigger.IAC);
		expected.Add((byte)Trigger.IAC); // the literal IAC, doubled
		expected.Add(0x42);
		expected.Add((byte)Trigger.IAC);
		expected.Add((byte)Trigger.SE);

		await AssertByteArraysEqual(negotiationOutput, expected.ToArray());

		await client_ti.DisposeAsync();
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
		
		// Poll until callback fires
		var gotMessage = await PollUntilAsync(() => receivedMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for MSDP message callback. Count: {receivedMessages.Count}");
		}

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
		
		// Poll until callback fires
		var gotMessage = await PollUntilAsync(() => receivedMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for MSDP message callback. Count: {receivedMessages.Count}");
		}

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
		
		// Poll until callback fires
		var gotMessage = await PollUntilAsync(() => receivedMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for MSDP message callback. Count: {receivedMessages.Count}");
		}

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
		
		// Poll until callback fires
		var gotMessage = await PollUntilAsync(() => receivedMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for MSDP message callback. Count: {receivedMessages.Count}");
		}

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
		
		// Poll until callback fires
		var gotMessage = await PollUntilAsync(() => receivedMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for MSDP message callback. Count: {receivedMessages.Count}");
		}

		await Assert.That(receivedMessages.Count).IsEqualTo(1);

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// Builds an MSDP subnegotiation carrying a single variable: IAC SB MSDP VAR name VAL value IAC SE.
	/// </summary>
	private static byte[] MSDPFrame(string variable, string value)
	{
		var frame = new List<byte>
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.MSDP,
			(byte)Trigger.MSDP_VAR,
		};
		frame.AddRange(Encoding.GetBytes(variable));
		frame.Add((byte)Trigger.MSDP_VAL);
		frame.AddRange(Encoding.GetBytes(value));
		frame.Add((byte)Trigger.IAC);
		frame.Add((byte)Trigger.SE);
		return frame.ToArray();
	}

	/// <summary>
	/// MSDP shared GMCP's 8KB ceiling and its silent truncation. A 10KB value is not exotic - a
	/// REPORT of a room's contents easily reaches it - and half a value parses into different data
	/// rather than less of it.
	/// </summary>
	[Test]
	public async Task TestMSDPLargeMessageArrivesWhole()
	{
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

		var largeValue = new string('A', 10000);

		await telnet.InterpretByteArrayAsync(MSDPFrame("LARGE", largeValue));
		await telnet.WaitForProcessingAsync(maxWaitMs: 30000);

		var gotMessage = await PollUntilAsync(() => receivedMessages.Count > 0, timeoutMs: 30000);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for MSDP message callback. Count: {receivedMessages.Count}");
		}

		await Assert.That(receivedMessages.Count).IsEqualTo(1);
		var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(receivedMessages[0]);
		await Assert.That(parsed!["LARGE"].Length).IsEqualTo(largeValue.Length);
		await Assert.That(parsed!["LARGE"]).IsEqualTo(largeValue);

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// The bound that remains: past the configured ceiling the message is dropped and reported,
	/// rather than parsed from a fragment.
	/// </summary>
	[Test]
	public async Task TestMSDPMessageBeyondTheConfiguredCeilingIsDroppedAndReported()
	{
		var receivedMessages = new List<string>();
		(long ReceivedBytes, int MaxMessageSize)? tooLarge = null;

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
				.WithMaxMessageSize(4096)
				.OnMSDPMessageTooLarge(overflow =>
				{
					tooLarge = overflow;
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSDP });

		await telnet.InterpretByteArrayAsync(MSDPFrame("LARGE", new string('A', 10000)));
		await telnet.WaitForProcessingAsync(maxWaitMs: 30000);

		var reported = await PollUntilAsync(() => tooLarge != null, timeoutMs: 30000);
		if (!reported)
		{
			throw new Exception("Timeout waiting for the oversized-message callback.");
		}

		await Assert.That(receivedMessages.Count).IsEqualTo(0);
		await Assert.That(tooLarge!.Value.MaxMessageSize).IsEqualTo(4096);
		// MSDP_VAR + "LARGE" + MSDP_VAL + 10000 == 10007 bytes.
		await Assert.That(tooLarge!.Value.ReceivedBytes).IsEqualTo(10007);

		// The connection keeps working.
		await telnet.InterpretByteArrayAsync(MSDPFrame("HEALTH", "100"));
		await telnet.WaitForProcessingAsync(maxWaitMs: 30000);

		var gotNext = await PollUntilAsync(() => receivedMessages.Count > 0, timeoutMs: 30000);
		if (!gotNext)
		{
			throw new Exception("Timeout waiting for the MSDP message following an oversized one.");
		}

		var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(receivedMessages[0]);
		await Assert.That(parsed!["HEALTH"]).IsEqualTo("100");

		await telnet.DisposeAsync();
	}

	#region MSDP over GMCP (MoG)

	/// <summary>
	/// Wraps a GMCP subnegotiation around a payload: IAC SB GMCP payload IAC SE.
	/// </summary>
	private static byte[] GMCPFrame(string payload)
	{
		var frame = new List<byte>(payload.Length + 5)
		{
			(byte)Trigger.IAC,
			(byte)Trigger.SB,
			(byte)Trigger.GMCP
		};
		frame.AddRange(Encoding.GetBytes(payload));
		frame.Add((byte)Trigger.IAC);
		frame.Add((byte)Trigger.SE);
		return frame.ToArray();
	}

	/// <summary>
	/// Builds a client-mode interpreter with GMCP negotiated, and with the MSDP plugin registered
	/// unless <paramref name="withMSDPPlugin"/> says otherwise.
	/// </summary>
	private static async Task<Interpreters.TelnetInterpreter> BuildMoGClientAsync(
		List<string> msdpMessages,
		List<(string Package, string Info)> gmcpMessages,
		bool withMSDPPlugin = true)
	{
		var builder = new Builders.TelnetInterpreterBuilder()
			.UseMode(Interpreters.TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, encoding, telnet) => ValueTask.CompletedTask)
			.OnNegotiation((data) => ValueTask.CompletedTask)
			.AddPlugin<Protocols.GMCPProtocol>()
				.OnGMCPMessage(message =>
				{
					gmcpMessages.Add(message);
					return ValueTask.CompletedTask;
				});

		var telnet = withMSDPPlugin
			? await builder
				.AddPlugin<Protocols.MSDPProtocol>()
					.OnMSDPMessage((t, message) =>
					{
						msdpMessages.Add(message);
						return ValueTask.CompletedTask;
					})
				.BuildAsync()
			: await builder.BuildAsync();

		await telnet.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.GMCP });
		await telnet.WaitForProcessingAsync();
		msdpMessages.Clear();
		gmcpMessages.Clear();

		return telnet;
	}

	/// <summary>
	/// The GMCP specification's own MoG handshake:
	/// <c>client - IAC SB GMCP 'MSDP {"LIST" : "COMMANDS"}' IAC SE</c>. The data field "must use the
	/// JSON data syntax", so the body is what the MSDP callback is owed - not the package name.
	/// </summary>
	[Test]
	public async Task MSDPOverGMCPDeliversTheJsonBody()
	{
		var msdpMessages = new List<string>();
		var gmcpMessages = new List<(string Package, string Info)>();
		var telnet = await BuildMoGClientAsync(msdpMessages, gmcpMessages);

		await telnet.InterpretByteArrayAsync(GMCPFrame("MSDP {\"LIST\" : \"COMMANDS\"}"));
		await telnet.WaitForProcessingAsync();

		var gotMessage = await PollUntilAsync(() => msdpMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for the MSDP callback. Count: {msdpMessages.Count}");
		}

		await Assert.That(msdpMessages.Count).IsEqualTo(1);
		var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(msdpMessages[0]);
		await Assert.That(parsed!["LIST"]).IsEqualTo("COMMANDS");

		// A MoG message belongs to MSDP, not to the general GMCP callback.
		await Assert.That(gmcpMessages.Count).IsEqualTo(0);

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// The server half of the same handshake, which carries an MSDP array - "MSDP_ARRAY_OPEN and
	/// MSDP_ARRAY_CLOSE" over the wire, a JSON array over GMCP.
	/// </summary>
	[Test]
	public async Task MSDPOverGMCPDeliversAJsonArray()
	{
		var msdpMessages = new List<string>();
		var gmcpMessages = new List<(string Package, string Info)>();
		var telnet = await BuildMoGClientAsync(msdpMessages, gmcpMessages);

		await telnet.InterpretByteArrayAsync(
			GMCPFrame("MSDP {\"COMMANDS\" : [\"LIST\", \"REPORT\", \"RESET\", \"SEND\", \"UNREPORT\"]}"));
		await telnet.WaitForProcessingAsync();

		var gotMessage = await PollUntilAsync(() => msdpMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for the MSDP callback. Count: {msdpMessages.Count}");
		}

		var parsed = JsonSerializer.Deserialize<Dictionary<string, string[]>>(msdpMessages[0]);
		await Assert.That(parsed!["COMMANDS"].Length).IsEqualTo(5);
		await Assert.That(parsed!["COMMANDS"][0]).IsEqualTo("LIST");
		await Assert.That(parsed!["COMMANDS"][4]).IsEqualTo("UNREPORT");

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// Every JSON shape the data field can take arrives verbatim: a scalar, an object (an MSDP
	/// table - "Tables are called objects in the JSON standard"), and an array.
	/// </summary>
	[Test]
	[Arguments("\"PONG\"")]
	[Arguments("100")]
	[Arguments("{\"HEALTH\" : 100, \"MAX_HEALTH\" : 150}")]
	[Arguments("{\"ROOM\" : {\"VNUM\" : 6008, \"NAME\" : \"The Forest clearing\"}}")]
	[Arguments("[\"LIST\", \"REPORT\"]")]
	public async Task MSDPOverGMCPDeliversEveryJsonShapeVerbatim(string body)
	{
		var msdpMessages = new List<string>();
		var gmcpMessages = new List<(string Package, string Info)>();
		var telnet = await BuildMoGClientAsync(msdpMessages, gmcpMessages);

		await telnet.InterpretByteArrayAsync(GMCPFrame("MSDP " + body));
		await telnet.WaitForProcessingAsync();

		var gotMessage = await PollUntilAsync(() => msdpMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for the MSDP callback for body {body}.");
		}

		await Assert.That(msdpMessages[0]).IsEqualTo(body);

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// A malformed data section is not JSON, so it is not the contract the MSDP callback is
	/// promised. It must be discarded rather than forwarded, and it must not throw onto the read
	/// loop or wedge the connection: the next message still arrives.
	/// </summary>
	[Test]
	public async Task MSDPOverGMCPWithMalformedJsonIsDroppedAndTheConnectionSurvives()
	{
		var msdpMessages = new List<string>();
		var gmcpMessages = new List<(string Package, string Info)>();
		var telnet = await BuildMoGClientAsync(msdpMessages, gmcpMessages);

		await telnet.InterpretByteArrayAsync(GMCPFrame("MSDP {\"LIST\" : "));
		await telnet.WaitForProcessingAsync();

		await Assert.That(msdpMessages.Count).IsEqualTo(0);
		await Assert.That(gmcpMessages.Count).IsEqualTo(0);

		// The connection keeps working.
		await telnet.InterpretByteArrayAsync(GMCPFrame("MSDP {\"LIST\" : \"COMMANDS\"}"));
		await telnet.WaitForProcessingAsync();

		var gotMessage = await PollUntilAsync(() => msdpMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception("Timeout waiting for the MSDP message following a malformed one.");
		}

		var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(msdpMessages[0]);
		await Assert.That(parsed!["LIST"]).IsEqualTo("COMMANDS");

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// With no MSDP plugin registered there is nobody to route MoG to - but it is still a GMCP
	/// message, and a consumer that asked for GMCP messages should get it rather than lose it.
	/// </summary>
	[Test]
	public async Task MSDPOverGMCPWithoutTheMSDPPluginIsDeliveredAsAGMCPMessage()
	{
		var msdpMessages = new List<string>();
		var gmcpMessages = new List<(string Package, string Info)>();
		var telnet = await BuildMoGClientAsync(msdpMessages, gmcpMessages, withMSDPPlugin: false);

		await telnet.InterpretByteArrayAsync(GMCPFrame("MSDP {\"LIST\" : \"COMMANDS\"}"));
		await telnet.WaitForProcessingAsync();

		var gotMessage = await PollUntilAsync(() => gmcpMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for the GMCP callback. Count: {gmcpMessages.Count}");
		}

		await Assert.That(gmcpMessages[0].Package).IsEqualTo("MSDP");
		await Assert.That(gmcpMessages[0].Info).IsEqualTo("{\"LIST\" : \"COMMANDS\"}");

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// "When using MoG (MSDP over GMCP) the package name is considered case sensitive and MSDP must
	/// be fully capitalized." A lowercase msdp package is an ordinary GMCP package.
	/// </summary>
	[Test]
	public async Task LowercaseMsdpPackageIsNotMSDPOverGMCP()
	{
		var msdpMessages = new List<string>();
		var gmcpMessages = new List<(string Package, string Info)>();
		var telnet = await BuildMoGClientAsync(msdpMessages, gmcpMessages);

		await telnet.InterpretByteArrayAsync(GMCPFrame("msdp {\"LIST\" : \"COMMANDS\"}"));
		await telnet.WaitForProcessingAsync();

		var gotMessage = await PollUntilAsync(() => gmcpMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for the GMCP callback. Count: {gmcpMessages.Count}");
		}

		await Assert.That(gmcpMessages[0].Package).IsEqualTo("msdp");
		await Assert.That(msdpMessages.Count).IsEqualTo(0);

		await telnet.DisposeAsync();
	}

	/// <summary>
	/// Only the exact package <c>MSDP</c> is MoG. An ordinary GMCP package must never reach the MSDP
	/// callback, and neither must a package that merely contains or extends the name - the
	/// specification names one package, not a family.
	/// </summary>
	[Test]
	[Arguments("Char.Vitals {\"hp\" : 100}")]
	[Arguments("Core.Ping")]
	[Arguments("Room.Info {\"num\" : 6008}")]
	[Arguments("MSDPX {\"LIST\" : \"COMMANDS\"}")]
	[Arguments("MSDP.Sub {\"LIST\" : \"COMMANDS\"}")]
	[Arguments("Char.MSDP {\"LIST\" : \"COMMANDS\"}")]
	public async Task AnOrdinaryGMCPPackageNeverReachesTheMSDPCallback(string payload)
	{
		var msdpMessages = new List<string>();
		var gmcpMessages = new List<(string Package, string Info)>();
		var telnet = await BuildMoGClientAsync(msdpMessages, gmcpMessages);

		await telnet.InterpretByteArrayAsync(GMCPFrame(payload));
		await telnet.WaitForProcessingAsync();

		var gotMessage = await PollUntilAsync(() => gmcpMessages.Count > 0);
		if (!gotMessage)
		{
			throw new Exception($"Timeout waiting for the GMCP callback for {payload}.");
		}

		await Assert.That(msdpMessages.Count).IsEqualTo(0);

		await telnet.DisposeAsync();
	}

	#endregion
}