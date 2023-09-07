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
	public class CHARSETTests
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
				CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
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

		[TestCaseSource(nameof(ServerCHARSETSequences))]
		public async Task ServerEvaluationCheck(IEnumerable<byte[]> clientSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<string[]> RegisteredCHARSETS)
		{
			if (clientSends.Count() != serverShouldRespondWith.Count())
				throw new Exception("Invalid Testcase.");

			foreach ((var clientSend, var serverShouldRespond, var shouldHaveCHARSETList) in clientSends.Zip(serverShouldRespondWith, RegisteredCHARSETS))
			{
				_negotiationOutput = null;
				foreach (var x in clientSend ?? Enumerable.Empty<byte>())
				{
					await _server_ti.InterpretAsync(x);
				}
				CollectionAssert.AreEqual(shouldHaveCHARSETList ?? Enumerable.Empty<string>(), _server_ti.TerminalTypes);
				CollectionAssert.AreEqual(serverShouldRespond, _negotiationOutput);
			}
		}

		[TestCaseSource(nameof(ClientCHARSETSequences))]
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

		public static IEnumerable<TestCaseData> ClientCHARSETSequences
		{
			get
			{
				yield return new TestCaseData(
					new[]
					{
						new [] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET }
					},
					new[]
					{
						new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET }
					}).SetName("Basic responds to Server CHARSET WILL");
				yield return new TestCaseData(
					new[]
					{
						new [] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET },
						new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED, (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', (byte)Trigger.IAC, (byte)Trigger.SE }

					},
					new[]
					{
						new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8',
							(byte)';', (byte)'i', (byte)'s', (byte)'o', (byte)'-', (byte)'8', (byte)'8',(byte)'5', (byte)'9',(byte)'-', (byte)'1',
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'1', (byte)'6',
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'1', (byte)'6',(byte)'B', (byte)'E',
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'3', (byte)'2',
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'3', (byte)'2',(byte)'B', (byte)'E',
							(byte)';', (byte)'u', (byte)'s', (byte)'-', (byte)'a', (byte)'s', (byte)'c',(byte)'i', (byte)'i',
							(byte)Trigger.IAC, (byte)Trigger.SE },
						null
					}).SetName("Capable of sending a CHARSET list");
			}
		}

		public static IEnumerable<TestCaseData> ServerCHARSETSequences
		{
			get
			{
				yield return new TestCaseData(
					new[] { // Client Sends
						new [] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET }
					},
					new[] { // Server Should Respond With
						new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET }
					},
					new[] // Registered CHARSET List After Negotiation
					{
						Array.Empty<string>()
					}).SetName("Basic responds to Client CHARSET Willing");
				yield return new TestCaseData(
					new[] { // Client Sends
						new [] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'1', (byte)'6',
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'1', (byte)'6',(byte)'B', (byte)'E',
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'3', (byte)'2',(byte)'B', (byte)'E',
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'3', (byte)'2',
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8',
							(byte)';', (byte)'i', (byte)'s', (byte)'o', (byte)'-', (byte)'8', (byte)'8',(byte)'5', (byte)'9',(byte)'-', (byte)'1',
							(byte)';', (byte)'u', (byte)'s', (byte)'-', (byte)'a', (byte)'s', (byte)'c',(byte)'i', (byte)'i',
							(byte)Trigger.IAC, (byte)Trigger.SE }
					},
					new[] { // Server Should Respond With
						new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED, (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'1', (byte)'6', (byte)Trigger.IAC, (byte)Trigger.SE }
					},
					new[] // Registered CHARSET List After Negotiation
					{
						Array.Empty<string>(),
						Array.Empty<string>(),
						new string[] { "UTF-8"}
					}).SetName("Basic response to Client Negotiation with one option");
				yield return new TestCaseData(
					new[] { // Client Sends
						new [] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
							(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8',
							(byte)';', (byte)'a', (byte)'n', (byte)'s', (byte)'i',
							(byte)Trigger.IAC, (byte)Trigger.SE }
					},
					new[] { // Server Should Respond With
						new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET },
						new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED, (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', (byte)Trigger.IAC, (byte)Trigger.SE }
					},
					new[] // Registered CHARSET List After Negotiation
					{
						Array.Empty<string>(),
						Array.Empty<string>(),
						new string[] { "UTF-8"}
					}).SetName("Basic response to Client Negotiation with two options");
			}
		}
	}
}