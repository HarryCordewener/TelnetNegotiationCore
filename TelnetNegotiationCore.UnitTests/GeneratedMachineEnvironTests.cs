using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>ENVIRON (RFC 1408) through the generated machine. WILL and DO are each answered the same
/// way regardless of which side receives them, mirrored here from EnvironTests.cs.</summary>
public class GeneratedMachineEnvironTests : BaseTest
{
    [Test]
    public async Task RespondsWithDoOnWill()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables(envVars => ValueTask.CompletedTask));

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ENVIRON });

        await server.DisposeAsync();
    }

    [Test]
    public async Task RequestsVariablesOnDo()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EnvironProtocol>());

        negotiationOutput = null;
        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ENVIRON });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENVIRON, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientSendsVariablesWhenAskedForEverything()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EnvironProtocol>()
                .WithClientEnvironmentVariables(new Dictionary<string, string> { { "LANG", "en_US.UTF-8" } }));

        negotiationOutput = null;
        await InterpretAndWaitAsync(client, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENVIRON, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        var expected = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENVIRON, (byte)Trigger.IS };
        expected.Add((byte)Trigger.NEWENVIRON_VAR);
        expected.AddRange(Encoding.ASCII.GetBytes("LANG"));
        expected.Add((byte)Trigger.NEWENVIRON_VALUE);
        expected.AddRange(Encoding.ASCII.GetBytes("en_US.UTF-8"));
        expected.Add((byte)Trigger.IAC);
        expected.Add((byte)Trigger.SE);

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, expected.ToArray());

        await client.DisposeAsync();
    }

    /// <summary>Mirrors EnvironTests.ServerReceivesEnvironmentVariables.</summary>
    [Test]
    public async Task ServerReceivesEnvironmentVariables()
    {
        Dictionary<string, string> receivedEnvVars = null;
        ValueTask OnEnvironmentVariables(Dictionary<string, string> envVars) { receivedEnvVars = new Dictionary<string, string>(envVars); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariables));

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });

        var response = new List<byte>
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENVIRON, (byte)Trigger.IS,
            (byte)Trigger.NEWENVIRON_VAR,
        };
        response.AddRange(Encoding.ASCII.GetBytes("USER"));
        response.Add((byte)Trigger.NEWENVIRON_VALUE);
        response.AddRange(Encoding.ASCII.GetBytes("testuser"));
        response.Add((byte)Trigger.NEWENVIRON_VAR);
        response.AddRange(Encoding.ASCII.GetBytes("LANG"));
        response.Add((byte)Trigger.NEWENVIRON_VALUE);
        response.AddRange(Encoding.ASCII.GetBytes("en_US.UTF-8"));
        response.Add((byte)Trigger.IAC);
        response.Add((byte)Trigger.SE);

        await InterpretAndWaitAsync(server, response.ToArray());

        await Assert.That(receivedEnvVars).IsNotNull();
        await Assert.That(receivedEnvVars["USER"]).IsEqualTo("testuser");
        await Assert.That(receivedEnvVars["LANG"]).IsEqualTo("en_US.UTF-8");

        await server.DisposeAsync();
    }

    /// <summary>
    /// A server expects IS (the client reporting its variables). A client that sends SEND instead --
    /// its own role's command, not the server's -- used to be processed anyway, since completion routed
    /// on <see cref="Interpreters.TelnetInterpreter.TelnetMode"/> alone: a request for variable names
    /// read as if it were a report of their values. It is now rejected instead of misinterpreted.
    /// </summary>
    [Test]
    public async Task ServerIgnoresSendInsteadOfIs()
    {
        Dictionary<string, string> receivedEnvVars = null;
        ValueTask OnEnvironmentVariables(Dictionary<string, string> envVars) { receivedEnvVars = new Dictionary<string, string>(envVars); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariables));

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });

        var response = new List<byte>
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENVIRON, (byte)Trigger.SEND,
            (byte)Trigger.NEWENVIRON_VAR,
        };
        response.AddRange(Encoding.ASCII.GetBytes("USER"));
        response.Add((byte)Trigger.IAC);
        response.Add((byte)Trigger.SE);

        await InterpretAndWaitAsync(server, response.ToArray());

        await Assert.That(receivedEnvVars).IsNull();

        await server.DisposeAsync();
    }
}
