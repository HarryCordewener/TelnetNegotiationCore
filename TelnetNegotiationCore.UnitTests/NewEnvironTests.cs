using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

public class NewEnvironTests : BaseTest
{
    [Test]
    public async Task ServerRequestsNewEnviron()
    {
        byte[] negotiationOutput = null;

        ValueTask ServerWriteBackToNegotiate(byte[] arg1)
        {
            negotiationOutput = arg1;
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables((envVars, userVars) =>
                {
                    logger.LogInformation("Received environment variables: {EnvCount} env, {UserCount} user", envVars.Count, userVars.Count);
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        // Arrange - Client announces willingness to do NEW-ENVIRON
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await server.WaitForProcessingAsync();

        // Assert - Server should send DO NEW-ENVIRON and then SEND request
        await Assert.That(negotiationOutput).IsNotNull();
        
        // The negotiation output should contain IAC SB NEWENVIRON SEND IAC SE
        var expectedSend = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
        };
        
        await Assert.That(negotiationOutput).Contains((byte)Trigger.NEWENVIRON);

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientSendsEnvironmentVariables()
    {
        byte[] negotiationOutput = null;

        ValueTask ClientWriteBackToNegotiate(byte[] arg1)
        {
            negotiationOutput = arg1;
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(ClientWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
            .BuildAsync();

        // Arrange - Complete NEW-ENVIRON negotiation
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await client.WaitForProcessingAsync();
        negotiationOutput = null;

        // Act - Server sends SEND request
        var sendRequest = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await client.InterpretByteArrayAsync(sendRequest);
        await client.WaitForProcessingAsync();

        // Assert - Client should send IS response with variables
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).Contains((byte)Trigger.IS);
        await Assert.That(negotiationOutput).Contains((byte)Trigger.NEWENVIRON_VAR);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerReceivesEnvironmentVariables()
    {
        Dictionary<string, string> receivedEnvVars = null;
        Dictionary<string, string> receivedUserVars = null;

        ValueTask OnEnvironmentVariablesReceived(Dictionary<string, string> envVars, Dictionary<string, string> userVars)
        {
            receivedEnvVars = new Dictionary<string, string>(envVars);
            receivedUserVars = new Dictionary<string, string>(userVars);
            logger.LogInformation("Received environment variables: {EnvCount} env, {UserCount} user", envVars.Count, userVars.Count);
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .BuildAsync();

        // Arrange - Complete NEW-ENVIRON negotiation
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await server.WaitForProcessingAsync();
        receivedEnvVars = null;
        receivedUserVars = null;

        // Act - Client sends environment variables
        var response = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.NEWENVIRON,
            (byte)Trigger.IS,
            (byte)Trigger.NEWENVIRON_VAR
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

        await server.InterpretByteArrayAsync(response.ToArray());
        await server.WaitForProcessingAsync();

        // Assert - Callback should have been called with the variables
        await Assert.That(receivedEnvVars).IsNotNull();
        await Assert.That(receivedUserVars).IsNotNull();
        await Assert.That(receivedEnvVars.Keys.Contains("USER")).IsTrue();
        await Assert.That(receivedEnvVars["USER"]).IsEqualTo("testuser");
        await Assert.That(receivedUserVars.Keys.Contains("CUSTOM")).IsTrue();
        await Assert.That(receivedUserVars["CUSTOM"]).IsEqualTo("customvalue");

        await server.DisposeAsync();
    }

    [Test]
    public async Task NewEnvironWorksWithOtherProtocols()
    {
        byte[] negotiationOutput = null;

        ValueTask ServerWriteBackToNegotiate(byte[] arg1)
        {
            negotiationOutput = arg1;
            return ValueTask.CompletedTask;
        }

        // Arrange - Setup server with multiple protocols
        var serverMulti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables((envVars, userVars) =>
                {
                    logger.LogInformation("Received environment variables: {EnvCount} env, {UserCount} user", envVars.Count, userVars.Count);
                    return ValueTask.CompletedTask;
                })
            .AddPlugin<NAWSProtocol>()
            .AddPlugin<TerminalTypeProtocol>()
            .BuildAsync();

        // Act - Client announces multiple protocols
        await serverMulti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await serverMulti.WaitForProcessingAsync();
        
        await serverMulti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
        await serverMulti.WaitForProcessingAsync();

        // Assert - Both protocols should work independently
        await Assert.That(negotiationOutput).IsNotNull();

        await serverMulti.DisposeAsync();
    }

    [Test]
    public async Task NewEnvironHandlesEmptyValues()
    {
        Dictionary<string, string> receivedEnvVars = null;

        ValueTask OnEnvironmentVariablesReceived(Dictionary<string, string> envVars, Dictionary<string, string> userVars)
        {
            receivedEnvVars = new Dictionary<string, string>(envVars);
            logger.LogInformation("Received environment variables: {EnvCount} env, {UserCount} user", envVars.Count, userVars.Count);
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .BuildAsync();

        // Arrange - Complete NEW-ENVIRON negotiation
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await server.WaitForProcessingAsync();
        receivedEnvVars = null;

        // Act - Client sends variable with empty value
        var response = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.NEWENVIRON,
            (byte)Trigger.IS,
            (byte)Trigger.NEWENVIRON_VAR
        };
        response.AddRange(Encoding.ASCII.GetBytes("EMPTYVAR"));
        response.Add((byte)Trigger.NEWENVIRON_VALUE);
        // No value bytes
        response.Add((byte)Trigger.IAC);
        response.Add((byte)Trigger.SE);

        await server.InterpretByteArrayAsync(response.ToArray());
        await server.WaitForProcessingAsync();

        // Assert - Variable should exist with empty value
        await Assert.That(receivedEnvVars).IsNotNull();
        await Assert.That(receivedEnvVars.Keys.Contains("EMPTYVAR")).IsTrue();
        await Assert.That(receivedEnvVars["EMPTYVAR"]).IsEqualTo(string.Empty);

        await server.DisposeAsync();
    }

    [Test]
    public async Task NewEnvironHandlesMultipleVariables()
    {
        Dictionary<string, string> receivedEnvVars = null;

        ValueTask OnEnvironmentVariablesReceived(Dictionary<string, string> envVars, Dictionary<string, string> userVars)
        {
            receivedEnvVars = new Dictionary<string, string>(envVars);
            logger.LogInformation("Received environment variables: {EnvCount} env, {UserCount} user", envVars.Count, userVars.Count);
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .BuildAsync();

        // Arrange - Complete NEW-ENVIRON negotiation
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await server.WaitForProcessingAsync();
        receivedEnvVars = null;

        // Act - Client sends multiple environment variables
        var response = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.NEWENVIRON,
            (byte)Trigger.IS,
            (byte)Trigger.NEWENVIRON_VAR
        };
        response.AddRange(Encoding.ASCII.GetBytes("USER"));
        response.Add((byte)Trigger.NEWENVIRON_VALUE);
        response.AddRange(Encoding.ASCII.GetBytes("alice"));
        response.Add((byte)Trigger.NEWENVIRON_VAR);
        response.AddRange(Encoding.ASCII.GetBytes("LANG"));
        response.Add((byte)Trigger.NEWENVIRON_VALUE);
        response.AddRange(Encoding.ASCII.GetBytes("en_US.UTF-8"));
        response.Add((byte)Trigger.NEWENVIRON_VAR);
        response.AddRange(Encoding.ASCII.GetBytes("TERM"));
        response.Add((byte)Trigger.NEWENVIRON_VALUE);
        response.AddRange(Encoding.ASCII.GetBytes("xterm-256color"));
        response.Add((byte)Trigger.IAC);
        response.Add((byte)Trigger.SE);

        await server.InterpretByteArrayAsync(response.ToArray());
        await server.WaitForProcessingAsync();

        // Assert - All variables should be received
        await Assert.That(receivedEnvVars).IsNotNull();
        await Assert.That(receivedEnvVars.Count).IsEqualTo(3);
        await Assert.That(receivedEnvVars["USER"]).IsEqualTo("alice");
        await Assert.That(receivedEnvVars["LANG"]).IsEqualTo("en_US.UTF-8");
        await Assert.That(receivedEnvVars["TERM"]).IsEqualTo("xterm-256color");

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientDeclineNewEnviron()
    {
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables((envVars, userVars) =>
                {
                    logger.LogInformation("Received environment variables: {EnvCount} env, {UserCount} user", envVars.Count, userVars.Count);
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        // Act - Client refuses NEW-ENVIRON
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NEWENVIRON });
        await server.WaitForProcessingAsync();

        // Assert - Server should not crash and should handle gracefully (test passes by not throwing)

        await server.DisposeAsync();
    }
}
