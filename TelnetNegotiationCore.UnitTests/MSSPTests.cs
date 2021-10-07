using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpretors;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests
{
	[TestFixture]
	public class MSSPTests
	{
		private TelnetInterpretor _ti;
		private Thread _tr;
		private StreamReader _input;
		private StreamWriter _output;

		private Task WriteBack(byte[] arg1, Encoding arg2)
		{
			throw new NotImplementedException();
		}

		[OneTimeSetUp]
		public void Setup()
		{
			var memoryStream = new MemoryStream();
			_input = new StreamReader(memoryStream);
			_output = new StreamWriter(memoryStream) { AutoFlush = true };

			_ti = new TelnetInterpretor(TelnetInterpretor.TelnetMode.Server)
					.RegisterStream(_input, _output)
					.RegisterCallback(WriteBack)
					.RegisterCharsetOrder(new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") })
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
					});

			_tr = new Thread(() => _ti.ProcessAsync().ConfigureAwait(false).GetAwaiter().GetResult());
			_tr.Start();
		}

		[Test]
		public void EvaluationCheck()
		{
			var client = new TcpClient();

			_input.BaseStream.Write(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });

			Thread.Sleep(1000);
			byte[] buffer = new byte[4000];
			int b;
			int c=0;
			while((b = _output.BaseStream.ReadByte()) != -1)
			{
				buffer[c] = (byte)b;
				c++;
			}

			var d = _output.BaseStream.Read(buffer, 0, 4000);
		}
	}
}