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
    private TelnetInterpreter _server_ti;
    private TelnetInterpreter _client_ti;
    private byte[] _negotiationOutput;
    private bool? _flowControlStateChanged;
    private FlowControlProtocol.FlowControlRestartMode? _restartModeChanged;

    private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

    private ValueTask WriteBackToNegotiate(byte[] arg1)
    {
        _negotiationOutput = arg1;
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleFlowControlStateChanged(bool enabled)
    {
        _flowControlStateChanged = enabled;
        logger.LogInformation("Flow control state changed: {Enabled}", enabled);
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleRestartModeChanged(FlowControlProtocol.FlowControlRestartMode mode)
    {
        _restartModeChanged = mode;
        logger.LogInformation("Restart mode changed: {Mode}", mode);
        return ValueTask.CompletedTask;
    }

    [Before(Test)]
    public async Task Setup()
    {
        _negotiationOutput = null;
        _flowControlStateChanged = null;
        _restartModeChanged = null;

        _server_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        _client_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(HandleFlowControlStateChanged)
                .OnRestartModeChanged(HandleRestartModeChanged)
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
    public async Task ClientRespondsWithWillFlowControlToServerDo()
    {
        // Arrange
        _negotiationOutput = null;
        _flowControlStateChanged = null;

        // Act - Client receives DO FLOWCONTROL from server
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should respond with WILL FLOWCONTROL
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        
        // Flow control should be enabled per RFC 1372
        await Assert.That(_flowControlStateChanged).IsNotNull();
        await Assert.That(_flowControlStateChanged.Value).IsTrue();
    }

    [Test]
    public async Task ServerAcceptsWillFlowControl()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Server receives WILL FLOWCONTROL from client
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should accept without additional response
        await Assert.That(_negotiationOutput).IsNull();
    }

    [Test]
    public async Task ClientHandlesDontFlowControl()
    {
        // Arrange
        _negotiationOutput = null;
        _flowControlStateChanged = null;

        // Act - Client receives DONT FLOWCONTROL from server
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.FLOWCONTROL });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should accept the rejection gracefully
        await Assert.That(_negotiationOutput).IsNull();
        
        // Flow control should be disabled
        await Assert.That(_flowControlStateChanged).IsNotNull();
        await Assert.That(_flowControlStateChanged.Value).IsFalse();
    }

    [Test]
    public async Task ServerHandlesWontFlowControl()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Server receives WONT FLOWCONTROL from client
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.FLOWCONTROL });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should accept the rejection gracefully
        await Assert.That(_negotiationOutput).IsNull();
    }

    [Test]
    public async Task ClientReceivesFlowControlOff()
    {
        // Arrange - Complete FLOWCONTROL negotiation
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await _client_ti.WaitForProcessingAsync();
        _flowControlStateChanged = null;

        // Act - Client receives OFF command
        await _client_ti.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_OFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Flow control should be disabled
        await Assert.That(_flowControlStateChanged).IsNotNull();
        await Assert.That(_flowControlStateChanged.Value).IsFalse();
        
        var plugin = _client_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.IsFlowControlEnabled).IsFalse();
    }

    [Test]
    public async Task ClientReceivesFlowControlOn()
    {
        // Arrange - Complete FLOWCONTROL negotiation and turn off flow control
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await _client_ti.WaitForProcessingAsync();
        await _client_ti.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_OFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();
        _flowControlStateChanged = null;

        // Act - Client receives ON command
        await _client_ti.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_ON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Flow control should be enabled
        await Assert.That(_flowControlStateChanged).IsNotNull();
        await Assert.That(_flowControlStateChanged.Value).IsTrue();
        
        var plugin = _client_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.IsFlowControlEnabled).IsTrue();
    }

    [Test]
    public async Task ClientReceivesRestartAnyCommand()
    {
        // Arrange - Complete FLOWCONTROL negotiation
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await _client_ti.WaitForProcessingAsync();
        _restartModeChanged = null;

        // Act - Client receives RESTART-ANY command
        await _client_ti.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_ANY,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Restart mode should be set to RestartAny
        await Assert.That(_restartModeChanged).IsNotNull();
        await Assert.That(_restartModeChanged.Value).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartAny);
        
        var plugin = _client_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.RestartMode).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartAny);
    }

    [Test]
    public async Task ClientReceivesRestartXONCommand()
    {
        // Arrange - Complete FLOWCONTROL negotiation
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await _client_ti.WaitForProcessingAsync();
        _restartModeChanged = null;

        // Act - Client receives RESTART-XON command
        await _client_ti.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_XON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Restart mode should be set to RestartXON
        await Assert.That(_restartModeChanged).IsNotNull();
        await Assert.That(_restartModeChanged.Value).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartXON);
        
        var plugin = _client_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.RestartMode).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartXON);
    }

    [Test]
    public async Task ServerCanEnableFlowControl()
    {
        // Arrange - Get the plugin from server
        var plugin = _server_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        
        // Complete negotiation (server receives WILL)
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await _server_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Act - Server sends enable flow control command
        await plugin!.EnableFlowControlAsync();
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should send ON command
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_ON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    [Test]
    public async Task ServerCanDisableFlowControl()
    {
        // Arrange - Get the plugin from server
        var plugin = _server_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        
        // Complete negotiation (server receives WILL)
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await _server_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Act - Server sends disable flow control command
        await plugin!.DisableFlowControlAsync();
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should send OFF command
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_OFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    [Test]
    public async Task ServerCanSetRestartAny()
    {
        // Arrange - Get the plugin from server
        var plugin = _server_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        
        // Complete negotiation (server receives WILL)
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await _server_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Act - Server sends RESTART-ANY command
        await plugin!.SetRestartAnyAsync();
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should send RESTART-ANY command
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_ANY,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    [Test]
    public async Task ServerCanSetRestartXON()
    {
        // Arrange - Get the plugin from server
        var plugin = _server_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        
        // Complete negotiation (server receives WILL)
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await _server_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Act - Server sends RESTART-XON command
        await plugin!.SetRestartXONAsync();
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should send RESTART-XON command
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_XON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    [Test]
    public async Task CompleteNegotiationSequence()
    {
        // This test verifies the complete negotiation sequence from start to finish
        var testServer = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<FlowControlProtocol>()
            .BuildAsync();

        var testClient = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<FlowControlProtocol>()
                .OnFlowControlStateChanged(HandleFlowControlStateChanged)
                .OnRestartModeChanged(HandleRestartModeChanged)
            .BuildAsync();

        // Step 1: Server sends DO FLOWCONTROL (handled in initial negotiation)
        // Step 2: Client receives DO and responds with WILL
        _negotiationOutput = null;
        _flowControlStateChanged = null;
        await testClient.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await testClient.WaitForProcessingAsync();
        
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await Assert.That(_flowControlStateChanged).IsNotNull();
        await Assert.That(_flowControlStateChanged.Value).IsTrue();

        // Step 3: Server receives WILL
        _negotiationOutput = null;
        await testServer.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.FLOWCONTROL });
        await testServer.WaitForProcessingAsync();

        // Step 4: Server sends RESTART-ANY command
        _restartModeChanged = null;
        var serverPlugin = testServer.PluginManager?.GetPlugin<FlowControlProtocol>();
        await serverPlugin!.SetRestartAnyAsync();
        await testServer.WaitForProcessingAsync();
        
        // Step 5: Client receives and processes RESTART-ANY
        await testClient.InterpretByteArrayAsync(_negotiationOutput);
        await testClient.WaitForProcessingAsync();
        
        await Assert.That(_restartModeChanged).IsNotNull();
        await Assert.That(_restartModeChanged.Value).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartAny);

        // Step 6: Server sends OFF command
        _flowControlStateChanged = null;
        await serverPlugin.DisableFlowControlAsync();
        await testServer.WaitForProcessingAsync();
        
        // Step 7: Client receives and processes OFF
        await testClient.InterpretByteArrayAsync(_negotiationOutput);
        await testClient.WaitForProcessingAsync();
        
        await Assert.That(_flowControlStateChanged).IsNotNull();
        await Assert.That(_flowControlStateChanged.Value).IsFalse();

        await testServer.DisposeAsync();
        await testClient.DisposeAsync();
    }

    [Test]
    public async Task PluginIsProperlyRegistered()
    {
        // Verify that the Flow Control protocol plugin is properly registered and enabled
        var plugin = _server_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.IsEnabled).IsTrue();
        await Assert.That(plugin.ProtocolName).IsEqualTo("Flow Control (RFC 1372)");
    }

    [Test]
    public async Task FlowControlStateDefaultsToFalse()
    {
        // Verify the initial state
        var plugin = _client_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.IsFlowControlEnabled).IsFalse();
    }

    [Test]
    public async Task RestartModeDefaultsToSystemDefault()
    {
        // Verify the initial restart mode
        var plugin = _client_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.RestartMode).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.SystemDefault);
    }

    [Test]
    public async Task ClientEnablesFlowControlOnWillSent()
    {
        // Per RFC 1372, flow control must be enabled when WILL is sent
        var plugin = _client_ti.PluginManager?.GetPlugin<FlowControlProtocol>();
        await Assert.That(plugin).IsNotNull();
        await Assert.That(plugin!.IsFlowControlEnabled).IsFalse();

        // Client receives DO FLOWCONTROL
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await _client_ti.WaitForProcessingAsync();

        // Flow control should now be enabled
        await Assert.That(plugin.IsFlowControlEnabled).IsTrue();
    }

    [Test]
    public async Task MultipleStateChangesWork()
    {
        // Test toggling flow control state multiple times
        _flowControlStateChanged = null;

        // Enable
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await _client_ti.WaitForProcessingAsync();
        await Assert.That(_flowControlStateChanged).IsNotNull();
        await Assert.That(_flowControlStateChanged.Value).IsTrue();

        // Disable
        _flowControlStateChanged = null;
        await _client_ti.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_OFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();
        await Assert.That(_flowControlStateChanged).IsNotNull();
        await Assert.That(_flowControlStateChanged.Value).IsFalse();

        // Enable again
        _flowControlStateChanged = null;
        await _client_ti.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_ON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();
        await Assert.That(_flowControlStateChanged).IsNotNull();
        await Assert.That(_flowControlStateChanged.Value).IsTrue();
    }

    [Test]
    public async Task MultipleRestartModeChangesWork()
    {
        // Complete negotiation first
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.FLOWCONTROL });
        await _client_ti.WaitForProcessingAsync();

        // Set to RESTART-ANY
        _restartModeChanged = null;
        await _client_ti.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_ANY,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();
        await Assert.That(_restartModeChanged).IsNotNull();
        await Assert.That(_restartModeChanged.Value).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartAny);

        // Set to RESTART-XON
        _restartModeChanged = null;
        await _client_ti.InterpretByteArrayAsync(new byte[] {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.FLOWCONTROL,
            (byte)Trigger.FLOWCONTROL_RESTART_XON,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await _client_ti.WaitForProcessingAsync();
        await Assert.That(_restartModeChanged).IsNotNull();
        await Assert.That(_restartModeChanged.Value).IsEqualTo(FlowControlProtocol.FlowControlRestartMode.RestartXON);
    }
}
