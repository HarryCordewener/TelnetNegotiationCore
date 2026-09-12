using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>AUTHENTICATION (RFC 2941) through the generated machine. A server only ever configured
/// WILL/WONT for this option, a client only ever configured DO/DONT, mirrored here from
/// AuthenticationTests.cs.</summary>
public class GeneratedMachineAuthenticationTests : BaseTest
{
    [Test]
    public async Task ServerSendsSendSubnegotiationAfterClientWill()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>());

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.AUTHENTICATION });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION, 1 /* SEND */,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientRespondsWithIsNullToServerSend()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<AuthenticationProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.AUTHENTICATION });
        negotiationOutput = null;

        await InterpretAndWaitAsync(client, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION, 1 /* SEND */,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION,
            0 /* IS */, 0 /* NULL */, 0 /* no modifiers */,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await client.DisposeAsync();
    }

    /// <summary>Mirrors AuthenticationTests.ClientCanProvideCustomAuthenticationResponse: the callback
    /// receives the SEND command byte itself as the first byte, the same shape Stateless's capture
    /// produced (the discriminator byte doubled as the first trigger captured into the data state).</summary>
    [Test]
    public async Task ClientCanProvideCustomAuthenticationResponse()
    {
        byte[] receivedRequest = null;
        byte[] negotiationOutput = null;

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(data => { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; })
            .AddPlugin<AuthenticationProtocol>()
                .OnAuthenticationRequest(authTypePairs =>
                {
                    receivedRequest = authTypePairs;
                    return ValueTask.FromResult((byte[])[5, 0, 0x01, 0x02, 0x03]);
                }));

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.AUTHENTICATION });
        negotiationOutput = null;

        await InterpretAndWaitAsync(client, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION,
            1 /* SEND */, 5, 0, 6, 2,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(receivedRequest).IsNotNull();
        await AssertByteArraysEqual(receivedRequest, [1, 5, 0, 6, 2]);

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION,
            0 /* IS */, 5, 0, 0x01, 0x02, 0x03,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await client.DisposeAsync();
    }

    /// <summary>Mirrors AuthenticationTests.ServerCanReceiveAndProcessAuthenticationResponse.</summary>
    [Test]
    public async Task ServerCanReceiveAndProcessAuthenticationResponse()
    {
        byte[] receivedAuthData = null;

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<AuthenticationProtocol>()
                .OnAuthenticationResponse(authData => { receivedAuthData = authData; return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.AUTHENTICATION });

        await InterpretAndWaitAsync(server, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION,
            0 /* IS */, 5, 0, 0x01, 0x02, 0x03,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(receivedAuthData).IsNotNull();
        await AssertByteArraysEqual(receivedAuthData, [0, 5, 0, 0x01, 0x02, 0x03]);

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerRejectsClientWontAuthentication()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<AuthenticationProtocol>());

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.AUTHENTICATION });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientRejectsServerDontAuthentication()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<AuthenticationProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.AUTHENTICATION });

        await client.DisposeAsync();
    }
}
