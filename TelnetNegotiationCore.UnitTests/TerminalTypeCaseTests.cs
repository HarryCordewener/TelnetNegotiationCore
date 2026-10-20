using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class TerminalTypeCaseTests : BaseTest
{
    [Test]
    [Arguments(false, "VT100")]
    [Arguments(false, "vt100")]
    [Arguments(false, "vT100")]
    [Arguments(true, "VT100")]
    [Arguments(true, "vt100")]
    [Arguments(true, "ansi")]
    public async Task ServerStopsRequestingOnCaseInsensitiveRepeat(bool multipleTypes, string repeatedType)
    {
        var negotiations = new List<byte[]>();
        var snapshots = new List<string[]>();
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(data =>
            {
                negotiations.Add(data.ToArray());
                return ValueTask.CompletedTask;
            })
            .AddPlugin<TerminalTypeProtocol>()
                .OnTerminalTypes(types =>
                {
                    snapshots.Add([.. types]);
                    return ValueTask.CompletedTask;
                }));

        try
        {
            await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE]);

            string[] expectedTypes = multipleTypes ? ["ANSI", "VT100"] : ["VT100"];
            foreach (var type in expectedTypes)
            {
                negotiations.Clear();
                await ReportAsync(type);
                await Assert.That(negotiations.Count).IsEqualTo(1);
                await AssertByteArraysEqual(negotiations[0],
                [
                    (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND,
                    (byte)Trigger.IAC, (byte)Trigger.SE
                ]);
            }

            negotiations.Clear();
            await ReportAsync(repeatedType);

            // RFC 1091 names ignore case. Keep the peer's first spelling and stop the SEND cycle.
            await Assert.That(negotiations.Count).IsEqualTo(0);
            await Assert.That(server.TerminalTypes).IsEquivalentTo(expectedTypes);
            await Assert.That(snapshots.Count).IsEqualTo(expectedTypes.Length + 1);
            await Assert.That(snapshots[snapshots.Count - 1]).IsEquivalentTo(expectedTypes);
        }
        finally
        {
            await server.DisposeAsync();
        }

        Task ReportAsync(string type) => InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS,
            .. Encoding.ASCII.GetBytes(type),
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);
    }
}

