using NUnit.Framework;
using Serilog;
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
		private TelnetInterpretor _server_ti;
		private TelnetInterpretor _client_ti;
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
			var log = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.Console()
				.MinimumLevel.Verbose()
				.CreateLogger();

			Log.Logger = log;

			_server_ti = await new TelnetInterpretor(TelnetInterpretor.TelnetMode.Server)
			{
				CallbackNegotiation = WriteBackToNegotiate,
				CallbackOnSubmit = WriteBackToOutput,
				CallbackOnByte = (x, y) => Task.CompletedTask,
			}.RegisterMSSPConfig(new MSSPConfig
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

			_client_ti = await new TelnetInterpretor(TelnetInterpretor.TelnetMode.Client)
			{
				CallbackNegotiation = WriteBackToNegotiate,
				CallbackOnSubmit = WriteBackToOutput,
				CallbackOnByte = (x, y) => Task.CompletedTask,
			}.RegisterMSSPConfig(new MSSPConfig
			{
				Name = () => "My Telnet Negotiated Client",
				UTF_8 = () => true,
				Gameplay = () => new[] { "ABC", "DEF" },
				Extended = new Dictionary<string, Func<dynamic>>
				{
					{ "Foo", () => "Bar"},
					{ "Baz", () => new [] {"Moo", "Meow" }}
				}
			}).Validate().Build();
		}

		[TestCaseSource(nameof(ServerTTypeSequences))]
		public async Task ServerEvaluationCheck(IEnumerable<byte[]> clientSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<string[]> RegisteredTTypes)
		{
			if (clientSends.Count() != serverShouldRespondWith.Count())
				throw new Exception("Invalid Testcase.");

			foreach ((var clientSend, var serverShouldRespond, var shouldHaveTTypeList) in clientSends.Zip(serverShouldRespondWith, RegisteredTTypes))
			{
				_negotiationOutput = null;
				foreach (var x in clientSend ?? Enumerable.Empty<byte>())
				{
					await _server_ti.InterpretAsync(x);
				}
				CollectionAssert.AreEqual(shouldHaveTTypeList ?? Enumerable.Empty<string>(), _server_ti.TerminalTypes);
				CollectionAssert.AreEqual(serverShouldRespond, _negotiationOutput);
			}
		}

		[TestCaseSource(nameof(ClientTTypeSequences))]
		public async Task ClientEvaluationCheck(IEnumerable<byte[]> serverSends, IEnumerable<byte[]> serverShouldRespondWith)
		{
			if (serverSends.Count() != serverShouldRespondWith.Count())
				throw new Exception("Invalid Testcase.");

			foreach ((var serverSend, var clientShouldRespond) in serverSends.Zip(serverShouldRespondWith))
			{
				_negotiationOutput = null;
				foreach (var x in serverSend ?? Enumerable.Empty<byte>())
				{
					await _client_ti.InterpretAsync(x);
				}
				CollectionAssert.AreEqual(clientShouldRespond, _negotiationOutput);
			}
		}

		public static IEnumerable<TestCaseData> ClientTTypeSequences
		{
			get
			{
				yield return new TestCaseData(
					new[]
					{
						new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE }
					},
					new[]
					{
						new [] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE }
					}).SetName("Basic responds to Server TType DO");
				yield return new TestCaseData(
					new[]
					{
						new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE }
					},
					new[]
					{
						new [] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE },
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'T', (byte)'N', (byte)'C', (byte)Trigger.IAC, (byte)Trigger.SE },
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'X', (byte)'T', (byte)'E', (byte)'R', (byte)'M', (byte)Trigger.IAC, (byte)Trigger.SE },
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'M', (byte)'T', (byte)'T', (byte)'S', (byte)' ', (byte)'3', (byte)'8', (byte)'5', (byte)'3', (byte)Trigger.IAC, (byte)Trigger.SE },
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'M', (byte)'T', (byte)'T', (byte)'S', (byte)' ', (byte)'3', (byte)'8', (byte)'5', (byte)'3', (byte)Trigger.IAC, (byte)Trigger.SE },
						new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS, (byte)'T', (byte)'N', (byte)'C', (byte)Trigger.IAC, (byte)Trigger.SE }
					}).SetName("Capable of sending a TType in a cycling manner, with a repeat for the last item");
			}
		}

		public static IEnumerable<TestCaseData> ServerTTypeSequences
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
					new[] // Registered TType List After Negotiation
					{
						Array.Empty<string>()
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
						Array.Empty<string>(),
						new string[] { "ANSI"},
						new string[] { "ANSI", "VT100"},
						new string[] { "ANSI", "VT100"}
					}).SetName("Long response to Client TType Willing");
			}
		}
	}
}