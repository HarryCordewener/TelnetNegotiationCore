using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>MSSP through the generated machine, receiving a report end to end.</summary>
public class GeneratedMachineMsspTests : BaseTest
{
    [Test]
    public async Task ClientReceivesAnMsspReport()
    {
        MSSPConfig received = null;
        ValueTask OnMssp(MSSPConfig config) { received = config; return ValueTask.CompletedTask; }

        var client = await new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((a, e, t) => ValueTask.CompletedTask)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<MSSPProtocol>()
                .OnMSSP(OnMssp)
            .BuildAsync();

        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP });
        await client.WaitForProcessingAsync();

        var msspBytes = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSSP, (byte)Trigger.MSSP_VAR };
        msspBytes.AddRange(Encoding.ASCII.GetBytes("NAME"));
        msspBytes.Add((byte)Trigger.MSSP_VAL);
        msspBytes.AddRange(Encoding.ASCII.GetBytes("MyServer"));
        msspBytes.Add((byte)Trigger.IAC);
        msspBytes.Add((byte)Trigger.SE);

        await client.InterpretByteArrayAsync(msspBytes.ToArray());
        await client.WaitForProcessingAsync();

        await Assert.That(await PollUntilAsync(() => received != null)).IsTrue();
        await Assert.That(received!.Name).IsEqualTo("MyServer");

        await client.DisposeAsync();
    }
}
