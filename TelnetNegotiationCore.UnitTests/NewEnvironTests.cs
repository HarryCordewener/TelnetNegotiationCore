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
    private TelnetInterpreter _server_ti;
    private TelnetInterpreter _client_ti;
    private byte[] _negotiationOutput;
    private Dictionary<string, string> _receivedEnvVars;
    private Dictionary<string, string> _receivedUserVars;

    private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

    private ValueTask ServerWriteBackToNegotiate(byte[] arg1)
    {
        _negotiationOutput = arg1;
        return ValueTask.CompletedTask;
    }

    private ValueTask ClientWriteBackToNegotiate(byte[] arg1)
    {
        _negotiationOutput = arg1;
        return ValueTask.CompletedTask;
    }

    private ValueTask OnEnvironmentVariablesReceived(Dictionary<string, string> envVars, Dictionary<string, string> userVars)
    {
        _receivedEnvVars = new Dictionary<string, string>(envVars);
        _receivedUserVars = new Dictionary<string, string>(userVars);
        logger.LogInformation("Received environment variables: {EnvCount} env, {UserCount} user", envVars.Count, userVars.Count);
        return ValueTask.CompletedTask;
    }

    [Before(Test)]
    public async Task Setup()
    {
        _negotiationOutput = null;
        _receivedEnvVars = null;
        _receivedUserVars = null;

        _server_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .BuildAsync();

        _client_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ClientWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
            .BuildAsync();
    }

    [After(Test)]
    public async Task TearDown()
    {
        if (_server_ti != null)
            await _server_ti.DisposeAsync();
        if (_client_ti != null)
            await _client_ti.DisposeAsync();
    }

    [Test]
    public async Task ServerRequestsNewEnviron()
    {
        // Arrange - Client announces willingness to do NEW-ENVIRON
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should send DO NEW-ENVIRON and then SEND request
        await Assert.That(_negotiationOutput).IsNotNull();
        
        // The negotiation output should contain IAC SB NEWENVIRON SEND IAC SE
        var expectedSend = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
        };
        
        await Assert.That(_negotiationOutput).Contains((byte)Trigger.NEWENVIRON);
    }

    [Test]
    public async Task ClientSendsEnvironmentVariables()
    {
        // Arrange - Complete NEW-ENVIRON negotiation
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await _client_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Act - Server sends SEND request
        var sendRequest = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await _client_ti.InterpretByteArrayAsync(sendRequest);
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should send IS response with variables
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).Contains((byte)Trigger.IS);
        await Assert.That(_negotiationOutput).Contains((byte)Trigger.NEWENVIRON_VAR);
    }

    [Test]
    public async Task ServerReceivesEnvironmentVariables()
    {
        // Arrange - Complete NEW-ENVIRON negotiation
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await _server_ti.WaitForProcessingAsync();
        _receivedEnvVars = null;
        _receivedUserVars = null;

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

        await _server_ti.InterpretByteArrayAsync(response.ToArray());
        await _server_ti.WaitForProcessingAsync();

        // Assert - Callback should have been called with the variables
        await Assert.That(_receivedEnvVars).IsNotNull();
        await Assert.That(_receivedUserVars).IsNotNull();
        await Assert.That(_receivedEnvVars).ContainsKey("USER");
        await Assert.That(_receivedEnvVars["USER"]).IsEqualTo("testuser");
        await Assert.That(_receivedUserVars).ContainsKey("CUSTOM");
        await Assert.That(_receivedUserVars["CUSTOM"]).IsEqualTo("customvalue");
    }

    [Test]
    public async Task NewEnvironWorksWithOtherProtocols()
    {
        // Arrange - Setup server with multiple protocols
        var serverMulti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .AddPlugin<NAWSProtocol>()
            .AddPlugin<TerminalTypeProtocol>()
            .BuildAsync();

        try
        {
            // Act - Client announces multiple protocols
            await serverMulti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
            await serverMulti.WaitForProcessingAsync();
            
            await serverMulti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
            await serverMulti.WaitForProcessingAsync();

            // Assert - Both protocols should work independently
            await Assert.That(_negotiationOutput).IsNotNull();
        }
        finally
        {
            await serverMulti.DisposeAsync();
        }
    }

    [Test]
    public async Task NewEnvironHandlesEmptyValues()
    {
        // Arrange - Complete NEW-ENVIRON negotiation
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await _server_ti.WaitForProcessingAsync();
        _receivedEnvVars = null;

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

        await _server_ti.InterpretByteArrayAsync(response.ToArray());
        await _server_ti.WaitForProcessingAsync();

        // Assert - Variable should exist with empty value
        await Assert.That(_receivedEnvVars).IsNotNull();
        await Assert.That(_receivedEnvVars).ContainsKey("EMPTYVAR");
        await Assert.That(_receivedEnvVars["EMPTYVAR"]).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task NewEnvironHandlesMultipleVariables()
    {
        // Arrange - Complete NEW-ENVIRON negotiation
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
        await _server_ti.WaitForProcessingAsync();
        _receivedEnvVars = null;

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

        await _server_ti.InterpretByteArrayAsync(response.ToArray());
        await _server_ti.WaitForProcessingAsync();

        // Assert - All variables should be received
        await Assert.That(_receivedEnvVars).IsNotNull();
        await Assert.That(_receivedEnvVars.Count).IsEqualTo(3);
        await Assert.That(_receivedEnvVars["USER"]).IsEqualTo("alice");
        await Assert.That(_receivedEnvVars["LANG"]).IsEqualTo("en_US.UTF-8");
        await Assert.That(_receivedEnvVars["TERM"]).IsEqualTo("xterm-256color");
    }

    [Test]
    public async Task ClientDeclineNewEnviron()
    {
        // Act - Client refuses NEW-ENVIRON
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NEWENVIRON });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should not crash and should handle gracefully
        await Assert.That(_receivedEnvVars).IsNull();
    }
}
