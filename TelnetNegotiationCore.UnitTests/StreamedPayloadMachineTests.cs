using System.Threading.Tasks;
using TelnetNegotiationCore.Machine;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>GMCP and MSDP through the generated machine: a payload of no fixed length, escaped IAC included.</summary>
public class StreamedPayloadMachineTests
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
    public async Task GmcpReadsItsPackageAndPayload()
    {
        var recorder = await Run([IAC, SB, 201, .. "Core.Ping \"1\""u8.ToArray(), IAC, SE]);

        await Assert.That(recorder.GmcpMessages).HasSingleItem();
        await Assert.That(recorder.GmcpMessages[0]).IsEquivalentTo("Core.Ping \"1\""u8.ToArray(), CollectionOrdering.Matching);
    }

    /// <summary>A literal 255 inside the JSON, doubled on the wire per RFC 854.</summary>
    [Test]
    public async Task GmcpKeepsAnEscapedIacAsOneByteOfData()
    {
        var recorder = await Run([IAC, SB, 201, 0x41, IAC, IAC, 0x42, IAC, SE]);

        await Assert.That(recorder.GmcpMessages).HasSingleItem();
        await Assert.That(recorder.GmcpMessages[0]).IsEquivalentTo(new byte[] { 0x41, IAC, 0x42 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task MsdpReadsItsPayload()
    {
        byte[] expected = [1, .. "NAME"u8.ToArray(), 2, .. "value"u8.ToArray()];
        var recorder = await Run([IAC, SB, 69, .. expected, IAC, SE]);

        await Assert.That(recorder.MsdpMessages).HasSingleItem();
        await Assert.That(recorder.MsdpMessages[0]).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task MsspReadsRepeatingVarValPairs()
    {
        byte[] payload =
        [
            1, .. "NAME"u8.ToArray(), 2, .. "Server"u8.ToArray(),
            1, .. "PLAYERS"u8.ToArray(), 2, .. "3"u8.ToArray(),
        ];
        var recorder = await Run([IAC, SB, 70, .. payload, IAC, SE]);

        await Assert.That(recorder.MsspEvents).IsEquivalentTo(new[]
        {
            "started", "VAR", "NAME", "VAL", "Server", "VAR", "PLAYERS", "VAL", "3", "ended",
        }, CollectionOrdering.Matching);
    }
}
