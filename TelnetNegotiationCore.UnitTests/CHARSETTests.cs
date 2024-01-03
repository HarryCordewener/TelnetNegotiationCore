using NUnit.Framework;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests
{
	[TestFixture]
	public class CHARSETTests
	{
		private byte[] _negotiationOutput;

		private Task WriteBackToOutput(byte[] arg1, Encoding arg2) => throw new NotImplementedException();

		private Task WriteBackToGMCP((string module, byte[] writeback) arg1, Encoding arg2) => throw new NotImplementedException();

		private Task ClientWriteBackToNegotiate(byte[] arg1)
		{
			_negotiationOutput = arg1;
			return Task.CompletedTask;
		}

		private Task ServerWriteBackToNegotiate(byte[] arg1)
		{
			_negotiationOutput = arg1;
			return Task.CompletedTask;
		}

		[SetUp]
		public void Setup()
		{
			_negotiationOutput = null;

			var log = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] ({TelnetMode}) {Message:lj}{NewLine}{Exception}")
				.MinimumLevel.Verbose()
				.CreateLogger();

			Log.Logger = log;
		}

		[TestCaseSource(nameof(ServerCHARSETSequences), Category = nameof(TelnetInterpreter.TelnetMode.Server))]
		public async Task ServerEvaluationCheck(IEnumerable<byte[]> clientSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<Encoding> currentEncoding)
		{
			var server_ti = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server)
			{
				CallbackNegotiation = ServerWriteBackToNegotiate,
				CallbackOnSubmit = WriteBackToOutput,
				CallbackOnGMCP = WriteBackToGMCP,
				CallbackOnByte = (x, y) => Task.CompletedTask,
			}.RegisterMSSPConfig(() => new MSSPConfig
			{
				Name =  "My Telnet Negotiated Server",
				UTF_8 =  true,
				Gameplay =  new[] { "ABC", "DEF" },
				Extended = new Dictionary<string,dynamic>
				{
					{ "Foo",  "Bar"},
					{ "Baz",  new [] {"Moo", "Meow" }}
				}
			}).Validate().Build();

			if (clientSends.Count() != serverShouldRespondWith.Count())
				throw new Exception("Invalid Testcase.");

			foreach ((var clientSend, var serverShouldRespond, var shouldHaveCurrentEncoding) in clientSends.Zip(serverShouldRespondWith, currentEncoding))
			{
				_negotiationOutput = null;
				foreach (var x in clientSend ?? Enumerable.Empty<byte>())
				{
					await server_ti.InterpretAsync(x);
				}

				Assert.AreEqual(shouldHaveCurrentEncoding, server_ti.CurrentEncoding);
				CollectionAssert.AreEqual(serverShouldRespond, _negotiationOutput);
			}
		}

		[TestCaseSource(nameof(ClientCHARSETSequences), Category = nameof(TelnetInterpreter.TelnetMode.Client))]
		public async Task ClientEvaluationCheck(IEnumerable<byte[]> serverSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<Encoding> currentEncoding)
		{
			var client_ti = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client)
			{
				CallbackNegotiation = ClientWriteBackToNegotiate,
				CallbackOnSubmit = WriteBackToOutput,
				CallbackOnGMCP = WriteBackToGMCP,
				CallbackOnByte = (x, y) => Task.CompletedTask,
				CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
			}.RegisterMSSPConfig(() => new MSSPConfig
			{
				Name =  "My Telnet Negotiated Client",
				UTF_8 =  true,
				Gameplay =  new[] { "ABC", "DEF" },
				Extended = new Dictionary<string, dynamic>
				{
					{ "Foo",  "Bar"},
					{ "Baz",  new [] {"Moo", "Meow" }}
				}
			}).Validate().Build();

			if (serverSends.Count() != serverShouldRespondWith.Count())
				throw new Exception("Invalid Testcase.");

			foreach ((var serverSend, var clientShouldRespond, var shouldHaveCurrentEncoding) in serverSends.Zip(serverShouldRespondWith, currentEncoding))
			{
				_negotiationOutput = null;
				foreach (var x in serverSend ?? Enumerable.Empty<byte>())
				{
					await client_ti.InterpretAsync(x);
				}
				Assert.AreEqual(shouldHaveCurrentEncoding, client_ti.CurrentEncoding);
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
					},
					new[] // Registered CHARSET List After Negotiation
					{
						Encoding.GetEncoding("ISO-8859-1"),
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
					},
					new[] // Registered CHARSET List After Negotiation
					{
						Encoding.GetEncoding("ISO-8859-1"),
						Encoding.GetEncoding("ISO-8859-1"),
						Encoding.UTF8
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
						Encoding.GetEncoding("ISO-8859-1")
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
						Encoding.GetEncoding("ISO-8859-1"),
						Encoding.GetEncoding("UTF-16")
					}).SetName("Basic response to Client Negotiation with many options");
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
						Encoding.GetEncoding("ISO-8859-1"),
						Encoding.UTF8
					}).SetName("Basic response to Client Negotiation with two options");
			}
		}
	}
}