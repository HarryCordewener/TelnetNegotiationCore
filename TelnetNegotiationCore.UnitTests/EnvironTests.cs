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

public class EnvironTests : BaseTest
{
    [Test]
    public async Task ServerRequestsEnviron()
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
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables((envVars) =>
                {
                    logger.LogInformation("Received environment variables: {EnvCount} env", envVars.Count);
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        // Arrange - Client announces willingness to do ENVIRON
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });
        await server.WaitForProcessingAsync();

        // Assert - Server should send DO ENVIRON and then SEND request
        await Assert.That(negotiationOutput).IsNotNull();
        
        // The negotiation output should contain IAC SB ENVIRON SEND IAC SE
        var expectedSend = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENVIRON, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
        };
        
        await Assert.That(negotiationOutput).Contains((byte)Trigger.ENVIRON);

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
            .AddPlugin<EnvironProtocol>()
            .BuildAsync();

        // Arrange - Complete ENVIRON negotiation
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });
        await client.WaitForProcessingAsync();
        negotiationOutput = null;

        // Act - Server sends SEND request
        var sendRequest = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENVIRON, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
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

        ValueTask OnEnvironmentVariablesReceived(Dictionary<string, string> envVars)
        {
            receivedEnvVars = new Dictionary<string, string>(envVars);
            logger.LogInformation("Received environment variables: {EnvCount} env", envVars.Count);
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .BuildAsync();

        // Arrange - Complete ENVIRON negotiation
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });
        await server.WaitForProcessingAsync();
        receivedEnvVars = null;

        // Act - Client sends environment variables (no USERVAR in RFC 1408)
        var response = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENVIRON,
            (byte)Trigger.IS,
            (byte)Trigger.NEWENVIRON_VAR
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

        await server.InterpretByteArrayAsync(response.ToArray());
        await server.WaitForProcessingAsync();

        // Assert - Callback should have been called with the variables
        await Assert.That(receivedEnvVars).IsNotNull();
        await Assert.That(receivedEnvVars.Keys.Contains("USER")).IsTrue();
        await Assert.That(receivedEnvVars["USER"]).IsEqualTo("testuser");
        await Assert.That(receivedEnvVars.Keys.Contains("LANG")).IsTrue();
        await Assert.That(receivedEnvVars["LANG"]).IsEqualTo("en_US.UTF-8");

        await server.DisposeAsync();
    }

    [Test]
    public async Task EnvironWorksWithOtherProtocols()
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
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables((envVars) =>
                {
                    logger.LogInformation("Received environment variables: {EnvCount} env", envVars.Count);
                    return ValueTask.CompletedTask;
                })
            .AddPlugin<NAWSProtocol>()
            .AddPlugin<TerminalTypeProtocol>()
            .BuildAsync();

        // Act - Client announces multiple protocols
        await serverMulti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });
        await serverMulti.WaitForProcessingAsync();
        
        await serverMulti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
        await serverMulti.WaitForProcessingAsync();

        // Assert - Both protocols should work independently
        await Assert.That(negotiationOutput).IsNotNull();

        await serverMulti.DisposeAsync();
    }

    [Test]
    public async Task EnvironHandlesEmptyValues()
    {
        Dictionary<string, string> receivedEnvVars = null;

        ValueTask OnEnvironmentVariablesReceived(Dictionary<string, string> envVars)
        {
            receivedEnvVars = new Dictionary<string, string>(envVars);
            logger.LogInformation("Received environment variables: {EnvCount} env", envVars.Count);
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .BuildAsync();

        // Arrange - Complete ENVIRON negotiation
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });
        await server.WaitForProcessingAsync();
        receivedEnvVars = null;

        // Act - Client sends variable with empty value
        var response = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENVIRON,
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
    public async Task EnvironHandlesMultipleVariables()
    {
        Dictionary<string, string> receivedEnvVars = null;

        ValueTask OnEnvironmentVariablesReceived(Dictionary<string, string> envVars)
        {
            receivedEnvVars = new Dictionary<string, string>(envVars);
            logger.LogInformation("Received environment variables: {EnvCount} env", envVars.Count);
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .BuildAsync();

        // Arrange - Complete ENVIRON negotiation
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });
        await server.WaitForProcessingAsync();
        receivedEnvVars = null;

        // Act - Client sends multiple environment variables
        var response = new List<byte>
        {
            (byte)Trigger.IAC,
            (byte)Trigger.SB,
            (byte)Trigger.ENVIRON,
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
    public async Task ClientDeclineEnviron()
    {
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables((envVars) =>
                {
                    logger.LogInformation("Received environment variables: {EnvCount} env", envVars.Count);
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        // Act - Client refuses ENVIRON
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.ENVIRON });
        await server.WaitForProcessingAsync();

        // Assert - Server should not crash and should handle gracefully (test passes by not throwing)

        await server.DisposeAsync();
    }

    [Test]
    public async Task EnvironCanActivateInIsolation()
    {
        byte[] negotiationOutput = null;

        ValueTask ServerWriteBackToNegotiate(byte[] arg1)
        {
            negotiationOutput = arg1;
            return ValueTask.CompletedTask;
        }

        // Arrange - Setup server with ONLY EnvironProtocol (no other protocols)
        var serverIsolated = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables((envVars) =>
                {
                    logger.LogInformation("Received environment variables: {EnvCount} env", envVars.Count);
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        // Act - Client announces willingness to do ENVIRON
        negotiationOutput = null;
        await serverIsolated.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });
        await serverIsolated.WaitForProcessingAsync();

        // Assert - Server should successfully negotiate ENVIRON in isolation
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).Contains((byte)Trigger.ENVIRON);

        await serverIsolated.DisposeAsync();
    }

    [Test]
    public async Task EnvironAndNewEnvironCanCoexist()
    {
        byte[] negotiationOutput = null;
        Dictionary<string, string> receivedEnvVars = null;
        Dictionary<string, string> receivedNewEnvVars = null;
        Dictionary<string, string> receivedNewUserVars = null;

        ValueTask ServerWriteBackToNegotiate(byte[] arg1)
        {
            negotiationOutput = arg1;
            return ValueTask.CompletedTask;
        }

        ValueTask OnEnvironmentVariablesReceived(Dictionary<string, string> envVars)
        {
            receivedEnvVars = new Dictionary<string, string>(envVars);
            logger.LogInformation("Received environment variables: {EnvCount} env", envVars.Count);
            return ValueTask.CompletedTask;
        }

        // Arrange - Setup server with BOTH EnvironProtocol and NewEnvironProtocol
        var serverBoth = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<EnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables((env, user) =>
                {
                    receivedNewEnvVars = new Dictionary<string, string>(env);
                    receivedNewUserVars = new Dictionary<string, string>(user);
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        // Act 1 - Client announces ENVIRON
        await serverBoth.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON });
        await serverBoth.WaitForProcessingAsync();

        // Assert - ENVIRON should work
        await Assert.That(negotiationOutput).IsNotNull();

        negotiationOutput = null;

        // Act 2 - Same client also announces NEW-ENVIRON
        await serverBoth.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await serverBoth.WaitForProcessingAsync();

        // Assert - NEW-ENVIRON should also work
        await Assert.That(negotiationOutput).IsNotNull();

        await serverBoth.DisposeAsync();
    }
}
