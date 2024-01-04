using NUnit.Framework;
using Serilog;
using Stateless.Graph;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
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
			var telnet = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client, _Logger.ForContext<TelnetInterpreter>())
			{
				CallbackOnSubmitAsync = WriteBack,
				SignalOnGMCPAsync = WriteBackToGMCP,
				CallbackNegotiationAsync = WriteToOutputStream,
				SignalOnNAWSAsync = SignalNAWS,
				CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
			}.BuildAsync();

			var dotGraph = UmlDotGraph.Format(telnet.TelnetStateMachine.GetInfo());
			File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "..", "ClientDotGraph.dot"), dotGraph);
		}

		[Test]
		public async Task WriteServerDotGraph()
		{
			var telnet = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, _Logger.ForContext<TelnetInterpreter>())
			{
				CallbackOnSubmitAsync = WriteBack,
				SignalOnGMCPAsync = WriteBackToGMCP,
				CallbackNegotiationAsync = WriteToOutputStream,
				SignalOnNAWSAsync = SignalNAWS,
				CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
			}
			.RegisterMSSPConfig(() => new MSSPConfig
			{
				Name =  "My Telnet Negotiated Server",
				UTF_8 =  true,
				Gameplay =  new[] { "ABC", "DEF" },
				Extended = new Dictionary<string, dynamic>
			{
								{ "Foo",  "Bar"},
								{ "Baz",  new [] {"Moo", "Meow" }}
			}
			}).BuildAsync();

			var dotGraph = UmlDotGraph.Format(telnet.TelnetStateMachine.GetInfo());
			File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "..", "ServerDotGraph.dot"), dotGraph);
		}

		private async Task WriteToOutputStream(byte[] arg) => await Task.CompletedTask;

		private async Task SignalNAWS(int arg1, int arg2) => await Task.CompletedTask;
		
		private async Task WriteBack(byte[] arg1, Encoding encoding, TelnetInterpreter t) => await Task.CompletedTask;

		private async Task WriteBackToGMCP((string module, string writeback) arg1) => await Task.CompletedTask;
	}
}
