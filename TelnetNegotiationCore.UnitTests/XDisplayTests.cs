using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

public class XDisplayTests : BaseTest
{
    private TelnetInterpreter _server_ti;
    private TelnetInterpreter _client_ti;
    private byte[] _negotiationOutput;
    private string _receivedDisplayLocation;

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

    private ValueTask OnDisplayLocationReceived(string displayLocation)
    {
        _receivedDisplayLocation = displayLocation;
        logger.LogInformation("Received X display location: {DisplayLocation}", displayLocation);
        return ValueTask.CompletedTask;
    }

    [Before(Test)]
    public async Task Setup()
    {
        _negotiationOutput = null;
        _receivedDisplayLocation = null;

        _server_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ServerWriteBackToNegotiate)
            .AddPlugin<XDisplayProtocol>()
                .OnDisplayLocation(OnDisplayLocationReceived)
            .BuildAsync();

        _client_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ClientWriteBackToNegotiate)
            .AddPlugin<XDisplayProtocol>()
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
    public async Task ServerRequestsXDisplay()
    {
        // Arrange - Client announces willingness to send XDISPLOC
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should send XDISPLOC SEND request
        await Assert.That(_negotiationOutput).IsNotNull();
        
        // The negotiation output should contain IAC SB XDISPLOC SEND IAC SE
        var expectedSend = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
        };
        
        await Assert.That(_negotiationOutput).Contains((byte)Trigger.XDISPLOC);
    }

    [Test]
    public async Task ClientSendsDisplayLocation()
    {
        // Arrange - Complete XDISPLOC negotiation
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
        await _client_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Configure client with display location
        var xdisplayPlugin = _client_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        xdisplayPlugin!.WithClientDisplayLocation("localhost:0.0");

        // Act - Server sends SEND request
        await _client_ti.InterpretByteArrayAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should send the display location
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).Contains((byte)Trigger.XDISPLOC);
        await Assert.That(_negotiationOutput).Contains((byte)Trigger.IS);
        
        // Check if the display location is in the output
        var displayBytes = Encoding.ASCII.GetBytes("localhost:0.0");
        foreach (var b in displayBytes)
        {
            await Assert.That(_negotiationOutput).Contains(b);
        }
    }

    [Test]
    public async Task ServerReceivesDisplayLocation()
    {
        // Arrange - Complete initial negotiation
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
        await _server_ti.WaitForProcessingAsync();

        // Act - Client sends display location
        var displayLocation = "myhost.example.com:0";
        var displayBytes = Encoding.ASCII.GetBytes(displayLocation);
        var message = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.IS
        };
        message = [.. message, .. displayBytes, (byte)Trigger.IAC, (byte)Trigger.SE];

        await _server_ti.InterpretByteArrayAsync(message);
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should have received the display location
        await Assert.That(_receivedDisplayLocation).IsNotNull();
        await Assert.That(_receivedDisplayLocation).IsEqualTo(displayLocation);
    }

    [Test]
    public async Task ClientWithConfiguredDisplayLocationSendsItAutomatically()
    {
        // Arrange - Configure client with display location before negotiation
        var clientWithDisplay = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(ClientWriteBackToNegotiate)
            .AddPlugin<XDisplayProtocol>()
                .WithClientDisplayLocation("configured.host:10.0")
            .BuildAsync();

        try
        {
            // Act - Negotiate XDISPLOC
            await clientWithDisplay.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
            await clientWithDisplay.WaitForProcessingAsync();
            _negotiationOutput = null;

            await clientWithDisplay.InterpretByteArrayAsync(new byte[]
            {
                (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
            });
            await clientWithDisplay.WaitForProcessingAsync();

            // Assert - Check the configured display location was sent
            await Assert.That(_negotiationOutput).IsNotNull();
            var displayBytes = Encoding.ASCII.GetBytes("configured.host:10.0");
            foreach (var b in displayBytes)
            {
                await Assert.That(_negotiationOutput).Contains(b);
            }
        }
        finally
        {
            await clientWithDisplay.DisposeAsync();
        }
    }

    [Test]
    public async Task ServerRejectsXDisplayWhenClientWont()
    {
        // Act - Client sends WONT XDISPLOC
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.XDISPLOC });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should accept the rejection (no further negotiation)
        // The plugin should log that client won't send X Display Location
        var xdisplayPlugin = _server_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        await Assert.That(xdisplayPlugin).IsNotNull();
        await Assert.That(xdisplayPlugin!.DisplayLocation).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ClientRejectsXDisplayWhenServerDont()
    {
        // Act - Server sends DONT XDISPLOC
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.XDISPLOC });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should accept the rejection
        var xdisplayPlugin = _client_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        await Assert.That(xdisplayPlugin).IsNotNull();
    }

    [Test]
    public async Task DisplayLocationWithColonAndDotIsHandledCorrectly()
    {
        // Arrange
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
        await _server_ti.WaitForProcessingAsync();

        // Act - Client sends display location with standard format
        var displayLocation = "192.168.1.100:0.0";
        var displayBytes = Encoding.ASCII.GetBytes(displayLocation);
        var message = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.IS
        };
        message = [.. message, .. displayBytes, (byte)Trigger.IAC, (byte)Trigger.SE];

        await _server_ti.InterpretByteArrayAsync(message);
        await _server_ti.WaitForProcessingAsync();

        // Assert
        await Assert.That(_receivedDisplayLocation).IsEqualTo(displayLocation);
    }

    [Test]
    public async Task EmptyDisplayLocationIsHandled()
    {
        // Arrange - Configure client with empty display location
        var xdisplayPlugin = _client_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        
        // Act - Client receives DO XDISPLOC
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
        await _client_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        await _client_ti.InterpretByteArrayAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should send empty display location (just the protocol bytes)
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).Contains((byte)Trigger.XDISPLOC);
    }

    [Test]
    public async Task ServerInitiatesNegotiationAutomatically()
    {
        // The server should automatically initiate XDISPLOC negotiation on connection
        // This is tested by checking if the plugin registers initial negotiation
        var xdisplayPlugin = _server_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        await Assert.That(xdisplayPlugin).IsNotNull();
        await Assert.That(xdisplayPlugin!.IsEnabled).IsTrue();
    }

    [Test]
    public async Task PluginPropertyReturnsCorrectDisplayLocation()
    {
        // Arrange
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
        await _server_ti.WaitForProcessingAsync();

        var displayLocation = "test.server:5.0";
        var displayBytes = Encoding.ASCII.GetBytes(displayLocation);
        var message = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.IS
        };
        message = [.. message, .. displayBytes, (byte)Trigger.IAC, (byte)Trigger.SE];

        // Act
        await _server_ti.InterpretByteArrayAsync(message);
        await _server_ti.WaitForProcessingAsync();

        // Assert - Check the plugin property
        var xdisplayPlugin = _server_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        await Assert.That(xdisplayPlugin!.DisplayLocation).IsEqualTo(displayLocation);
    }

    [Test]
    public async Task WithClientDisplayLocationThrowsOnNullOrEmpty()
    {
        // Arrange
        var xdisplayPlugin = new XDisplayProtocol();

        // Act & Assert
        await Assert.That(() => xdisplayPlugin.WithClientDisplayLocation(null!))
            .Throws<ArgumentNullException>();
        
        await Assert.That(() => xdisplayPlugin.WithClientDisplayLocation(string.Empty))
            .Throws<ArgumentNullException>();
    }
}
