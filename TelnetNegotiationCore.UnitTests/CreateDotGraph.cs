using NUnit.Framework;
using Stateless.Graph;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.UnitTests;

[TestFixture(
    Category = "Tool",
    Description = "Creates the DotGraph files for Server and Client forms. Some of these are combined.")]
public class CreateDotGraph : BaseTest
{
    [Test]
    public async Task WriteClientDotGraph()
    {
        var telnet = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client, logger)
        {
            CallbackOnSubmitAsync = WriteBack,
            SignalOnGMCPAsync = WriteBackToGMCP,
            CallbackNegotiationAsync = WriteToOutputStream,
            SignalOnNAWSAsync = SignalNAWS,
            CharsetOrder = [Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1")]
        }.BuildAsync();

        var dotGraph = UmlDotGraph.Format(telnet.TelnetStateMachine.GetInfo());
        await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "..", "ClientDotGraph.dot"), dotGraph);
    }

    [Test]
    public async Task WriteServerDotGraph()
    {
        var telnet = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, logger)
            {
                CallbackOnSubmitAsync = WriteBack,
                SignalOnGMCPAsync = WriteBackToGMCP,
                CallbackNegotiationAsync = WriteToOutputStream,
                SignalOnNAWSAsync = SignalNAWS,
                CharsetOrder = [Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1")]
            }
            .RegisterMSSPConfig(() => new MSSPConfig
            {
                Name = "My Telnet Negotiated Server",
                UTF_8 = true,
                Gameplay = ["ABC", "DEF"],
                Extended = new Dictionary<string, dynamic>
                {
                    { "Foo", "Bar" },
                    { "Baz", (string[]) ["Moo", "Meow"] }
                }
            }).BuildAsync();

        var dotGraph = UmlDotGraph.Format(telnet.TelnetStateMachine.GetInfo());
        await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "..", "ServerDotGraph.dot"),
            dotGraph);
    }

    private async ValueTask WriteToOutputStream(byte[] arg) => await ValueTask.CompletedTask;

    private async ValueTask SignalNAWS(int arg1, int arg2) => await ValueTask.CompletedTask;

    private async ValueTask WriteBack(byte[] arg1, Encoding encoding, TelnetInterpreter t) => await ValueTask.CompletedTask;

    private async ValueTask WriteBackToGMCP((string module, string writeback) arg1) => await ValueTask.CompletedTask;
}