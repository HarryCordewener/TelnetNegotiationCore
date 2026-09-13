using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>LINEMODE, AUTHENTICATION and ENCRYPT through the generated machine.</summary>
public class LineModeAuthenticationEncryptionMachineTests
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
    public async Task LineModeReadsModeData()
    {
        var recorder = await Run([IAC, SB, 34, 1, 7, IAC, SE]);

        await Assert.That(recorder.LineModeMessages).HasSingleItem();
        await Assert.That(recorder.LineModeMessages[0].Kind).IsEqualTo((byte)1);
        await Assert.That(recorder.LineModeMessages[0].Data).IsEquivalentTo(new byte[] { 7 });
    }

    [Test]
    public async Task LineModeReadsForwardMaskAndSlc()
    {
        var forwardMask = await Run([IAC, SB, 34, 2, 1, 2, IAC, SE]);
        var slc = await Run([IAC, SB, 34, 3, 9, 9, 9, IAC, SE]);

        await Assert.That(forwardMask.LineModeMessages[0].Kind).IsEqualTo((byte)2);
        await Assert.That(slc.LineModeMessages[0].Kind).IsEqualTo((byte)3);
    }

    [Test]
    public async Task AuthenticationSendReadsTheOfferedTypes()
    {
        var recorder = await Run([IAC, SB, 37, 1, 1, 2, 3, IAC, SE]);

        await Assert.That(recorder.AuthenticationSends).HasSingleItem();
        await Assert.That(recorder.AuthenticationSends[0]).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task AuthenticationIsReadsTheResponse()
    {
        var recorder = await Run([IAC, SB, 37, 0, 9, 9, IAC, SE]);

        await Assert.That(recorder.AuthenticationIsMessages).HasSingleItem();
        await Assert.That(recorder.AuthenticationIsMessages[0]).IsEquivalentTo(new byte[] { 9, 9 });
    }

    [Test]
    public async Task EncryptionSendReadsTheSupportedTypes()
    {
        var recorder = await Run([IAC, SB, 38, 1, 1, 2, IAC, SE]);

        await Assert.That(recorder.EncryptionSends).HasSingleItem();
        await Assert.That(recorder.EncryptionSends[0]).IsEquivalentTo(new byte[] { 1, 2 });
    }

    [Test]
    public async Task EncryptionIsReadsTheInitData()
    {
        var recorder = await Run([IAC, SB, 38, 0, 5, 6, IAC, SE]);

        await Assert.That(recorder.EncryptionIsMessages).HasSingleItem();
        await Assert.That(recorder.EncryptionIsMessages[0]).IsEquivalentTo(new byte[] { 5, 6 });
    }

    [Test]
    public async Task EncryptionStartReadsTheKeyId()
    {
        var recorder = await Run([IAC, SB, 38, 3, 9, 8, IAC, SE]);

        await Assert.That(recorder.EncryptionStarts).HasSingleItem();
        await Assert.That(recorder.EncryptionStarts[0]).IsEquivalentTo(new byte[] { 9, 8 });
    }

    [Test]
    public async Task EncryptionStartWithNoKeyIdReadsAnEmptyPayload()
    {
        var recorder = await Run([IAC, SB, 38, 3, IAC, SE]);

        await Assert.That(recorder.EncryptionStarts).HasSingleItem();
        await Assert.That(recorder.EncryptionStarts[0]).IsEquivalentTo(Array.Empty<byte>());
    }

    [Test]
    public async Task EncryptionEndFiresWithNoPayload()
    {
        var recorder = await Run([IAC, SB, 38, 4, IAC, SE]);

        await Assert.That(recorder.EncryptionEnds).IsEqualTo(1);
    }
}
