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
    private TelnetInterpreter _server_ti;
    private TelnetInterpreter _client_ti;
    private byte[] _negotiationOutput;
    private bool? _echoStateChanged;

    private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

    private ValueTask WriteBackToNegotiate(byte[] arg1)
    {
        _negotiationOutput = arg1;
        return ValueTask.CompletedTask;
    }

    private ValueTask EchoStateChanged(bool enabled)
    {
        _echoStateChanged = enabled;
        return ValueTask.CompletedTask;
    }

    [Before(Test)]
    public async Task Setup()
    {
        _negotiationOutput = null;
        _echoStateChanged = null;

        _server_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(EchoStateChanged)
            .BuildAsync();

        _client_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(EchoStateChanged)
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
    public async Task ClientRespondsWithDoEchoToServerWill()
    {
        // Arrange
        _negotiationOutput = null;
        _echoStateChanged = null;

        // Act - Client receives WILL ECHO from server
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should respond with DO ECHO
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        
        // Echo state should be enabled on client
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsTrue();
    }

    [Test]
    public async Task ServerAcceptsDoEcho()
    {
        // Arrange
        _negotiationOutput = null;
        _echoStateChanged = null;

        // Act - Server receives DO ECHO from client
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should accept without additional response
        await Assert.That(_negotiationOutput).IsNull();
        
        // Echo state should be enabled on server
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsTrue();
    }

    [Test]
    public async Task ClientAcceptsWillEcho()
    {
        // Arrange
        _negotiationOutput = null;
        _echoStateChanged = null;

        // Act - Client receives WILL ECHO from server
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should send DO ECHO
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        
        // Echo state callback should be invoked
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsTrue();
    }

    [Test]
    public async Task ServerHandlesDontEcho()
    {
        // Arrange
        _negotiationOutput = null;
        _echoStateChanged = null;

        // Act - Server receives DONT ECHO from client
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.ECHO });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should accept the rejection gracefully (no error thrown)
        await Assert.That(_negotiationOutput).IsNull();
        
        // Echo state should be disabled
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsFalse();
    }

    [Test]
    public async Task ClientHandlesWontEcho()
    {
        // Arrange
        _negotiationOutput = null;
        _echoStateChanged = null;

        // Act - Client receives WONT ECHO from server
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.ECHO });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should accept the rejection gracefully (no error thrown)
        await Assert.That(_negotiationOutput).IsNull();
        
        // Echo state should be disabled
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsFalse();
    }

    [Test]
    public async Task EchoNegotiationSequenceComplete()
    {
        // This test verifies the complete negotiation sequence
        var testClient = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(EchoStateChanged)
            .BuildAsync();

        // Step 1: Server sends WILL ECHO
        _negotiationOutput = null;
        _echoStateChanged = null;
        await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
        await testClient.WaitForProcessingAsync();
        
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsTrue();

        await testClient.DisposeAsync();
    }

    [Test]
    public async Task ServerEchoNegotiationWithClient()
    {
        // This test verifies server-side negotiation
        var testServer = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<EchoProtocol>()
                .OnEchoStateChanged(EchoStateChanged)
            .BuildAsync();

        // Client sends DO ECHO
        _negotiationOutput = null;
        _echoStateChanged = null;
        await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        await testServer.WaitForProcessingAsync();
        
        // Server should accept (no error, negotiation completes, no response sent)
        await Assert.That(_negotiationOutput).IsNull();
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsTrue();

        await testServer.DisposeAsync();
    }

    [Test]
    public async Task ClientWillEchoToServer()
    {
        // Test client receiving WILL ECHO
        _negotiationOutput = null;
        _echoStateChanged = null;

        // Act - Client receives WILL ECHO
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should respond with DO
        await Assert.That(_negotiationOutput).IsNotNull();
        var expectedResponse = new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO };
        await Assert.That(_negotiationOutput).IsEquivalentTo(expectedResponse);
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsTrue();
    }

    [Test]
    public async Task EchoWithDontResponse()
    {
        // Test server handling client's DONT
        _negotiationOutput = null;
        _echoStateChanged = null;

        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.ECHO });
        await _server_ti.WaitForProcessingAsync();
        
        // Server should handle DONT gracefully and record that echo is not enabled (no error thrown)
        await Assert.That(_negotiationOutput).IsNull();
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsFalse();
    }

    [Test]
    public async Task EchoWithWontResponse()
    {
        // Test client handling server's WONT
        _negotiationOutput = null;
        _echoStateChanged = null;

        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.ECHO });
        await _client_ti.WaitForProcessingAsync();
        
        // Client should handle WONT gracefully and record that echo is not enabled (no error thrown)
        await Assert.That(_negotiationOutput).IsNull();
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsFalse();
    }

    [Test]
    public async Task EchoProtocolPluginIsEnabled()
    {
        // Verify that the Echo protocol plugin is properly registered and enabled
        var echoPlugin = _server_ti.PluginManager!.GetPlugin<EchoProtocol>();
        
        await Assert.That(echoPlugin).IsNotNull();
        await Assert.That(echoPlugin!.IsEnabled).IsTrue();
        await Assert.That(echoPlugin.ProtocolName).IsEqualTo("Echo");
    }

    [Test]
    public async Task EchoStateToggles()
    {
        // Test toggling echo state multiple times
        _echoStateChanged = null;

        // Enable echo
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });
        await _client_ti.WaitForProcessingAsync();
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsTrue();

        // Disable echo
        _echoStateChanged = null;
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.ECHO });
        await _client_ti.WaitForProcessingAsync();
        await Assert.That(_echoStateChanged).IsNotNull();
        await Assert.That(_echoStateChanged.Value).IsFalse();
    }

    [Test]
    public async Task EchoPluginIsEchoingProperty()
    {
        // Verify the IsEchoing property reflects state correctly
        var echoPlugin = _server_ti.PluginManager!.GetPlugin<EchoProtocol>();
        await Assert.That(echoPlugin).IsNotNull();
        
        // Initially not echoing
        await Assert.That(echoPlugin!.IsEchoing).IsFalse();

        // After DO ECHO, should be echoing
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        await _server_ti.WaitForProcessingAsync();
        await Assert.That(echoPlugin.IsEchoing).IsTrue();

        // After DONT ECHO, should not be echoing
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.ECHO });
        await _server_ti.WaitForProcessingAsync();
        await Assert.That(echoPlugin.IsEchoing).IsFalse();
    }

    [Test]
    public async Task DefaultEchoHandlerEchoesReceivedBytes()
    {
        // Create a server with default echo handler
        var echoedBytes = new System.Collections.Generic.List<byte>();
        
        var testServer = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(async (bytes) =>
            {
                // Capture echoed bytes (but not negotiation sequences)
                if (bytes.Length == 1)
                {
                    echoedBytes.Add(bytes[0]);
                }
                await ValueTask.CompletedTask;
            })
            .AddPlugin<EchoProtocol>()
                .UseDefaultEchoHandler()
            .BuildAsync();

        // Enable echo
        await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });
        await testServer.WaitForProcessingAsync();

        // Send some test bytes
        byte[] testBytes = new byte[] { (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
        await testServer.InterpretByteArrayAsync(testBytes);
        await testServer.WaitForProcessingAsync();

        // Verify that the bytes were echoed back
        await Assert.That(echoedBytes).HasCount().EqualTo(5);
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
        // Create a server with default echo handler
        var echoedBytes = new System.Collections.Generic.List<byte>();
        
        var testServer = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(async (bytes) =>
            {
                // Capture echoed bytes
                if (bytes.Length == 1)
                {
                    echoedBytes.Add(bytes[0]);
                }
                await ValueTask.CompletedTask;
            })
            .AddPlugin<EchoProtocol>()
                .UseDefaultEchoHandler()
            .BuildAsync();

        // Do NOT enable echo - send bytes directly
        byte[] testBytes = new byte[] { (byte)'H', (byte)'i' };
        await testServer.InterpretByteArrayAsync(testBytes);
        await testServer.WaitForProcessingAsync();

        // Verify that no bytes were echoed
        await Assert.That(echoedBytes).HasCount().EqualTo(0);

        await testServer.DisposeAsync();
    }
}
