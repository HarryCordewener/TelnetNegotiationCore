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

/// <summary>
/// Tests to ensure NEW-ENVIRON and other protocols work together without conflicts
/// </summary>
public class NewEnvironIntegrationTests : BaseTest
{
    private byte[] _negotiationOutput;
    private Dictionary<string, string> _receivedEnvVars;
    private Dictionary<string, string> _receivedUserVars;
    private int _receivedHeight;
    private int _receivedWidth;
    private List<string> _receivedTerminalTypes = new();

    private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

    private ValueTask ServerWriteBackToNegotiate(byte[] arg1)
    {
        _negotiationOutput = arg1;
        return ValueTask.CompletedTask;
    }

    private ValueTask OnEnvironmentVariablesReceived(Dictionary<string, string> envVars, Dictionary<string, string> userVars)
    {
        _receivedEnvVars = new Dictionary<string, string>(envVars);
        _receivedUserVars = new Dictionary<string, string>(userVars);
        return ValueTask.CompletedTask;
    }

    private ValueTask OnNAWSReceived(int height, int width)
    {
        _receivedHeight = height;
        _receivedWidth = width;
        return ValueTask.CompletedTask;
    }

    [Before(Test)]
    public void Setup()
    {
        _negotiationOutput = null;
        _receivedEnvVars = null;
        _receivedUserVars = null;
        _receivedHeight = 0;
        _receivedWidth = 0;
        _receivedTerminalTypes.Clear();
    }

