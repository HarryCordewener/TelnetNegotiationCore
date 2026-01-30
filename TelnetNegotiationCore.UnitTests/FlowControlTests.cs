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

public class FlowControlTests : BaseTest
{

    [Test]
    public async Task ClientRespondsWithWillFlowControlToServerDo()
    {
        byte[] negotiationOutput = null;
        bool? flowControlStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureFlowControlStateChanged(bool enabled)
        {
            flowControlStateChanged = enabled;
            logger.LogInformation("Flow control state changed: {Enabled}", enabled);
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(CaptureFlowControlStateChanged)
            .BuildAsync();

        await Task.Delay(100);

        // Act - Client receives DO FLOWCONTROL from server
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();

        // Assert - Client should respond with WILL FLOWCONTROL
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        
        // Flow control should be enabled per RFC 1372
        await Assert.That(flowControlStateChanged).IsNotNull();
        await Assert.That(flowControlStateChanged.Value).IsTrue();

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerAcceptsWillFlowControl()
    {
        byte[] negotiationOutput = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Server sends DO FLOWCONTROL on initialization - clear it
        await server.WaitForProcessingAsync();
        negotiationOutput = null;

        // Act - Server receives WILL FLOWCONTROL from client
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await server.WaitForProcessingAsync();

        // Assert - Server should accept without additional response
        await Assert.That(negotiationOutput).IsNull();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientHandlesDontFlowControl()
    {
        byte[] negotiationOutput = null;
        bool? flowControlStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureFlowControlStateChanged(bool enabled)
        {
            flowControlStateChanged = enabled;
            logger.LogInformation("Flow control state changed: {Enabled}", enabled);
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(CaptureFlowControlStateChanged)
            .BuildAsync();

        await Task.Delay(100);

        // First enable flow control
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();
        flowControlStateChanged = null;
        negotiationOutput = null;

        // Act - Client receives DONT FLOWCONTROL from server
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();

        // Assert - Client should accept the rejection gracefully
        await Assert.That(negotiationOutput).IsNull();
        
        // Flow control should be disabled
        await Assert.That(flowControlStateChanged).IsNotNull();
        await Assert.That(flowControlStateChanged.Value).IsFalse();

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerHandlesWontFlowControl()
    {
        byte[] negotiationOutput = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Server sends DO FLOWCONTROL on initialization - clear it
        await server.WaitForProcessingAsync();
        negotiationOutput = null;

        // Act - Server receives WONT FLOWCONTROL from client
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.FLOWCONTROL });
        await server.WaitForProcessingAsync();

        // Assert - Server should accept the rejection gracefully
        await Assert.That(negotiationOutput).IsNull();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientReceivesFlowControlOff()
    {
        byte[] negotiationOutput = null;
        bool? flowControlStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureFlowControlStateChanged(bool enabled)
        {
            flowControlStateChanged = enabled;
            logger.LogInformation("Flow control state changed: {Enabled}", enabled);
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(CaptureFlowControlStateChanged)
            .BuildAsync();

        await Task.Delay(100);

        // Arrange - Complete FLOWCONTROL negotiation
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();
        flowControlStateChanged = null;

        // Act - Client receives OFF command
        await client.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_OFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();

        // Assert - Flow control should be disabled
        await Assert.That(flowControlStateChanged).IsNotNull();
        await Assert.That(flowControlStateChanged.Value).IsFalse();
        
        var plugin = client.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.IsFlowControlEnabled).IsFalse();

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientReceivesFlowControlOn()
    {
        byte[] negotiationOutput = null;
        bool? flowControlStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureFlowControlStateChanged(bool enabled)
        {
            flowControlStateChanged = enabled;
            logger.LogInformation("Flow control state changed: {Enabled}", enabled);
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(CaptureFlowControlStateChanged)
            .BuildAsync();

        await Task.Delay(100);

        // Arrange - Complete FLOWCONTROL negotiation and turn off flow control
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();
        await client.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_OFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();
        flowControlStateChanged = null;

        // Act - Client receives ON command
        await client.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_ON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();

        // Assert - Flow control should be enabled
        await Assert.That(flowControlStateChanged).IsNotNull();
        await Assert.That(flowControlStateChanged.Value).IsTrue();
        
        var plugin = client.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.IsFlowControlEnabled).IsTrue();

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientReceivesRestartAnyCommand()
    {
        byte[] negotiationOutput = null;
        bool? flowControlStateChanged = null;
        FlowControlProtocol.FlowControlRestartMode? restartModeChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureFlowControlStateChanged(bool enabled)
        {
            flowControlStateChanged = enabled;
            logger.LogInformation("Flow control state changed: {Enabled}", enabled);
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureRestartModeChanged(FlowControlProtocol.FlowControlRestartMode mode)
        {
            restartModeChanged = mode;
            logger.LogInformation("Restart mode changed: {Mode}", mode);
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(CaptureFlowControlStateChanged)
                .OnRestartModeChanged(CaptureRestartModeChanged)
            .BuildAsync();

        await Task.Delay(100);

        // Arrange - Complete FLOWCONTROL negotiation
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();
        restartModeChanged = null;

        // Act - Client receives RESTART-ANY command
        await client.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_ANY,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();

        // Assert - Restart mode should be set to RestartAny
        await Assert.That(restartModeChanged).IsNotNull();
        await Assert.That(restartModeChanged.Value).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartAny);
        
        var plugin = client.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.RestartMode).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartAny);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientReceivesRestartXONCommand()
    {
        byte[] negotiationOutput = null;
        bool? flowControlStateChanged = null;
        FlowControlProtocol.FlowControlRestartMode? restartModeChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureFlowControlStateChanged(bool enabled)
        {
            flowControlStateChanged = enabled;
            logger.LogInformation("Flow control state changed: {Enabled}", enabled);
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureRestartModeChanged(FlowControlProtocol.FlowControlRestartMode mode)
        {
            restartModeChanged = mode;
            logger.LogInformation("Restart mode changed: {Mode}", mode);
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(CaptureFlowControlStateChanged)
                .OnRestartModeChanged(CaptureRestartModeChanged)
            .BuildAsync();

        await Task.Delay(100);

        // Arrange - Complete FLOWCONTROL negotiation
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();
        restartModeChanged = null;

        // Act - Client receives RESTART-XON command
        await client.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_XON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();

        // Assert - Restart mode should be set to RestartXON
        await Assert.That(restartModeChanged).IsNotNull();
        await Assert.That(restartModeChanged.Value).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartXON);
        
        var plugin = client.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.RestartMode).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartXON);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerCanEnableFlowControl()
    {
        byte[] negotiationOutput = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Server sends DO FLOWCONTROL on initialization - clear it
        await server.WaitForProcessingAsync();
        negotiationOutput = null;

        // Arrange - Get the plugin from server
        var plugin = server.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        
        // Complete negotiation (server receives WILL)
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await server.WaitForProcessingAsync();
        negotiationOutput = null;

        // Act - Server sends enable flow control command
        await plugin!.EnableFlowControlAsync();
        await server.WaitForProcessingAsync();

        // Assert - Server should send ON command
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_ON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerCanDisableFlowControl()
    {
        byte[] negotiationOutput = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Server sends DO FLOWCONTROL on initialization - clear it
        await server.WaitForProcessingAsync();
        negotiationOutput = null;

        // Arrange - Get the plugin from server
        var plugin = server.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        
        // Complete negotiation (server receives WILL)
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await server.WaitForProcessingAsync();
        negotiationOutput = null;

        // Act - Server sends disable flow control command
        await plugin!.DisableFlowControlAsync();
        await server.WaitForProcessingAsync();

        // Assert - Server should send OFF command
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_OFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerCanSetRestartAny()
    {
        byte[] negotiationOutput = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Server sends DO FLOWCONTROL on initialization - clear it
        await server.WaitForProcessingAsync();
        negotiationOutput = null;

        // Arrange - Get the plugin from server
        var plugin = server.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        
        // Complete negotiation (server receives WILL)
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await server.WaitForProcessingAsync();
        negotiationOutput = null;

        // Act - Server sends RESTART-ANY command
        await plugin!.SetRestartAnyAsync();
        await server.WaitForProcessingAsync();

        // Assert - Server should send RESTART-ANY command
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_ANY,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerCanSetRestartXON()
    {
        byte[] negotiationOutput = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Server sends DO FLOWCONTROL on initialization - clear it
        await server.WaitForProcessingAsync();
        negotiationOutput = null;

        // Arrange - Get the plugin from server
        var plugin = server.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        
        // Complete negotiation (server receives WILL)
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await server.WaitForProcessingAsync();
        negotiationOutput = null;

        // Act - Server sends RESTART-XON command
        await plugin!.SetRestartXONAsync();
        await server.WaitForProcessingAsync();

        // Assert - Server should send RESTART-XON command
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_XON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });

        await server.DisposeAsync();
    }

    [Test]
    public async Task CompleteNegotiationSequence()
    {
        byte[] negotiationOutput = null;
        bool? flowControlStateChanged = null;
        FlowControlProtocol.FlowControlRestartMode? restartModeChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureFlowControlStateChanged(bool enabled)
        {
            flowControlStateChanged = enabled;
            logger.LogInformation("Flow control state changed: {Enabled}", enabled);
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureRestartModeChanged(FlowControlProtocol.FlowControlRestartMode mode)
        {
            restartModeChanged = mode;
            logger.LogInformation("Restart mode changed: {Mode}", mode);
            return ValueTask.CompletedTask;
        }

        // This test verifies the complete negotiation sequence from start to finish
        var testServer = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Server sends DO FLOWCONTROL on initialization - clear it
        await testServer.WaitForProcessingAsync();
        negotiationOutput = null;

        var testClient = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(CaptureFlowControlStateChanged)
                .OnRestartModeChanged(CaptureRestartModeChanged)
            .BuildAsync();

        await Task.Delay(100);

        // Step 1: Server sends DO FLOWCONTROL (handled in initial negotiation)
        // Step 2: Client receives DO and responds with WILL
        negotiationOutput = null;
        flowControlStateChanged = null;
        await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await testClient.WaitForProcessingAsync();
        
        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await Assert.That(flowControlStateChanged).IsNotNull();
        await Assert.That(flowControlStateChanged.Value).IsTrue();

        // Step 3: Server receives WILL
        negotiationOutput = null;
        await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await testServer.WaitForProcessingAsync();

        // Step 4: Server sends RESTART-ANY command
        restartModeChanged = null;
        var serverPlugin = testServer.PluginManager?.GetPlugin<FlowControlProtocol>();
        await serverPlugin!.SetRestartAnyAsync();
        await testServer.WaitForProcessingAsync();
        
        // Step 5: Client receives and processes RESTART-ANY
        await testClient.InterpretByteArrayAsync(negotiationOutput);
        await testClient.WaitForProcessingAsync();
        
        await Assert.That(restartModeChanged).IsNotNull();
        await Assert.That(restartModeChanged.Value).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartAny);

        // Step 6: Server sends OFF command
        flowControlStateChanged = null;
        await serverPlugin.DisableFlowControlAsync();
        await testServer.WaitForProcessingAsync();
        
        // Step 7: Client receives and processes OFF
        await testClient.InterpretByteArrayAsync(negotiationOutput);
        await testClient.WaitForProcessingAsync();
        
        await Assert.That(flowControlStateChanged).IsNotNull();
        await Assert.That(flowControlStateChanged.Value).IsFalse();

        await testServer.DisposeAsync();
        await testClient.DisposeAsync();
    }

    [Test]
    public async Task PluginIsProperlyRegistered()
    {
        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Server sends DO FLOWCONTROL on initialization - wait for it
        await server.WaitForProcessingAsync();

        // Verify that the Flow Control protocol plugin is properly registered and enabled
        var plugin = server.PluginManager?.GetPlugin<FlowControlProtocol>();
        
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.IsEnabled).IsTrue();
        await Assert.That(plugin.ProtocolName).IsEqualTo("Flow Control (RFC 1372)");

        await server.DisposeAsync();
    }

    [Test]
    public async Task FlowControlStateDefaultsToFalse()
    {
        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Verify the initial state
        var plugin = client.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.IsFlowControlEnabled).IsFalse();

        await client.DisposeAsync();
    }

    [Test]
    public async Task RestartModeDefaultsToSystemDefault()
    {
        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Verify the initial restart mode
        var plugin = client.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.RestartMode).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.SystemDefault);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientEnablesFlowControlOnWillSent()
    {
        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation((data) => ValueTask.CompletedTask)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        await Task.Delay(100);

        // Per RFC 1372, flow control must be enabled when WILL is sent
        var plugin = client.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.IsFlowControlEnabled).IsFalse();

        // Client receives DO FLOWCONTROL
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();

        // Flow control should now be enabled
        await Assert.That(plugin.IsFlowControlEnabled).IsTrue();

        await client.DisposeAsync();
    }

    [Test]
    public async Task MultipleStateChangesWork()
    {
        byte[] negotiationOutput = null;
        bool? flowControlStateChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureFlowControlStateChanged(bool enabled)
        {
            flowControlStateChanged = enabled;
            logger.LogInformation("Flow control state changed: {Enabled}", enabled);
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(CaptureFlowControlStateChanged)
            .BuildAsync();

        await Task.Delay(100);

        // Test toggling flow control state multiple times
        flowControlStateChanged = null;

        // Enable
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();
        await Assert.That(flowControlStateChanged).IsNotNull();
        await Assert.That(flowControlStateChanged.Value).IsTrue();

        // Disable
        flowControlStateChanged = null;
        await client.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_OFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();
        await Assert.That(flowControlStateChanged).IsNotNull();
        await Assert.That(flowControlStateChanged.Value).IsFalse();

        // Enable again
        flowControlStateChanged = null;
        await client.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_ON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();
        await Assert.That(flowControlStateChanged).IsNotNull();
        await Assert.That(flowControlStateChanged.Value).IsTrue();

        await client.DisposeAsync();
    }

    [Test]
    public async Task MultipleRestartModeChangesWork()
    {
        byte[] negotiationOutput = null;
        bool? flowControlStateChanged = null;
        FlowControlProtocol.FlowControlRestartMode? restartModeChanged = null;

        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureFlowControlStateChanged(bool enabled)
        {
            flowControlStateChanged = enabled;
            logger.LogInformation("Flow control state changed: {Enabled}", enabled);
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureRestartModeChanged(FlowControlProtocol.FlowControlRestartMode mode)
        {
            restartModeChanged = mode;
            logger.LogInformation("Restart mode changed: {Mode}", mode);
            return ValueTask.CompletedTask;
        }

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(CaptureFlowControlStateChanged)
                .OnRestartModeChanged(CaptureRestartModeChanged)
            .BuildAsync();

        await Task.Delay(100);

        // Complete negotiation first
        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await client.WaitForProcessingAsync();

        // Set to RESTART-ANY
        restartModeChanged = null;
        await client.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_ANY,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();
        await Assert.That(restartModeChanged).IsNotNull();
        await Assert.That(restartModeChanged.Value).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartAny);

        // Set to RESTART-XON
        restartModeChanged = null;
        await client.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_XON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();
        await Assert.That(restartModeChanged).IsNotNull();
        await Assert.That(restartModeChanged.Value).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartXON);

        await client.DisposeAsync();
    }
}
