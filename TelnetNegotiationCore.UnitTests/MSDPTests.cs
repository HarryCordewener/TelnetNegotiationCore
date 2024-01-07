﻿using NUnit.Framework;
using Serilog;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests
{
	[TestFixture]
	public class MSDPTests : BaseTest
	{
		static Encoding encoding = Encoding.ASCII;

		[TestCaseSource(nameof(FSharpTestSequences))]
		public void TestFSharp(byte[] testcase, dynamic expectedObject)
		{
			var result = Functional.MSDPLibrary.MSDPScan(testcase, encoding);
			Log.Logger.Information("Serialized: {NewLine} {Serialized}", JsonSerializer.Serialize(result));
			Assert.AreEqual(JsonSerializer.Serialize(expectedObject), JsonSerializer.Serialize(result));
		}

		public static IEnumerable<TestCaseData> FSharpTestSequences
		{
			get
			{
				yield return new TestCaseData((byte[])[
					(byte)Trigger.MSDP_VAR,
					.. encoding.GetBytes("LIST"),
					(byte)Trigger.MSDP_VAL,
					.. encoding.GetBytes("COMMANDS")],
					new { LIST = "COMMANDS" });

				yield return new TestCaseData((byte[])[
					(byte)Trigger.MSDP_VAR,
					.. encoding.GetBytes("COMMANDS"),
					(byte)Trigger.MSDP_VAL,
					(byte)Trigger.MSDP_ARRAY_OPEN,
					(byte)Trigger.MSDP_VAL,
					.. encoding.GetBytes("LIST"),
					(byte)Trigger.MSDP_VAL,
					.. encoding.GetBytes("REPORT"),
					(byte)Trigger.MSDP_VAL,
					.. encoding.GetBytes("SEND"),
					(byte)Trigger.MSDP_ARRAY_CLOSE],
					new { COMMANDS = (string[])["LIST", "REPORT", "SEND"] });

				yield return new TestCaseData((byte[])[
					(byte)Trigger.MSDP_VAR,
					.. encoding.GetBytes("ROOM"),
					(byte)Trigger.MSDP_VAL,
					(byte)Trigger.MSDP_TABLE_OPEN,
					(byte)Trigger.MSDP_VAR,
					.. encoding.GetBytes("VNUM"),
					(byte)Trigger.MSDP_VAL,
					.. encoding.GetBytes("6008"),
					(byte)Trigger.MSDP_VAR,
					.. encoding.GetBytes("NAME"),
					(byte)Trigger.MSDP_VAL,
					.. encoding.GetBytes("The Forest clearing"),
					(byte)Trigger.MSDP_VAR,
					.. encoding.GetBytes("AREA"),
					(byte)Trigger.MSDP_VAL,
					.. encoding.GetBytes("Haon Dor"),
					(byte)Trigger.MSDP_VAR,
					.. encoding.GetBytes("EXITS"),
					(byte)Trigger.MSDP_VAL,
					(byte)Trigger.MSDP_TABLE_OPEN,
					(byte)Trigger.MSDP_VAR,
					.. encoding.GetBytes("n"),
					(byte)Trigger.MSDP_VAL,
					.. encoding.GetBytes("6011"),
					(byte)Trigger.MSDP_VAR,
					.. encoding.GetBytes("e"),
					(byte)Trigger.MSDP_VAL,
					.. encoding.GetBytes("6012"),
					(byte)Trigger.MSDP_TABLE_CLOSE,
					(byte)Trigger.MSDP_TABLE_CLOSE
				],
					new { ROOM = new { AREA = "Haon Dor", EXITS = new { e = "6012", n = "6011" }, NAME = "The Forest clearing", VNUM = "6008" } });
			}
		}
	}
}
