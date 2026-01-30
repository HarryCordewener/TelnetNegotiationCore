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

public class EchoTests : BaseTest
{
    [Test]
    public async Task ClientRespondsWithDoEchoToServerWill()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        bool? echoStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act - Client receives WILL ECHO from server
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
        await client.WaitForProcessingAsync();

        // Assert - Client should respond with DO ECHO
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        
        // Echo state should be enabled on client
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsTrue();
        
        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerAcceptsDoEcho()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        bool? echoStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act - Server receives DO ECHO from client
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        await server.WaitForProcessingAsync();

        // Assert - Server should accept without additional response
        await Assert.That(negotiationOutput).IsNull();
        
        // Echo state should be enabled on server
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsTrue();
        
        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientAcceptsWillEcho()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        bool? echoStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act - Client receives WILL ECHO from server
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
        await client.WaitForProcessingAsync();

        // Assert - Client should send DO ECHO
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        
        // Echo state callback should be invoked
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsTrue();
        
        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerHandlesDontEcho()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        bool? echoStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act - Server receives DONT ECHO from client
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.ECHO });
        await server.WaitForProcessingAsync();

        // Assert - Server should accept the rejection gracefully (no error thrown)
        await Assert.That(negotiationOutput).IsNull();
        
        // Echo state should be disabled
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsFalse();
        
        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientHandlesWontEcho()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        bool? echoStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act - Client receives WONT ECHO from server
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.ECHO });
        await client.WaitForProcessingAsync();

        // Assert - Client should accept the rejection gracefully (no error thrown)
        await Assert.That(negotiationOutput).IsNull();
        
        // Echo state should be disabled
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsFalse();
        
        await client.DisposeAsync();
    }

    [Test]
    public async Task EchoNegotiationSequenceComplete()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        bool? echoStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var testClient = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act - Server sends WILL ECHO
        await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
        await testClient.WaitForProcessingAsync();
        
        // Assert
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsTrue();

        await testClient.DisposeAsync();
    }

    [Test]
    public async Task ServerEchoNegotiationWithClient()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        bool? echoStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var testServer = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act - Client sends DO ECHO
        await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        await testServer.WaitForProcessingAsync();
        
        // Assert - Server should accept (no error, negotiation completes, no response sent)
        await Assert.That(negotiationOutput).IsNull();
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsTrue();

        await testServer.DisposeAsync();
    }

    [Test]
    public async Task ClientWillEchoToServer()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        bool? echoStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act - Client receives WILL ECHO
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
        await client.WaitForProcessingAsync();

        // Assert - Client should respond with DO
        await Assert.That(negotiationOutput).IsNotNull();
        var expectedResponse = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO };
        await Assert.That(negotiationOutput).IsEquivalentTo(expectedResponse);
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsTrue();
        
        await client.DisposeAsync();
    }

    [Test]
    public async Task EchoWithDontResponse()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        bool? echoStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act - Server receives DONT ECHO from client
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.ECHO });
        await server.WaitForProcessingAsync();
        
        // Assert - Server should handle DONT gracefully and record that echo is not enabled (no error thrown)
        await Assert.That(negotiationOutput).IsNull();
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsFalse();
        
        await server.DisposeAsync();
    }

    [Test]
    public async Task EchoWithWontResponse()
    {
        // Arrange - Create local variables
        byte[] negotiationOutput = null;
        bool? echoStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act - Client receives WONT ECHO from server
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.ECHO });
        await client.WaitForProcessingAsync();
        
        // Assert - Client should handle WONT gracefully and record that echo is not enabled (no error thrown)
        await Assert.That(negotiationOutput).IsNull();
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsFalse();
        
        await client.DisposeAsync();
    }

    [Test]
    public async Task EchoProtocolPluginIsEnabled()
    {
        // Arrange - Create server instance
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<EchoProtocol>()
            .BuildAsync();
        
        // Act - Get the Echo protocol plugin
        var echoPlugin = server.PluginManager!.GetPlugin<EchoProtocol>();
        
        // Assert - Verify that the Echo protocol plugin is properly registered and enabled
        await Assert.That(echoPlugin).IsNotNull();
        await Assert.That(echoPlugin!.IsEnabled).IsTrue();
        await Assert.That(echoPlugin.ProtocolName).IsEqualTo("Echo");
        
        await server.DisposeAsync();
    }

    [Test]
    public async Task EchoStateToggles()
    {
        // Arrange - Create local variables
        bool? echoStateChanged = null;

        ValueTask CaptureEchoStateChange(bool enabled)
        {
            echoStateChanged = enabled;
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(CaptureEchoStateChange)
            .BuildAsync();

        // Act & Assert - Enable echo
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
        await client.WaitForProcessingAsync();
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsTrue();

        // Act & Assert - Disable echo
        echoStateChanged = null;
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.ECHO });
        await client.WaitForProcessingAsync();
        await Assert.That(echoStateChanged).IsNotNull();
        await Assert.That(echoStateChanged.Value).IsFalse();
        
        await client.DisposeAsync();
    }

    [Test]
    public async Task EchoPluginIsEchoingProperty()
    {
        // Arrange - Create server instance
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<EchoProtocol>()
            .BuildAsync();
        
        var echoPlugin = server.PluginManager!.GetPlugin<EchoProtocol>();
        await Assert.That(echoPlugin).IsNotNull();
        
        // Assert - Initially not echoing
        await Assert.That(echoPlugin!.IsEchoing).IsFalse();

        // Act - Enable echo with DO ECHO
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        await server.WaitForProcessingAsync();
        
        // Assert - Should be echoing
        await Assert.That(echoPlugin.IsEchoing).IsTrue();

        // Act - Disable echo with DONT ECHO
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.ECHO });
        await server.WaitForProcessingAsync();
        
        // Assert - Should not be echoing
        await Assert.That(echoPlugin.IsEchoing).IsFalse();
        
        await server.DisposeAsync();
    }

    [Test]
    public async Task DefaultEchoHandlerEchoesReceivedBytes()
    {
        // Arrange - Create local variables
        var echoedBytes = new System.Collections.Generic.List<byte>();
        
        ValueTask CaptureNegotiation(byte[] bytes)
        {
            // Capture echoed bytes (but not negotiation sequences)
            if (bytes.Length == 1)
            {
                echoedBytes.Add(bytes[0]);
            }
            return ValueTask.CompletedTask;
        }

        var testServer = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .UseDefaultEchoHandler()
            .BuildAsync();

        // Act - Enable echo
        await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        await testServer.WaitForProcessingAsync();

        // Act - Send some test bytes
        byte[] testBytes = new byte[] { (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
        await testServer.InterpretByteArrayAsync(testBytes);
        await testServer.WaitForProcessingAsync();

        // Assert - Verify that the bytes were echoed back
        await Assert.That(echoedBytes.Count).IsEqualTo(5);
        await Assert.That(echoedBytes[0]).IsEqualTo((byte)'H');
        await Assert.That(echoedBytes[1]).IsEqualTo((byte)'e');
        await Assert.That(echoedBytes[2]).IsEqualTo((byte)'l');
        await Assert.That(echoedBytes[3]).IsEqualTo((byte)'l');
        await Assert.That(echoedBytes[4]).IsEqualTo((byte)'o');

        await testServer.DisposeAsync();
    }

    [Test]
    public async Task DefaultEchoHandlerDoesNotEchoWhenDisabled()
    {
        // Arrange - Create local variables
        var echoedBytes = new System.Collections.Generic.List<byte>();
        
        ValueTask CaptureNegotiation(byte[] bytes)
        {
            // Capture echoed bytes
            if (bytes.Length == 1)
            {
                echoedBytes.Add(bytes[0]);
            }
            return ValueTask.CompletedTask;
        }

        var testServer = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>()
                .UseDefaultEchoHandler()
            .BuildAsync();

        // Act - Do NOT enable echo - send bytes directly
        byte[] testBytes = new byte[] { (byte)'H', (byte)'i' };
        await testServer.InterpretByteArrayAsync(testBytes);
        await testServer.WaitForProcessingAsync();

        // Assert - Verify that no bytes were echoed
        await Assert.That(echoedBytes.Count).IsEqualTo(0);

        await testServer.DisposeAsync();
    }
}
