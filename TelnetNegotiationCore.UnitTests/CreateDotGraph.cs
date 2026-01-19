using TUnit.Core;
using Stateless.Graph;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

public class CreateDotGraph : BaseTest
{
    [Test]
    public async Task WriteClientDotGraph()
    {
        var telnet = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBack)
            .OnNegotiation(WriteToOutputStream)
            .AddPlugin<NAWSProtocol>()
                .OnNAWS(SignalNAWS)
            .BuildAsync();

        var dotGraph = UmlDotGraph.Format(telnet.TelnetStateMachine.GetInfo());
        await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "..", "ClientDotGraph.dot"), dotGraph);
    }

    [Test]
    public async Task WriteServerDotGraph()
    {
        var telnet = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBack)
            .OnNegotiation(WriteToOutputStream)
            .AddPlugin<NAWSProtocol>()
                .OnNAWS(SignalNAWS)
            .AddPlugin<MSSPProtocol>()
            .BuildAsync();

        var mssp = telnet.PluginManager!.GetPlugin<MSSPProtocol>();
        mssp!.SetMSSPConfig(() => new MSSPConfig
        {
            Name = "My Telnet Negotiated Server",
            UTF_8 = true,
            Gameplay = ["ABC", "DEF"],
            Extended = new Dictionary<string, dynamic>
            {
                { "Foo", "Bar" },
                { "Baz", (string[]) ["Moo", "Meow"] }
            }
        });

        var dotGraph = UmlDotGraph.Format(telnet.TelnetStateMachine.GetInfo());
        await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "..", "ServerDotGraph.dot"),
            dotGraph);
    }

    private async ValueTask WriteToOutputStream(byte[] arg) => await ValueTask.CompletedTask;

    private async ValueTask SignalNAWS(int arg1, int arg2) => await ValueTask.CompletedTask;

    private async ValueTask WriteBack(byte[] arg1, Encoding encoding, TelnetInterpreter t) => await ValueTask.CompletedTask;

    private async ValueTask WriteBackToGMCP((string module, string writeback) arg1) => await ValueTask.CompletedTask;
}