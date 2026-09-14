using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>MXP and the three MCCP start markers through the generated machine.</summary>
public class MxpAndMccpMachineTests
{
    private const byte SE = 240;
    private const byte WILL = 251;
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
    public async Task MxpStartMarkerCarriesNoPayload()
    {
        var recorder = await Run([IAC, SB, 91, IAC, SE]);

        await Assert.That(recorder.MxpStarts).IsEqualTo(1);
    }

    [Test]
    public async Task Mccp2MarkerCarriesNoPayload()
    {
        var recorder = await Run([IAC, SB, 86, IAC, SE]);

        await Assert.That(recorder.Mccp2Markers).IsEqualTo(1);
    }

    [Test]
    public async Task Mccp3MarkerCarriesNoPayload()
    {
        var recorder = await Run([IAC, SB, 87, IAC, SE]);

        await Assert.That(recorder.Mccp3Markers).IsEqualTo(1);
    }

    /// <summary>MCCP1's own shape: IAC SB COMPRESS WILL SE, no doubled IAC at all.</summary>
    [Test]
    public async Task Mccp1MarkerUsesWillNotIac()
    {
        var recorder = await Run([IAC, SB, 85, WILL, SE]);

        await Assert.That(recorder.Mccp1Markers).IsEqualTo(1);
    }

    /// <summary>What the 2.17.0 fix was for: text right behind the marker must not be swallowed looking for IAC SE.</summary>
    [Test]
    public async Task Mccp1MarkerDoesNotConsumeWhatFollowsIt()
    {
        var recorder = await Run([IAC, SB, 85, WILL, SE, .. "ok\n"u8.ToArray()]);

        await Assert.That(recorder.Mccp1Markers).IsEqualTo(1);
        await Assert.That(recorder.Lines).IsEquivalentTo(new[] { "ok" });
    }
}
