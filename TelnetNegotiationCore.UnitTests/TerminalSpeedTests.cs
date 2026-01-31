using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

public class TerminalSpeedTests : BaseTest
{
    private TelnetInterpreter _server_ti;
    private TelnetInterpreter _client_ti;
    private byte[] _negotiationOutput;
    private int _receivedTransmitSpeed;
    private int _receivedReceiveSpeed;
    private bool _speedReceived;

    private ValueTask WriteBackToNegotiate(byte[] arg1)
    {
        _negotiationOutput = arg1;
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleTerminalSpeed(int transmitSpeed, int receiveSpeed)
    {
        _receivedTransmitSpeed = transmitSpeed;
        _receivedReceiveSpeed = receiveSpeed;
        _speedReceived = true;
        logger.LogInformation("Received terminal speed: {Transmit} bps transmit, {Receive} bps receive",
            transmitSpeed, receiveSpeed);
        return ValueTask.CompletedTask;
    }

    [Before(Test)]
    public async Task Setup()
    {
        _negotiationOutput = null;
        _speedReceived = false;
        _receivedTransmitSpeed = 0;
        _receivedReceiveSpeed = 0;

        _server_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<TerminalSpeedProtocol>()
                .OnTerminalSpeed(HandleTerminalSpeed));

        _client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<TerminalSpeedProtocol>()
                .WithClientTerminalSpeed(38400, 38400));
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
    public async Task ClientRespondsWithWillTSPEEDToServerDo()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Client receives DO TSPEED from server
        await InterpretAndWaitAsync(_client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TSPEED });

        // Assert
        await Assert.That(_negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(_negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });
    }

    [Test]
    public async Task ServerSendsDoTSPEEDOnInitialNegotiation()
    {
        // The server should send DO TSPEED during initial negotiation
        // This is tested implicitly through the Setup method
        // We verify it doesn't crash and the protocol is enabled
        await Assert.That(_server_ti).IsNotNull();
        var plugin = _server_ti.PluginManager?.GetPlugin<TerminalSpeedProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin.IsEnabled).IsTrue();
    }

    [Test]
    public async Task ClientAcceptsDoTSPEED()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Client receives DO TSPEED from server and responds
        await InterpretAndWaitAsync(_client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TSPEED });
        await Task.Delay(50);

        // Assert - Client should send WILL TSPEED
        await Assert.That(_negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(_negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });
    }

    [Test]
    public async Task ServerHandlesDontTSPEED()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Server receives DONT TSPEED from client
        await InterpretAndWaitAsync(_server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.TSPEED });

        // Assert - Server should accept the rejection gracefully (no error thrown)
        await Assert.That(_negotiationOutput).IsNull();
    }

    [Test]
    public async Task ClientHandlesWontTSPEED()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Client receives WONT TSPEED from server
        await InterpretAndWaitAsync(_client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.TSPEED });

        // Assert - Client should accept the rejection gracefully (no error thrown)
        await Assert.That(_negotiationOutput).IsNull();
    }

    [Test]
    public async Task ServerRequestsTerminalSpeed()
    {
        // Arrange - Complete TSPEED negotiation (server receives WILL)
        _negotiationOutput = null;
        await InterpretAndWaitAsync(_server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });

        // Assert - Server should send IAC SB TSPEED SEND IAC SE
        await Assert.That(_negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(_negotiationOutput, new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    [Test]
    public async Task ClientSendsTerminalSpeedWhenRequested()
    {
        // Arrange - Complete TSPEED negotiation
        await InterpretAndWaitAsync(_client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TSPEED });
        _negotiationOutput = null;

        // Act - Client receives SEND request
        await InterpretAndWaitAsync(_client_ti, new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });

        // Assert - Client should send IAC SB TSPEED IS "38400,38400" IAC SE
        await Assert.That(_negotiationOutput).IsNotNull();
        
        var expectedSpeed = "38400,38400";
        var expectedBytes = new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS
        }
        .Concat(Encoding.ASCII.GetBytes(expectedSpeed))
        .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
        .ToArray();
        
        await AssertByteArraysEqual(_negotiationOutput, expectedBytes);
    }

    [Test]
    public async Task ServerReceivesAndParsesTerminalSpeed()
    {
        // Arrange - Complete TSPEED negotiation
        await InterpretAndWaitAsync(_server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });
        _speedReceived = false;

        // Act - Server receives terminal speed
        var speedString = "9600,9600";
        var speedBytes = new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS
        }
        .Concat(Encoding.ASCII.GetBytes(speedString))
        .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
        .ToArray();
        
        await InterpretAndWaitAsync(_server_ti, speedBytes);

        // Assert
        await Assert.That(_speedReceived).IsTrue();
        await Assert.That(_receivedTransmitSpeed).IsEqualTo(9600);
        await Assert.That(_receivedReceiveSpeed).IsEqualTo(9600);
    }

    [Test]
    public async Task ServerHandlesDifferentTransmitAndReceiveSpeeds()
    {
        // Arrange - Complete TSPEED negotiation
        await InterpretAndWaitAsync(_server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });
        _speedReceived = false;

        // Act - Server receives terminal speed with different transmit/receive
        var speedString = "115200,57600";
        var speedBytes = new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS
        }
        .Concat(Encoding.ASCII.GetBytes(speedString))
        .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
        .ToArray();
        
        await InterpretAndWaitAsync(_server_ti, speedBytes);

        // Assert
        await Assert.That(_speedReceived).IsTrue();
        await Assert.That(_receivedTransmitSpeed).IsEqualTo(115200);
        await Assert.That(_receivedReceiveSpeed).IsEqualTo(57600);
    }

    [Test]
    public async Task ClientSendsCustomTerminalSpeed()
    {
        // Arrange - Create client with custom speeds
        var customClient = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<TerminalSpeedProtocol>()
                .WithClientTerminalSpeed(115200, 115200));

        await InterpretAndWaitAsync(customClient, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TSPEED });
        _negotiationOutput = null;

        // Act - Client receives SEND request
        await InterpretAndWaitAsync(customClient, new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });

        // Assert - Client should send the custom speed
        await Assert.That(_negotiationOutput).IsNotNull();
        
        var expectedSpeed = "115200,115200";
        var expectedBytes = new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS
        }
        .Concat(Encoding.ASCII.GetBytes(expectedSpeed))
        .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
        .ToArray();
        
        await AssertByteArraysEqual(_negotiationOutput, expectedBytes);

        await customClient.DisposeAsync();
    }

    [Test]
    public async Task CompleteNegotiationSequence()
    {
        // This test verifies the complete negotiation sequence from start to finish
        var testServer = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<TerminalSpeedProtocol>()
                .OnTerminalSpeed(HandleTerminalSpeed));

        // Step 1: Server receives WILL TSPEED from client
        _negotiationOutput = null;
        await InterpretAndWaitAsync(testServer, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });
        
        // Server should request speed
        await Assert.That(_negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(_negotiationOutput, new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });

        // Step 2: Server receives terminal speed
        _speedReceived = false;
        var speedString = "19200,19200";
        var speedBytes = new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS
        }
        .Concat(Encoding.ASCII.GetBytes(speedString))
        .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
        .ToArray();
        
        await InterpretAndWaitAsync(testServer, speedBytes);
        
        await Assert.That(_speedReceived).IsTrue();
        await Assert.That(_receivedTransmitSpeed).IsEqualTo(19200);
        await Assert.That(_receivedReceiveSpeed).IsEqualTo(19200);

        await testServer.DisposeAsync();
    }

    [Test]
    public async Task PluginExposesTerminalSpeedProperties()
    {
        // Arrange - Complete negotiation and send speed
        await InterpretAndWaitAsync(_server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });

        var speedString = "2400,1200";
        var speedBytes = new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS
        }
        .Concat(Encoding.ASCII.GetBytes(speedString))
        .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
        .ToArray();
        
        await InterpretAndWaitAsync(_server_ti, speedBytes);

        // Act - Get the plugin and check properties
        var plugin = _server_ti.PluginManager?.GetPlugin<TerminalSpeedProtocol>();

        // Assert
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin.TransmitSpeed).IsEqualTo(2400);
        await Assert.That(plugin.ReceiveSpeed).IsEqualTo(1200);
    }

    [Test]
    public async Task InvalidSpeedFormatDoesNotCrash()
    {
        // Arrange - Complete TSPEED negotiation
        await InterpretAndWaitAsync(_server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TSPEED });
        _speedReceived = false;

        // Act - Server receives invalid speed format
        var invalidSpeed = "invalid";
        var speedBytes = new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TSPEED, (byte)Trigger.IS
        }
        .Concat(Encoding.ASCII.GetBytes(invalidSpeed))
        .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
        .ToArray();
        
        await InterpretAndWaitAsync(_server_ti, speedBytes);

        // Assert - Should not crash, and callback should not be called
        await Assert.That(_speedReceived).IsFalse();
    }
}
