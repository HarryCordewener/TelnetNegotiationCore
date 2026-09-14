using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>NEW-ENVIRON (RFC 1572) through the generated machine. DO is answered the same way on
/// either side; WILL, IS and SEND differ by mode, mirrored here from NewEnvironTests.cs and
/// NewEnvironSendRequestTests.cs.</summary>
public class GeneratedMachineNewEnvironTests : BaseTest
{
    [Test]
    public async Task ServerRequestsVariablesAfterWill()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables((envVars, userVars) => ValueTask.CompletedTask));

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientSendsVariablesWhenAskedForEverything()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<NewEnvironProtocol>()
                .WithClientEnvironmentVariables(new Dictionary<string, string> { { "WORD_WRAP", "OFF" } }));

        negotiationOutput = null;
        await InterpretAndWaitAsync(client, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        var expected = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS };
        ClientIdentityTests.AppendVariable(expected, "WORD_WRAP", "OFF");
        expected.Add((byte)Trigger.IAC);
        expected.Add((byte)Trigger.SE);

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, expected.ToArray());

        await client.DisposeAsync();
    }

    /// <summary>Mirrors NewEnvironSendRequestTests.OnlyTheRequestedVariablesAreSent.</summary>
    [Test]
    public async Task OnlyTheRequestedVariablesAreSent()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<NewEnvironProtocol>()
                .WithClientEnvironmentVariables(new Dictionary<string, string>
                {
                    { "CHARSET", "UTF-8" },
                    { "WORD_WRAP", "OFF" },
                    { "IPADDRESS", "203.0.113.7" },
                }));

        negotiationOutput = null;
        var request = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND };
        request.Add((byte)Trigger.NEWENVIRON_VAR);
        request.AddRange(Encoding.ASCII.GetBytes("WORD_WRAP"));
        request.Add((byte)Trigger.IAC);
        request.Add((byte)Trigger.SE);
        await InterpretAndWaitAsync(client, request.ToArray());

        var expected = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS };
        ClientIdentityTests.AppendVariable(expected, "WORD_WRAP", "OFF");
        expected.Add((byte)Trigger.IAC);
        expected.Add((byte)Trigger.SE);

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, expected.ToArray());

        await client.DisposeAsync();
    }

    /// <summary>
    /// Regression test: the generated machine's plugin-facing context used to be a second,
    /// freshly-constructed ProtocolContext rather than the one TelnetInterpreterBuilder.BuildAsync
    /// populates via WithClientIdentity before any plugin configures itself -- ProtocolContext's
    /// shared state lives per instance, so a second instance saw an identity-shaped hole where
    /// CLIENT_NAME should have been. See TelnetInterpreter.SharedProtocolContext.
    /// </summary>
    [Test]
    public async Task ClientIdentityReachesTheGeneratedMachine()
    {
        byte[] negotiationOutput = null;

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(data => { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; })
            .WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER"))
            .AddPlugin<NewEnvironProtocol>());

        negotiationOutput = null;
        var request = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND };
        request.Add((byte)Trigger.NEWENVIRON_VAR);
        request.AddRange(Encoding.ASCII.GetBytes("CLIENT_NAME"));
        request.Add((byte)Trigger.IAC);
        request.Add((byte)Trigger.SE);
        await InterpretAndWaitAsync(client, request.ToArray());

        var expected = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS };
        ClientIdentityTests.AppendVariable(expected, "CLIENT_NAME", "MUINDEX-CRAWLER");
        expected.Add((byte)Trigger.IAC);
        expected.Add((byte)Trigger.SE);

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, expected.ToArray());

        await client.DisposeAsync();
    }

    /// <summary>Mirrors NewEnvironTests.ServerReceivesEnvironmentVariables.</summary>
    [Test]
    public async Task ServerReceivesEnvironmentAndUserVariables()
    {
        Dictionary<string, string> receivedEnvVars = null;
        Dictionary<string, string> receivedUserVars = null;
        ValueTask OnEnvironmentVariables(Dictionary<string, string> envVars, Dictionary<string, string> userVars)
        {
            receivedEnvVars = new Dictionary<string, string>(envVars);
            receivedUserVars = new Dictionary<string, string>(userVars);
            return ValueTask.CompletedTask;
        }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariables));

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });

        var response = new List<byte>
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS,
            (byte)Trigger.NEWENVIRON_VAR,
        };
        response.AddRange(Encoding.ASCII.GetBytes("USER"));
        response.Add((byte)Trigger.NEWENVIRON_VALUE);
        response.AddRange(Encoding.ASCII.GetBytes("testuser"));
        response.Add((byte)Trigger.NEWENVIRON_USERVAR);
        response.AddRange(Encoding.ASCII.GetBytes("CUSTOM"));
        response.Add((byte)Trigger.NEWENVIRON_VALUE);
        response.AddRange(Encoding.ASCII.GetBytes("customvalue"));
        response.Add((byte)Trigger.IAC);
        response.Add((byte)Trigger.SE);

        await InterpretAndWaitAsync(server, response.ToArray());

        await Assert.That(receivedEnvVars).IsNotNull();
        await Assert.That(receivedUserVars).IsNotNull();
        await Assert.That(receivedEnvVars["USER"]).IsEqualTo("testuser");
        await Assert.That(receivedUserVars["CUSTOM"]).IsEqualTo("customvalue");

        await server.DisposeAsync();
    }
}