    [Test]
    public async Task NewEnvironWorksWithNAWS()
    {
        // Arrange - Setup server with both protocols
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .AddPlugin<NAWSProtocol>()
                .OnNAWS(OnNAWSReceived)
            .BuildAsync();

        try
        {
            // Act - Client announces support for both protocols
            await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
            await server.WaitForProcessingAsync();

            await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
            await server.WaitForProcessingAsync();

            // Send NEW-ENVIRON data
            var envResponse = new List<byte>
            {
                (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS,
                (byte)Trigger.NEWENVIRON_VAR
            };
            envResponse.AddRange(Encoding.ASCII.GetBytes("USER"));
            envResponse.Add((byte)Trigger.NEWENVIRON_VALUE);
            envResponse.AddRange(Encoding.ASCII.GetBytes("testuser"));
            envResponse.Add((byte)Trigger.IAC);
            envResponse.Add((byte)Trigger.SE);
            await server.InterpretByteArrayAsync(envResponse.ToArray());
            await server.WaitForProcessingAsync();

            // Send NAWS data (80x24)
            var nawsData = new byte[]
            {
                (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
                0, 80, 0, 24,
                (byte)Trigger.IAC, (byte)Trigger.SE
            };
            await server.InterpretByteArrayAsync(nawsData);
            await server.WaitForProcessingAsync();

            // Assert - Both protocols should have received their data independently
            await Assert.That(_receivedEnvVars).IsNotNull();
            await Assert.That(_receivedEnvVars.ContainsKey("USER")).IsTrue();
            await Assert.That(_receivedEnvVars["USER"]).IsEqualTo("testuser");
            await Assert.That(_receivedWidth).IsEqualTo(80);
            await Assert.That(_receivedHeight).IsEqualTo(24);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task NewEnvironWorksWithTerminalType()
    {
        // Arrange - Setup server with both protocols
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .AddPlugin<TerminalTypeProtocol>()
            .BuildAsync();

        try
        {
            // Act - Client announces support for both protocols
            await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE });
            await server.WaitForProcessingAsync();

            await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
            await server.WaitForProcessingAsync();

            // Send Terminal Type with MTTS including MNES flag (512)
            var ttypeResponse = new List<byte>
            {
                (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS
            };
            ttypeResponse.AddRange(Encoding.ASCII.GetBytes("MTTS 3853")); // Includes MNES flag (512) among other capabilities
            ttypeResponse.Add((byte)Trigger.IAC);
            ttypeResponse.Add((byte)Trigger.SE);
            await server.InterpretByteArrayAsync(ttypeResponse.ToArray());
            await server.WaitForProcessingAsync();

            // Send NEW-ENVIRON data
            var envResponse = new List<byte>
            {
                (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS,
                (byte)Trigger.NEWENVIRON_VAR
            };
            envResponse.AddRange(Encoding.ASCII.GetBytes("TERM"));
            envResponse.Add((byte)Trigger.NEWENVIRON_VALUE);
            envResponse.AddRange(Encoding.ASCII.GetBytes("xterm-256color"));
            envResponse.Add((byte)Trigger.IAC);
            envResponse.Add((byte)Trigger.SE);
            await server.InterpretByteArrayAsync(envResponse.ToArray());
            await server.WaitForProcessingAsync();

            // Assert - Both protocols should work
            await Assert.That(_receivedEnvVars).IsNotNull();
            await Assert.That(_receivedEnvVars.ContainsKey("TERM")).IsTrue();
            
            var ttypePlugin = server.PluginManager!.GetPlugin<TerminalTypeProtocol>();
            await Assert.That(ttypePlugin).IsNotNull();
            await Assert.That(ttypePlugin!.TerminalTypes.Count).IsGreaterThan(0);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task NewEnvironWorksWithAllMUDProtocols()
    {
        // Arrange - Setup server with all default MUD protocols
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .AddPlugin<NAWSProtocol>()
                .OnNAWS(OnNAWSReceived)
            .AddPlugin<TerminalTypeProtocol>()
            .AddPlugin<CharsetProtocol>()
            .AddPlugin<GMCPProtocol>()
            .AddPlugin<MSSPProtocol>()
            .AddPlugin<EORProtocol>()
            .AddPlugin<SuppressGoAheadProtocol>()
            .BuildAsync();

        try
        {
            // Act - Client announces support for multiple protocols
            await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
            await server.WaitForProcessingAsync();

            await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
            await server.WaitForProcessingAsync();

            await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE });
            await server.WaitForProcessingAsync();

            // Send some data for each protocol
            var envResponse = new List<byte>
            {
                (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS,
                (byte)Trigger.NEWENVIRON_VAR
            };
            envResponse.AddRange(Encoding.ASCII.GetBytes("USER"));
            envResponse.Add((byte)Trigger.NEWENVIRON_VALUE);
            envResponse.AddRange(Encoding.ASCII.GetBytes("mudder"));
            envResponse.Add((byte)Trigger.IAC);
            envResponse.Add((byte)Trigger.SE);
            await server.InterpretByteArrayAsync(envResponse.ToArray());
            await server.WaitForProcessingAsync();

            // Assert - NEW-ENVIRON should work alongside all other protocols
            await Assert.That(_receivedEnvVars).IsNotNull();
            await Assert.That(_receivedEnvVars.ContainsKey("USER")).IsTrue();
            await Assert.That(_receivedEnvVars["USER"]).IsEqualTo("mudder");

            // Verify all plugins are enabled
            var newEnvironPlugin = server.PluginManager!.GetPlugin<NewEnvironProtocol>();
            var nawsPlugin = server.PluginManager!.GetPlugin<NAWSProtocol>();
            var ttypePlugin = server.PluginManager!.GetPlugin<TerminalTypeProtocol>();
            
            await Assert.That(newEnvironPlugin).IsNotNull();
            await Assert.That(nawsPlugin).IsNotNull();
            await Assert.That(ttypePlugin).IsNotNull();
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task ClientCanEnableNewEnvironIndependently()
    {
        // Arrange - Setup client with just NEW-ENVIRON
        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
            .BuildAsync();

        try
        {
            _negotiationOutput = null;

            // Act - Server requests NEW-ENVIRON
            await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
            await client.WaitForProcessingAsync();

            // Assert - Client should respond with DO
            await Assert.That(_negotiationOutput).IsNotNull();
            await Assert.That(_negotiationOutput).Contains((byte)Trigger.DO);
            await Assert.That(_negotiationOutput).Contains((byte)Trigger.NEWENVIRON);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task ServerCanEnableNewEnvironIndependently()
    {
        // Arrange - Setup server with just NEW-ENVIRON
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<NewEnvironProtocol>()
                .OnEnvironmentVariables(OnEnvironmentVariablesReceived)
            .BuildAsync();

        try
        {
            _negotiationOutput = null;
            _receivedEnvVars = null;

            // Act - Client agrees to NEW-ENVIRON
            await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON });
            await server.WaitForProcessingAsync();

            // Server should request variables
            await Assert.That(_negotiationOutput).IsNotNull();
            await Assert.That(_negotiationOutput).Contains((byte)Trigger.SEND);

            // Client sends data
            var envResponse = new List<byte>
            {
                (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS,
                (byte)Trigger.NEWENVIRON_VAR
            };
            envResponse.AddRange(Encoding.ASCII.GetBytes("TEST"));
            envResponse.Add((byte)Trigger.NEWENVIRON_VALUE);
            envResponse.AddRange(Encoding.ASCII.GetBytes("standalone"));
            envResponse.Add((byte)Trigger.IAC);
            envResponse.Add((byte)Trigger.SE);
            await server.InterpretByteArrayAsync(envResponse.ToArray());
            await server.WaitForProcessingAsync();

            // Assert - Should work independently
            await Assert.That(_receivedEnvVars).IsNotNull();
            await Assert.That(_receivedEnvVars.ContainsKey("TEST")).IsTrue();
            await Assert.That(_receivedEnvVars["TEST"]).IsEqualTo("standalone");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
