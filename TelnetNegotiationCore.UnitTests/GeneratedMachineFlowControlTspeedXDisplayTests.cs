using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>FLOWCONTROL, TSPEED and XDISPLOC through the generated machine, negotiation and subnegotiation both.</summary>
public class GeneratedMachineFlowControlTspeedXDisplayTests : BaseTest
{
    [Test]
    public async Task ClientReceivesFlowControlOff()
    {
        bool? flowControlStateChanged = null;
        ValueTask CaptureFlowControlStateChanged(bool enabled) { flowControlStateChanged = enabled; return ValueTask.CompletedTask; }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(CaptureFlowControlStateChanged)
            .BuildAsync();

        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();
        flowControlStateChanged = null;

        await client.InterpretByteArrayAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_OFF,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });
        await client.WaitForProcessingAsync();

        await Assert.That(flowControlStateChanged).IsNotNull();
        await Assert.That(flowControlStateChanged.Value).IsFalse();

        var plugin = client.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin!.IsFlowControlEnabled).IsFalse();

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientRespondsWithWillFlowControlToServerDo()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerRequestsTerminalSpeed()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<TerminalSpeedProtocol>());

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientSendsTerminalSpeedWhenRequested()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<TerminalSpeedProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TSPEED });
        negotiationOutput = null;

        await InterpretAndWaitAsync(client, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(negotiationOutput).IsNotNull();
        var expectedBytes = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS }
            .Concat(Encoding.ASCII.GetBytes("38400,38400"))
            .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
            .ToArray();
        await AssertByteArraysEqual(negotiationOutput, expectedBytes);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerReceivesAndParsesTerminalSpeed()
    {
        var speedReceived = false;
        int transmit = 0, receive = 0;
        ValueTask HandleTerminalSpeed(int t, int r) { transmit = t; receive = r; speedReceived = true; return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<TerminalSpeedProtocol>()
                .OnTerminalSpeed(HandleTerminalSpeed));

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });

        var speedBytes = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS }
            .Concat(Encoding.ASCII.GetBytes("9600,9600"))
            .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
            .ToArray();
        await InterpretAndWaitAsync(server, speedBytes);

        await Assert.That(speedReceived).IsTrue();
        await Assert.That(transmit).IsEqualTo(9600);
        await Assert.That(receive).IsEqualTo(9600);

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerReceivesAndParsesXDisplayLocation()
    {
        string displayReceived = null;
        ValueTask HandleXDisplay(string display) { displayReceived = display; return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<XDisplayProtocol>()
                .OnDisplayLocation(HandleXDisplay));

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });

        var displayBytes = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.IS }
            .Concat(Encoding.ASCII.GetBytes("unix:0.0"))
            .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
            .ToArray();
        await InterpretAndWaitAsync(server, displayBytes);

        await Assert.That(displayReceived).IsEqualTo("unix:0.0");

        await server.DisposeAsync();
    }
}
