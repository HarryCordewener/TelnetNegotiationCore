#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// RFC 2946, the ENCRYPT option. This library carries the messages; the cryptography belongs to the
/// consumer's callbacks, so what there is to test is the framing and which callback fires when.
/// </summary>
public class EncryptionTests : BaseTest
{
    private static readonly byte[] DoEncrypt =
        [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ENCRYPT];

    private static readonly byte[] WillEncrypt =
        [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENCRYPT];

    private byte[] _negotiationOutput = [];

    private ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
    {
        _negotiationOutput = data.ToArray();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ negotiation

    [Test]
    public async Task ServerOffersEncryptionOnConnect()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        await AssertByteArraysEqual(_negotiationOutput, DoEncrypt);

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientAnswersWillToDoEncrypt()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(client, DoEncrypt);

        await AssertByteArraysEqual(_negotiationOutput, WillEncrypt);

        await client.DisposeAsync();
    }

    // ------------------------------------------------------------------ SUPPORT, client side

    [Test]
    public async Task ClientIsHandedTheSupportBodyCommandByteFirst()
    {
        byte[]? offered = null;

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionSupport(types =>
                {
                    offered = types;
                    return new ValueTask<byte[]?>((byte[]?)null);
                }));

        await InterpretAndWaitAsync(client, DoEncrypt);
        await InterpretAndWaitAsync(client,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            1,          // SUPPORT
            1, 3,       // DES_CFB64, DES3_CFB64
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        // The subnegotiation body as it arrived — the same shape PluginFluentConfigurationTests pins.
        await AssertByteArraysEqual(offered!, [1, 1, 3]);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientWithNoSupportCallbackRejectsWithIsNull()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(client, DoEncrypt);
        await InterpretAndWaitAsync(client,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            1, 1, 3,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        // IAC SB ENCRYPT IS NULL IAC SE — nothing to encrypt with, said out loud.
        await AssertByteArraysEqual(_negotiationOutput,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            0,          // IS
            0,          // NULL
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientSendsWhatItsSupportCallbackReturned()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionSupport(_ => new ValueTask<byte[]?>(new byte[] { 1, 0xAA })));

        await InterpretAndWaitAsync(client, DoEncrypt);
        await InterpretAndWaitAsync(client,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            1, 1, 3,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await AssertByteArraysEqual(_negotiationOutput,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            0,              // IS
            1, 0xAA,        // DES_CFB64, and the initialisation the callback produced
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await client.DisposeAsync();
    }

    // ------------------------------------------------------------------ IS, server side

    [Test]
    public async Task ServerIsHandedTheIsBodyCommandByteFirst()
    {
        byte[]? received = null;

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionRequest(data => { received = data; return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(server, WillEncrypt);
        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            0,              // IS
            1,              // DES_CFB64
            0x01, 0x02,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await AssertByteArraysEqual(received!, [0, 1, 0x01, 0x02]);

        await server.DisposeAsync();
    }

    // ------------------------------------------------------------------ START and END

    [Test]
    public async Task StartFromThePeerReachesOnEncryptionStartWithTheKeyId()
    {
        byte[]? keyId = null;

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionStart(id => { keyId = id; return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(server, WillEncrypt);
        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            3,              // START
            0x07,           // keyid
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await AssertByteArraysEqual(keyId!, [0x07]);

        await server.DisposeAsync();
    }

    [Test]
    public async Task EndFromThePeerReachesOnEncryptionEnd()
    {
        var ended = false;

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionEnd(() => { ended = true; return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(server, WillEncrypt);
        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            4,              // END
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await Assert.That(ended).IsTrue();

        await server.DisposeAsync();
    }

    // ------------------------------------------------------------------ the send methods

    [Test]
    public async Task SendEncryptionSupportWritesTheSupportFrame()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(server, WillEncrypt);

        var enc = server.PluginManager!.GetPlugin<EncryptionProtocol>()!;
        await enc.SendEncryptionSupportAsync([1, 3]);

        await AssertByteArraysEqual(_negotiationOutput,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            1, 1, 3,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await server.DisposeAsync();
    }

    [Test]
    public async Task SendEncryptionIsWritesTheIsFrame()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(client, DoEncrypt);

        var enc = client.PluginManager!.GetPlugin<EncryptionProtocol>()!;
        await enc.SendEncryptionIsAsync([1, 0x01, 0x02]);

        await AssertByteArraysEqual(_negotiationOutput,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            0, 1, 0x01, 0x02,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await client.DisposeAsync();
    }

    [Test]
    public async Task SendEncryptionReplyWritesTheReplyFrame()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(server, WillEncrypt);

        var enc = server.PluginManager!.GetPlugin<EncryptionProtocol>()!;
        await enc.SendEncryptionReplyAsync([1, 0x03, 0x04]);

        await AssertByteArraysEqual(_negotiationOutput,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            2, 1, 0x03, 0x04,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await server.DisposeAsync();
    }

    [Test]
    public async Task SendEncryptionStartWritesTheStartFrame()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(client, DoEncrypt);

        var enc = client.PluginManager!.GetPlugin<EncryptionProtocol>()!;
        await enc.SendEncryptionStartAsync([0x07]);

        await AssertByteArraysEqual(_negotiationOutput,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            3, 0x07,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await client.DisposeAsync();
    }

    [Test]
    public async Task SendEncryptionEndWritesTheEndFrame()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(client, DoEncrypt);

        var enc = client.PluginManager!.GetPlugin<EncryptionProtocol>()!;
        await enc.SendEncryptionEndAsync();

        await AssertByteArraysEqual(_negotiationOutput,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            4,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await client.DisposeAsync();
    }

    /// <summary>
    /// A key id of 0xFF is a lone IAC on the wire, which any receiver would read as the start of a
    /// command. RFC 854's doubling applies inside a subnegotiation like anywhere else.
    /// </summary>
    [Test]
    public async Task AKeyIdOfIacIsDoubled()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(client, DoEncrypt);

        var enc = client.PluginManager!.GetPlugin<EncryptionProtocol>()!;
        await enc.SendEncryptionStartAsync([(byte)Trigger.IAC]);

        await AssertByteArraysEqual(_negotiationOutput,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            3,
            (byte)Trigger.IAC, (byte)Trigger.IAC,     // the key id, escaped
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await client.DisposeAsync();
    }
    /// <summary>
    /// The receiving half of <see cref="AKeyIdOfIacIsDoubled"/>: a doubled <c>IAC</c> in a peer's
    /// body is one 0xFF, not a terminator and not two bytes.
    /// </summary>
    [Test]
    public async Task AnEscapedIacInAnIsBodyIsOneByte()
    {
        byte[]? received = null;

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionRequest(data => { received = data; return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(server, WillEncrypt);
        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            0,                                        // IS
            1,                                        // DES_CFB64
            (byte)Trigger.IAC, (byte)Trigger.IAC,     // one 0xFF of initialisation data
            0x02,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await AssertByteArraysEqual(received!, [0, 1, 0xFF, 0x02]);

        await server.DisposeAsync();
    }
    /// <summary>
    /// RFC 2946 splits the messages by role: the side that said <c>WILL</c> sends <c>IS</c>,
    /// <c>START</c> and <c>END</c>; the side that said <c>DO</c> sends <c>SUPPORT</c>, <c>REPLY</c>
    /// and the <c>REQUEST-</c> pair. This library's client mode answers <c>DO ENCRYPT</c> with
    /// <c>WILL ENCRYPT</c>, so a client is always the WILL side and a <c>START</c> arriving at one
    /// is the peer talking out of turn. Acting on it would tell the consumer to switch on
    /// decryption for a stream that is not encrypted.
    /// </summary>
    [Test]
    public async Task AClientIgnoresStartAndEndFromTheServer()
    {
        var started = false;
        var ended = false;

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionStart(_ => { started = true; return ValueTask.CompletedTask; })
                .OnEncryptionEnd(() => { ended = true; return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(client, DoEncrypt);

        await InterpretAndWaitAsync(client,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            3, 0x07,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);
        await InterpretAndWaitAsync(client,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            4,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await Assert.That(started).IsFalse();
        await Assert.That(ended).IsFalse();

        await client.DisposeAsync();
    }

    /// <summary>
    /// And not before the option is agreed: a <c>START</c> that arrives without any <c>WILL</c> or
    /// <c>DO</c> having been exchanged is for an option this connection is not running.
    /// </summary>
    [Test]
    public async Task StartBeforeNegotiationIsIgnored()
    {
        var started = false;

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionStart(_ => { started = true; return ValueTask.CompletedTask; }));

        // No WILL ENCRYPT from the peer: the server offered, and nobody accepted.
        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            3, 0x07,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await Assert.That(started).IsFalse();

        await server.DisposeAsync();
    }
}
