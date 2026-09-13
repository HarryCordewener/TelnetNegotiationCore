using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="RecordingTelnetContext.Snapshot"/> is the oracle every generated property compares
/// against, so it has to be sensitive to everything that matters and blind to everything that does
/// not. These tests pin both halves of that.
/// </summary>
public class RecorderSnapshotTests
{
    private const byte SE = 240;
    private const byte SB = 250;
    private const byte WILL = 251;
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
    public async Task PendingTextExposesALineThatHasNotBeenSubmitted()
    {
        var recorder = await Run(Encoding.ASCII.GetBytes("partial"));

        await Assert.That(recorder.Lines).IsEmpty();
        await Assert.That(recorder.PendingText).IsEqualTo("partial");
    }

    [Test]
    public async Task SnapshotDistinguishesDroppedTrailingTextFromDeliveredText()
    {
        var withText = await Run(Encoding.ASCII.GetBytes("abc"));
        var withoutText = await Run();

        await Assert.That(withText.Snapshot()).IsNotEqualTo(withoutText.Snapshot());
    }

    [Test]
    public async Task SnapshotIsEqualForTwoIdenticalRuns()
    {
        var first = await Run([IAC, WILL, 31, .. "hi\r\n"u8]);
        var second = await Run([IAC, WILL, 31, .. "hi\r\n"u8]);

        await Assert.That(first.Snapshot()).IsEqualTo(second.Snapshot());
    }

    [Test]
    public async Task SnapshotSeesASubnegotiationAndANegotiationSeparately()
    {
        var negotiated = await Run([IAC, WILL, 70]);
        var subnegotiated = await Run([IAC, SB, 70, IAC, SE]);

        await Assert.That(negotiated.Snapshot()).IsNotEqualTo(subnegotiated.Snapshot());
    }

    /// <summary>
    /// Chunking a stream changes how byte runs batch into <c>Write</c> calls but must not change
    /// what the snapshot says, or the fragmentation property would fail on every case for a reason
    /// nobody cares about. This pins that intent so a later change to the recorder cannot quietly
    /// make the snapshot sensitive to call boundaries.
    /// </summary>
    [Test]
    public async Task SnapshotIsBlindToHowWriteCallsWereBatched()
    {
        var whole = new RecordingTelnetContext();
        await using (var machine = new TelnetCoreMachine(whole))
        {
            await machine.StartAsync();
            await machine.FireAsync(Encoding.ASCII.GetBytes("hello\r\n"));
        }

        var split = new RecordingTelnetContext();
        await using (var machine = new TelnetCoreMachine(split))
        {
            await machine.StartAsync();
            foreach (var b in Encoding.ASCII.GetBytes("hello\r\n"))
            {
                await machine.FireAsync(new byte[] { b });
            }
        }

        await Assert.That(whole.Snapshot()).IsEqualTo(split.Snapshot());
    }
}
