using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpretors;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests
{
	[TestFixture]
	public class TTypeTests
	{
		private TelnetInterpretor _ti;
		private byte[] _negotiationOutput;

		private Task WriteBackToOutput(byte[] arg1, Encoding arg2) => throw new NotImplementedException();

		private Task WriteBackToNegotiate(byte[] arg1)
		{
			_negotiationOutput = arg1;
			return Task.CompletedTask;
		}

		[SetUp]
		public async Task Setup()
		{
			_ti = await new TelnetInterpretor()
			{
				CallbackNegotiation = WriteBackToNegotiate,
				CallbackOnSubmit = WriteBackToOutput,
				CallbackOnByte = (x, y) => Task.CompletedTask,
				Mode = TelnetInterpretor.TelnetMode.Server
			}
				.RegisterMSSPConfig(new MSSPConfig
				{
					Name = () => "My Telnet Negotiated Server",
					UTF_8 = () => true,
					Gameplay = () => new[] { "ABC", "DEF" },
					Extended = new Dictionary<string, Func<dynamic>>
				{
					{ "Foo", () => "Bar"},
					{ "Baz", () => new [] {"Moo", "Meow" }}
				}
				}).Validate().Build();
		}

		[TestCaseSource(nameof(TTypeSequences))]
		public async Task EvaluationCheck(IEnumerable<byte[]> clientSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<string[]> RegisteredTTypes)
		{
			if (clientSends.Count() != serverShouldRespondWith.Count())
				throw new Exception("Invalid Testcase.");

			foreach((var clientSend, var serverShouldRespond, var shouldHaveTTypeList) in clientSends.Zip(serverShouldRespondWith, RegisteredTTypes))
			{
				_negotiationOutput = null;
				foreach (var x in clientSend ?? Enumerable.Empty<byte>())
				{
					await _ti.Interpret(x);
				}
				CollectionAssert.AreEqual(shouldHaveTTypeList ?? Enumerable.Empty<string>(), _ti.TerminalTypes);
				CollectionAssert.AreEqual(serverShouldRespond, _negotiationOutput);
			}
		}

		public static IEnumerable<TestCaseData> TTypeSequences
		{
			get
			{
				yield return new TestCaseData(
					new[] { // Client Sends
						new[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE },
					},
					new[] { // Server Should Respond With
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE },
					},
					new [] // Registered TType List After Negotiation
					{
						new string[] { }
					}).SetName("Basic responds to Client TType Willing");
				yield return new TestCaseData(
					new[] { // Client Sends
						new[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE },
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'A', (byte)'N', (byte)'S', (byte)'I', (byte)Trigger.IAC, (byte)Trigger.SE },
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'V', (byte)'T', (byte)'1', (byte)'0', (byte)'0', (byte)Trigger.IAC, (byte)Trigger.SE },
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'V', (byte)'T', (byte)'1', (byte)'0', (byte)'0', (byte)Trigger.IAC, (byte)Trigger.SE }
					},
					new[] { // Server Should Respond With
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE },
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE },
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE },
						null
					},
					new[] // Registered TType List After Negotiation
					{
						new string[] { },
						new string[] { "ANSI"},
						new string[] { "ANSI", "VT100"},
						new string[] { "ANSI", "VT100"}
					}).SetName("Long response to Client TType Willing");
			}
		}
	}
}