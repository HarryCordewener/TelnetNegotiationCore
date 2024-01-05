using NUnit.Framework;
using Serilog;
using System.Collections.Generic;
using System.Text;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests
{
	[TestFixture]
	public class MSDPTests : BaseTest
	{
		static Encoding encoding = Encoding.ASCII;

		[TestCaseSource(nameof(FSharpTestSequences))]
		public void TestFSharp(byte[] testcase)
		{
			var result = Functional.MSDPLibrary.MSDPScan(testcase, encoding);
			Log.Logger.Information("{$Result}", result);
			Assert.True(true);
		}

		public static IEnumerable<TestCaseData> FSharpTestSequences
		{
			get
			{
				yield return new TestCaseData((byte[])[
					(byte)Trigger.MSDP_VAR,
					.. encoding.GetBytes("LIST"),
					(byte)Trigger.MSDP_VAL,
					.. encoding.GetBytes("COMMANDS")]);

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
					(byte)Trigger.MSDP_ARRAY_CLOSE]);

				///MSDP_VAR "EXITS" MSDP_VAL MSDP_TABLE_OPEN MSDP_VAR "n" MSDP_VAL "6011" MSDP_VAR "e" MSDP_VAL "6007" MSDP_TABLE_CLOSE MSDP_TABLE_CLOSE IAC SE

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
				]);
			}
		}
	}
}
