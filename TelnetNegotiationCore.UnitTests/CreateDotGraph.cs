using NUnit.Framework;
using Serilog;
using Stateless.Graph;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpretors;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests
{
	[TestFixture(
		Category = "Tool", 
		Description = "Creates the DotGraph files for Server and Client forms. Some of these are combined.")]
	public class CreateDotGraph
	{
		readonly ILogger _Logger;

		public CreateDotGraph()
		{
			_Logger = Log.Logger.ForContext<CreateDotGraph>();
		}

		[Test]
		public async Task WriteClientDotGraph()
		{
			var telnet = await new TelnetInterpretor(TelnetInterpretor.TelnetMode.Client, _Logger.ForContext<TelnetInterpretor>())
			{
				CallbackOnSubmit = WriteBack,
				CallbackNegotiation = WriteToOutputStream,
				NAWSCallback = SignalNAWS,
				CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
			}.Validate().Build();

			var dotGraph = UmlDotGraph.Format(telnet.TelnetStateMachine.GetInfo());
			File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "..", "ClientDotGraph.dot"), dotGraph);
		}

		[Test]
		public async Task WriteServerDotGraph()
		{
			var telnet = await new TelnetInterpretor(TelnetInterpretor.TelnetMode.Server, _Logger.ForContext<TelnetInterpretor>())
			{
				CallbackOnSubmit = WriteBack,
				CallbackNegotiation = WriteToOutputStream,
				NAWSCallback = SignalNAWS,
				CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
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

			var dotGraph = UmlDotGraph.Format(telnet.TelnetStateMachine.GetInfo());
			File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "..", "ServerDotGraph.dot"), dotGraph);
		}

		private async Task WriteToOutputStream(byte[] arg) => await Task.CompletedTask;

		private async Task SignalNAWS(int arg1, int arg2) => await Task.CompletedTask;

		private async Task WriteBack(byte[] arg1, Encoding encoding) => await Task.CompletedTask;
	}
}
