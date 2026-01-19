using Microsoft.Extensions.Logging;
using TUnit.Core;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;

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
		logger.LogInformation("Sequence: {@Serialized}", result);
		await Assert.That(result).IsEqualTo(expectedSequence);
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

		yield return (new { LIST = (dynamic[])["COMMANDS", (dynamic[])["JIM"]] }, (byte[])[
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
}