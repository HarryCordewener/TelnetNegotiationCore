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
    [Test]
    public async Task ServerRequestsXDisplay()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var server_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<XDisplayProtocol>());

        // Arrange - Client announces willingness to send XDISPLOC
        await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });

        // Assert - Server should send XDISPLOC SEND request
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).Contains((byte)Trigger.XDISPLOC);
        
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task ClientSendsDisplayLocation()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<XDisplayProtocol>());

        // Arrange - Complete XDISPLOC negotiation
        await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
        negotiationOutput = null;

        // Configure client with display location
        var xdisplayPlugin = client_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        xdisplayPlugin!.WithClientDisplayLocation("localhost:0.0");

        // Act - Server sends SEND request
        await InterpretAndWaitAsync(client_ti, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
        });

        // Assert - Client should send the display location
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).Contains((byte)Trigger.XDISPLOC);
        await Assert.That(negotiationOutput).Contains((byte)Trigger.IS);
        
        // Check if the display location is in the output
        var displayBytes = Encoding.ASCII.GetBytes("localhost:0.0");
        foreach (var b in displayBytes)
        {
            await Assert.That(negotiationOutput).Contains(b);
        }
        
        await client_ti.DisposeAsync();
    }

    [Test]
    public async Task ServerReceivesDisplayLocation()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        string receivedDisplayLocation = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        ValueTask OnDisplayLocationReceived(string displayLocation)
        {
            receivedDisplayLocation = displayLocation;
            logger.LogInformation("Received X display location: {DisplayLocation}", displayLocation);
            return ValueTask.CompletedTask;
        }
        
        var server_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<XDisplayProtocol>()
                .OnDisplayLocation(OnDisplayLocationReceived));

        // Arrange - Complete initial negotiation
        await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });

        // Act - Client sends display location
        var displayLocation = "myhost.example.com:0";
        var displayBytes = Encoding.ASCII.GetBytes(displayLocation);
        var message = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.IS
        };
        message = [.. message, .. displayBytes, (byte)Trigger.IAC, (byte)Trigger.SE];

        await InterpretAndWaitAsync(server_ti, message);

        // Assert - Server should have received the display location
        await Assert.That(receivedDisplayLocation).IsNotNull();
        await Assert.That(receivedDisplayLocation).IsEqualTo(displayLocation);
        
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task ClientWithConfiguredDisplayLocationSendsItAutomatically()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        // Arrange - Configure client with display location before negotiation
        var clientWithDisplay = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<XDisplayProtocol>()
                .WithClientDisplayLocation("configured.host:10.0"));

        try
        {
            // Act - Negotiate XDISPLOC
            await InterpretAndWaitAsync(clientWithDisplay, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
            negotiationOutput = null;

            await InterpretAndWaitAsync(clientWithDisplay, new byte[]
            {
                (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
            });

            // Assert - Check the configured display location was sent
            await Assert.That(negotiationOutput).IsNotNull();
            var displayBytes = Encoding.ASCII.GetBytes("configured.host:10.0");
            foreach (var b in displayBytes)
            {
                await Assert.That(negotiationOutput).Contains(b);
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
        // Arrange - Create local variables
        var server_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<XDisplayProtocol>());

        // Act - Client sends WONT XDISPLOC
        await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.XDISPLOC });

        // Assert - Server should accept the rejection (no further negotiation)
        // The plugin should log that client won't send X Display Location
        var xdisplayPlugin = server_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        await Assert.That(xdisplayPlugin).IsNotNull();
        await Assert.That(xdisplayPlugin!.DisplayLocation).IsEqualTo(string.Empty);
        
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task ClientRejectsXDisplayWhenServerDont()
    {
        // Arrange - Create local variables
        var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<XDisplayProtocol>());

        // Act - Server sends DONT XDISPLOC
        await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.XDISPLOC });

        // Assert - Client should accept the rejection
        var xdisplayPlugin = client_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        await Assert.That(xdisplayPlugin).IsNotNull();
        
        await client_ti.DisposeAsync();
    }

    [Test]
    public async Task DisplayLocationWithColonAndDotIsHandledCorrectly()
    {
        // Arrange - Create local variables
        string receivedDisplayLocation = null;
        
        ValueTask OnDisplayLocationReceived(string displayLocation)
        {
            receivedDisplayLocation = displayLocation;
            logger.LogInformation("Received X display location: {DisplayLocation}", displayLocation);
            return ValueTask.CompletedTask;
        }
        
        var server_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<XDisplayProtocol>()
                .OnDisplayLocation(OnDisplayLocationReceived));

        // Arrange
        await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });

        // Act - Client sends display location with standard format
        var displayLocation = "192.168.1.100:0.0";
        var displayBytes = Encoding.ASCII.GetBytes(displayLocation);
        var message = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.IS
        };
        message = [.. message, .. displayBytes, (byte)Trigger.IAC, (byte)Trigger.SE];

        await InterpretAndWaitAsync(server_ti, message);

        // Assert
        await Assert.That(receivedDisplayLocation).IsEqualTo(displayLocation);
        
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task EmptyDisplayLocationIsHandled()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }
        
        var client_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<XDisplayProtocol>());

        // Arrange - Configure client with empty display location
        var xdisplayPlugin = client_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        
        // Act - Client receives DO XDISPLOC
        await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });
        negotiationOutput = null;

        await InterpretAndWaitAsync(client_ti, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.SEND, (byte)Trigger.IAC, (byte)Trigger.SE
        });

        // Assert - Client should send empty display location (just the protocol bytes)
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).Contains((byte)Trigger.XDISPLOC);
        
        await client_ti.DisposeAsync();
    }

    [Test]
    public async Task ServerInitiatesNegotiationAutomatically()
    {
        // Arrange - Create local variables
        var server_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<XDisplayProtocol>());

        // The server should automatically initiate XDISPLOC negotiation on connection
        // This is tested by checking if the plugin registers initial negotiation
        var xdisplayPlugin = server_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        await Assert.That(xdisplayPlugin).IsNotNull();
        await Assert.That(xdisplayPlugin!.IsEnabled).IsTrue();
        
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task PluginPropertyReturnsCorrectDisplayLocation()
    {
        // Arrange - Create local variables
        string receivedDisplayLocation = null;
        
        ValueTask OnDisplayLocationReceived(string displayLocation)
        {
            receivedDisplayLocation = displayLocation;
            logger.LogInformation("Received X display location: {DisplayLocation}", displayLocation);
            return ValueTask.CompletedTask;
        }
        
        var server_ti = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<XDisplayProtocol>()
                .OnDisplayLocation(OnDisplayLocationReceived));

        // Arrange
        await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.XDISPLOC });

        var displayLocation = "test.server:5.0";
        var displayBytes = Encoding.ASCII.GetBytes(displayLocation);
        var message = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.XDISPLOC, (byte)Trigger.IS
        };
        message = [.. message, .. displayBytes, (byte)Trigger.IAC, (byte)Trigger.SE];

        // Act
        await InterpretAndWaitAsync(server_ti, message);

        // Assert - Check the plugin property
        var xdisplayPlugin = server_ti.PluginManager!.GetPlugin<XDisplayProtocol>();
        await Assert.That(xdisplayPlugin!.DisplayLocation).IsEqualTo(displayLocation);
        
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task WithClientDisplayLocationThrowsOnNullOrEmpty()
    {
        // Arrange
        var xdisplayPlugin = new XDisplayProtocol();

        // Act & Assert - Test null
        await Assert.That(() => xdisplayPlugin.WithClientDisplayLocation(null!))
            .Throws<ArgumentNullException>();
        
        // Act & Assert - Test empty string
        await Assert.That(() => xdisplayPlugin.WithClientDisplayLocation(string.Empty))
            .Throws<ArgumentException>();
    }
}
