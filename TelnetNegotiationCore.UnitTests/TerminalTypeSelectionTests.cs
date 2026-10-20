using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class TerminalTypeSelectionTests : BaseTest
{
    private static byte[] Report(string name) => [255, 250, 24, 0, .. Encoding.ASCII.GetBytes(name), 255, 240];

    [Test]
    [Arguments("ANSI|VT100|VT100", "ANSI|VT100|VT100")]
    [Arguments("VT100|VT100", "VT100|VT100")]
    [Arguments("ANSI|VT100|ANSI", "ANSI|VT100|ANSI")]
    [Arguments("MY-CLIENT|VT100|MTTS 2|MTTS 2", "MY-CLIENT|VT100|VT100|VT100")]
    [Arguments("MY-CLIENT|MTTS 4|MTTS 4", "MY-CLIENT|MY-CLIENT|MY-CLIENT")]
    [Arguments("MTTS 8|VT100|VT100", "unknown|VT100|VT100")]
    [Arguments("MTTS 2|MTTS 2", "unknown|unknown")]
    public async Task ServerSelectionTracksReportedNamesAndSurvivesMttsExpansion(string reports, string selections)
    {
        var output = new List<byte>();
        var callbackSelections = new List<string>();
        var plugin = new TerminalTypeProtocol();
        plugin.OnTerminalTypes(_ => { callbackSelections.Add(plugin.CurrentTerminalType); return default; });
        await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { output.AddRange(bytes.ToArray()); return default; })
            .AddPlugin(plugin));
        await InterpretAndWaitAsync(server, [255, 251, 24]);
        await Assert.That(plugin.CurrentTerminalType).IsEqualTo("unknown");
        await Assert.That(server.CurrentTerminalType).IsEqualTo("unknown");

        var expected = selections.Split('|');
        var names = reports.Split('|');
        for (var n = 0; n < names.Length; n++)
        {
            output.Clear();
            await InterpretAndWaitAsync(server, Report(names[n]));
            await Assert.That(plugin.CurrentTerminalType).IsEqualTo(expected[n]);
            await Assert.That(server.CurrentTerminalType).IsEqualTo(expected[n]);
            await Assert.That(callbackSelections[n]).IsEqualTo(expected[n]);
            await Assert.That(server.TerminalTypes).IsEquivalentTo(plugin.TerminalTypes);
        }
        await Assert.That(output.Count).IsEqualTo(0);

        await InterpretAndWaitAsync(server, [255, 252, 24]);
        await Assert.That(plugin.CurrentTerminalType).IsEqualTo("unknown");
        await Assert.That(server.CurrentTerminalType).IsEqualTo("unknown");
        await Assert.That(server.TerminalTypes.Count).IsEqualTo(0);

        await InterpretAndWaitAsync(server, [255, 251, 24]);
        await InterpretAndWaitAsync(server, Report("XTERM"));
        await Assert.That(plugin.CurrentTerminalType).IsEqualTo("XTERM");
        await Assert.That(server.CurrentTerminalType).IsEqualTo("XTERM");
        await Assert.That(server.TerminalTypes).IsEquivalentTo(["XTERM"]);
    }

    [Test]
    public async Task ClientSelectionRemainsTheLastResponseSent()
    {
        var plugin = new TerminalTypeProtocol().WithTerminalTypes("MY-CLIENT", "VT100", "MTTS 2");
        await using var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client).UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback).OnNegotiation(_ => default).AddPlugin(plugin));
        await InterpretAndWaitAsync(client, [255, 253, 24]);
        foreach (var expected in new[] { "MY-CLIENT", "VT100", "MTTS 2", "MTTS 2", "MY-CLIENT" })
        {
            await InterpretAndWaitAsync(client, [255, 250, 24, 1, 255, 240]);
            await Assert.That(plugin.CurrentTerminalType).IsEqualTo(expected);
            await Assert.That(client.CurrentTerminalType).IsEqualTo(expected);
        }
    }
}

