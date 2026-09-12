using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>ENVIRON and NEW-ENVIRON through the generated machine.</summary>
public class EnvironMachineTests
{
    private const byte SE = 240;
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
    public async Task NewEnvironReadsAVarAndItsValue()
    {
        byte[] payload = [0, 0, .. "USER"u8.ToArray(), 1, .. "grave"u8.ToArray()];
        var recorder = await Run([IAC, SB, 39, .. payload, IAC, SE]);

        await Assert.That(recorder.NewEnvironEvents).IsEquivalentTo(new[]
        {
            "started 0", "VAR", "USER", "VALUE", "grave", "ended",
        });
    }

    [Test]
    public async Task NewEnvironDistinguishesUserVarFromVar()
    {
        byte[] payload = [2, 3, .. "MYVAR"u8.ToArray(), 1, .. "yes"u8.ToArray()];
        var recorder = await Run([IAC, SB, 39, .. payload, IAC, SE]);

        await Assert.That(recorder.NewEnvironEvents).IsEquivalentTo(new[]
        {
            "started 2", "USERVAR", "MYVAR", "VALUE", "yes", "ended",
        });
    }

    [Test]
    public async Task NewEnvironSendCarriesNamesWithNoValues()
    {
        byte[] payload = [1, 0, .. "USER"u8.ToArray(), 0, .. "TERM"u8.ToArray()];
        var recorder = await Run([IAC, SB, 39, .. payload, IAC, SE]);

        await Assert.That(recorder.NewEnvironEvents).IsEquivalentTo(new[]
        {
            "started 1", "VAR", "USER", "VAR", "TERM", "ended",
        });
    }

    [Test]
    public async Task EnvironReadsAVarAndItsValue()
    {
        byte[] payload = [0, 0, .. "USER"u8.ToArray(), 1, .. "grave"u8.ToArray()];
        var recorder = await Run([IAC, SB, 36, .. payload, IAC, SE]);

        await Assert.That(recorder.EnvironEvents).IsEquivalentTo(new[]
        {
            "started 0", "VAR", "USER", "VALUE", "grave", "ended",
        });
    }
}
