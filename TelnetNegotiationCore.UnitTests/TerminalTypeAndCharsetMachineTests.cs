using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>TTYPE and CHARSET through the generated machine.</summary>
public class TerminalTypeAndCharsetMachineTests
{
    private const byte SE = 240;
    private const byte SEND = 1;
    private const byte IS = 0;
    private const byte SB = 250;
    private const byte IAC = 255;

    private static async Task<RecordingTelnetContext> Run(params byte[] bytes)
    {
        var recorder = new RecordingTelnetContext();
        await using var machine = new TelnetCoreMachine(recorder, TelnetMachineConfig.Default);
        await machine.StartAsync();
        await machine.FireAsync(bytes);
        return recorder;
    }

    [Test]
    public async Task TerminalTypeAnswersSend()
    {
        var recorder = await Run([IAC, SB, 24, SEND, IAC, SE]);

        await Assert.That(recorder.TerminalTypeRequests).IsEqualTo(1);
    }

    [Test]
    public async Task TerminalTypeReadsIsAsText()
    {
        var recorder = await Run([IAC, SB, 24, IS, .. "xterm"u8.ToArray(), IAC, SE]);

        await Assert.That(recorder.TerminalTypeReports).HasSingleItem();
        await Assert.That(recorder.TerminalTypeReports[0]).IsEquivalentTo("xterm"u8.ToArray());
    }

    [Test]
    public async Task CharsetRequestReadsTheSeparatorDelimitedList()
    {
        byte[] payload = [(byte)';', .. "UTF-8;ASCII"u8.ToArray()];
        var recorder = await Run([IAC, SB, 42, 1, .. payload, IAC, SE]);

        await Assert.That(recorder.CharsetRequests).HasSingleItem();
        await Assert.That(recorder.CharsetRequests[0]).IsEquivalentTo(payload);
    }

    [Test]
    public async Task CharsetAcceptedReadsTheName()
    {
        var recorder = await Run([IAC, SB, 42, 2, .. "UTF-8"u8.ToArray(), IAC, SE]);

        await Assert.That(recorder.CharsetAccepted).HasSingleItem();
        await Assert.That(recorder.CharsetAccepted[0]).IsEquivalentTo("UTF-8"u8.ToArray());
    }

    [Test]
    public async Task CharsetRejectedCarriesNoPayload()
    {
        var recorder = await Run([IAC, SB, 42, 3, IAC, SE]);

        await Assert.That(recorder.CharsetRejections).IsEqualTo(1);
    }

    [Test]
    public async Task CharsetTTableIsReadsTheTable()
    {
        var recorder = await Run([IAC, SB, 42, 4, 1, 2, 3, IAC, SE]);

        await Assert.That(recorder.CharsetTTables).HasSingleItem();
        await Assert.That(recorder.CharsetTTables[0]).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task CharsetTTableRejectedAckAndNakCarryNoPayload()
    {
        var rejected = await Run([IAC, SB, 42, 5, IAC, SE]);
        var acked = await Run([IAC, SB, 42, 6, IAC, SE]);
        var naked = await Run([IAC, SB, 42, 7, IAC, SE]);

        await Assert.That(rejected.CharsetTTableRejections).IsEqualTo(1);
        await Assert.That(acked.CharsetTTableAcks).IsEqualTo(1);
        await Assert.That(naked.CharsetTTableNaks).IsEqualTo(1);
    }
}
