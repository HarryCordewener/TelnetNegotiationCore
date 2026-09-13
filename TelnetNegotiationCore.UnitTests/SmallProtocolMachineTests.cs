using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>FLOWCONTROL, TSPEED and XDISPLOC through the generated machine.</summary>
public class SmallProtocolMachineTests
{
    private const byte SE = 240;
    private const byte SEND = 1;
    private const byte IS = 0;
    private const byte SB = 250;
    private const byte IAC = 255;

    private static async Task<RecordingTelnetContext> Run(params byte[] bytes)
    {
        var recorder = new RecordingTelnetContext();
        await using var machine = new TelnetCoreMachine(recorder);
        await machine.StartAsync();
        await machine.FireAsync(bytes);
        return recorder;
    }

    [Test]
    public async Task FlowControlCapturesItsOneCommandByte()
    {
        const byte flowControlOff = 0;
        var recorder = await Run([IAC, SB, 33, flowControlOff, IAC, SE]);

        await Assert.That(recorder.FlowControlCommands).IsEquivalentTo(new byte[] { flowControlOff });
    }

    [Test]
    public async Task TerminalSpeedAnswersSend()
    {
        var recorder = await Run([IAC, SB, 32, SEND, IAC, SE]);

        await Assert.That(recorder.TerminalSpeedRequests).IsEqualTo(1);
    }

    [Test]
    public async Task TerminalSpeedReadsIsAsText()
    {
        var recorder = await Run([IAC, SB, 32, IS, .. "38400,38400"u8.ToArray(), IAC, SE]);

        await Assert.That(recorder.TerminalSpeedReports).HasSingleItem();
        await Assert.That(recorder.TerminalSpeedReports[0]).IsEquivalentTo("38400,38400"u8.ToArray());
    }

    [Test]
    public async Task XDisplayLocationAnswersSend()
    {
        var recorder = await Run([IAC, SB, 35, SEND, IAC, SE]);

        await Assert.That(recorder.XDisplayLocationRequests).IsEqualTo(1);
    }

    [Test]
    public async Task XDisplayLocationReadsIsAsText()
    {
        var recorder = await Run([IAC, SB, 35, IS, .. "unix:0.0"u8.ToArray(), IAC, SE]);

        await Assert.That(recorder.XDisplayLocationReports).HasSingleItem();
        await Assert.That(recorder.XDisplayLocationReports[0]).IsEquivalentTo("unix:0.0"u8.ToArray());
    }
}
