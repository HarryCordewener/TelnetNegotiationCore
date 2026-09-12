using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>ENCRYPT (RFC 2946) through the generated machine. Shares AUTHENTICATION's exact shape --
/// a server only ever configured WILL/WONT, a client only ever configured DO/DONT, and the Stateless
/// capture for SEND/IS included the discriminator byte itself as the first data byte -- so these tests
/// mirror GeneratedMachineAuthenticationTests.cs rather than an existing EncryptionTests.cs, which does
/// not exist; EncryptionProtocol had no dedicated test file before this.</summary>
public class GeneratedMachineEncryptionTests : BaseTest
{
    [Test]
    public async Task ServerSendsSupportSubnegotiationAfterClientWill()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENCRYPT });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT, 1 /* SUPPORT */,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientRespondsWithIsNullToServerSupport()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ENCRYPT });
        negotiationOutput = null;

        await InterpretAndWaitAsync(client, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT, 1 /* SUPPORT */, 1, 3,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT, 0 /* IS */, 0 /* NULL */,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await client.DisposeAsync();
    }

    /// <summary>The callback receives the SUPPORT command byte itself as the first byte, the same
    /// shape Stateless's capture produced for AUTHENTICATION's SEND.</summary>
    [Test]
    public async Task ClientCanProvideCustomEncryptionSelection()
    {
        byte[] receivedSupport = null;
        byte[] negotiationOutput = null;

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(data => { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; })
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionSupport(supported =>
                {
                    receivedSupport = supported;
                    return ValueTask.FromResult((byte[])[1, 0xAA, 0xBB]);
                }));

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ENCRYPT });
        negotiationOutput = null;

        await InterpretAndWaitAsync(client, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT, 1 /* SUPPORT */, 1, 3,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(receivedSupport).IsNotNull();
        await AssertByteArraysEqual(receivedSupport, [1, 1, 3]);

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT, 0 /* IS */, 1, 0xAA, 0xBB,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerCanReceiveAndProcessEncryptionIs()
    {
        byte[] receivedEncData = null;

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionRequest(encData => { receivedEncData = encData; return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENCRYPT });

        await InterpretAndWaitAsync(server, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT, 0 /* IS */, 1, 0xCC, 0xDD,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(receivedEncData).IsNotNull();
        await AssertByteArraysEqual(receivedEncData, [0, 1, 0xCC, 0xDD]);

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerCanReceiveAndProcessEncryptionStart()
    {
        byte[] receivedKeyId = null;

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionStart(keyId => { receivedKeyId = keyId; return ValueTask.CompletedTask; }));

        var encryption = server.PluginManager!.GetPlugin<EncryptionProtocol>();
        await Assert.That(encryption.IsEncrypting).IsFalse();

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENCRYPT });

        await InterpretAndWaitAsync(server, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT, 3 /* START */, 0xAA, 0xBB,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(receivedKeyId).IsNotNull();
        await AssertByteArraysEqual(receivedKeyId, [0xAA, 0xBB]);
        await Assert.That(encryption.IsEncrypting).IsTrue();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerCanReceiveAndProcessEncryptionEnd()
    {
        var endedCount = 0;

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionEnd(() => { endedCount++; return ValueTask.CompletedTask; }));

        var encryption = server.PluginManager!.GetPlugin<EncryptionProtocol>();

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENCRYPT });

        await InterpretAndWaitAsync(server, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT, 3 /* START */, 0x01,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(encryption.IsEncrypting).IsTrue();

        await InterpretAndWaitAsync(server, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT, 4 /* END */,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(endedCount).IsEqualTo(1);
        await Assert.That(encryption.IsEncrypting).IsFalse();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerAcceptsClientWontEncrypt()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.ENCRYPT });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientAcceptsServerDontEncrypt()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EncryptionProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.ENCRYPT });

        await client.DisposeAsync();
    }
}
