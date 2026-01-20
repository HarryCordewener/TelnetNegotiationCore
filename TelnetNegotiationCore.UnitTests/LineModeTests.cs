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

public class LineModeTests : BaseTest
{
    private TelnetInterpreter _server_ti;
    private TelnetInterpreter _client_ti;
    private byte[] _negotiationOutput;
    private byte? _modeChanged;

    private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

    private ValueTask WriteBackToNegotiate(byte[] arg1)
    {
        _negotiationOutput = arg1;
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleModeChanged(byte mode)
    {
        _modeChanged = mode;
        logger.LogInformation("Line mode changed: {Mode:X2}", mode);
        return ValueTask.CompletedTask;
    }

    [Before(Test)]
    public async Task Setup()
    {
        _negotiationOutput = null;
        _modeChanged = null;

        _server_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<LineModeProtocol>()
            .BuildAsync();

        _client_ti = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(WriteBackToNegotiate)
            .AddPlugin<LineModeProtocol>()
                .OnModeChanged(HandleModeChanged)
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
    public async Task ClientRespondsWithWillLineModeToServerDo()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Client receives DO LINEMODE from server
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should respond with WILL LINEMODE
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });
        
        // Line mode should be enabled
        var plugin = _client_ti.PluginManager.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin.IsLineModeEnabled).IsTrue();
    }

    [Test]
    public async Task ServerAcceptsWillLineMode()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Server receives WILL LINEMODE from client
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should accept without additional response
        await Assert.That(_negotiationOutput).IsNull();
    }

    [Test]
    public async Task ClientHandlesDontLineMode()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Client receives DONT LINEMODE from server
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.LINEMODE });
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should accept the rejection gracefully
        await Assert.That(_negotiationOutput).IsNull();
        
        // Line mode should be disabled
        var plugin = _client_ti.PluginManager.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin.IsLineModeEnabled).IsFalse();
    }

    [Test]
    public async Task ServerHandlesWontLineMode()
    {
        // Arrange
        _negotiationOutput = null;

        // Act - Server receives WONT LINEMODE from client
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.LINEMODE });
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should accept the rejection gracefully
        await Assert.That(_negotiationOutput).IsNull();
    }

    [Test]
    public async Task ClientReceivesModeCommand()
    {
        // Arrange - Complete LINEMODE negotiation
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });
        await _client_ti.WaitForProcessingAsync();
        _negotiationOutput = null;
        _modeChanged = null;

        // Act - Client receives MODE command with EDIT (0x01) set
        var modeCommand = new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x01, // EDIT mode
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await _client_ti.InterpretByteArrayAsync(modeCommand);
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should acknowledge the mode
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x05, // EDIT | MODE_ACK
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        
        // Mode should be updated
        await Assert.That(_modeChanged).IsNotNull();
        await Assert.That(_modeChanged.Value).IsEqualTo((byte)0x01);
        
        // Plugin should reflect the mode
        var plugin = _client_ti.PluginManager.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin.IsEditModeEnabled).IsTrue();
        await Assert.That(plugin.IsTrapSigModeEnabled).IsFalse();
    }

    [Test]
    public async Task ClientReceivesEditAndTrapSigMode()
    {
        // Arrange - Complete LINEMODE negotiation
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });
        await _client_ti.WaitForProcessingAsync();
        _negotiationOutput = null;
        _modeChanged = null;

        // Act - Client receives MODE command with EDIT (0x01) and TRAPSIG (0x02)
        var modeCommand = new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x03, // EDIT | TRAPSIG
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await _client_ti.InterpretByteArrayAsync(modeCommand);
        await _client_ti.WaitForProcessingAsync();

        // Assert - Client should acknowledge the mode
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x07, // EDIT | TRAPSIG | MODE_ACK
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        
        // Mode should be updated
        await Assert.That(_modeChanged).IsNotNull();
        await Assert.That(_modeChanged.Value).IsEqualTo((byte)0x03);
        
        // Plugin should reflect both modes
        var plugin = _client_ti.PluginManager.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin.IsEditModeEnabled).IsTrue();
        await Assert.That(plugin.IsTrapSigModeEnabled).IsTrue();
    }

    [Test]
    public async Task ServerReceivesModeAcknowledgment()
    {
        // Arrange - Complete LINEMODE negotiation
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });
        await _server_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Act - Server receives MODE acknowledgment with EDIT | MODE_ACK
        var ackCommand = new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x05, // EDIT | MODE_ACK
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await _server_ti.InterpretByteArrayAsync(ackCommand);
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should update its mode state
        var plugin = _server_ti.PluginManager.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin.CurrentMode).IsEqualTo((byte)0x01); // EDIT (without ACK bit)
        await Assert.That(plugin.IsEditModeEnabled).IsTrue();
    }

    [Test]
    public async Task ServerCanSendModeCommand()
    {
        // Arrange - Get server plugin
        _negotiationOutput = null;
        var plugin = _server_ti.PluginManager.GetPlugin<LineModeProtocol>();

        // Complete negotiation first
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });
        await _server_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Act - Server sets EDIT mode
        await plugin.EnableEditModeAsync();
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should send MODE command
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x01, // EDIT
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    [Test]
    public async Task ServerCanSetCustomMode()
    {
        // Arrange - Get server plugin
        _negotiationOutput = null;
        var plugin = _server_ti.PluginManager.GetPlugin<LineModeProtocol>();

        // Complete negotiation first
        await _server_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });
        await _server_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Act - Server sets custom mode with multiple flags
        await plugin.SetModeAsync(0x1B); // EDIT | TRAPSIG | SOFT_TAB | LIT_ECHO
        await _server_ti.WaitForProcessingAsync();

        // Assert - Server should send MODE command with all flags
        await Assert.That(_negotiationOutput).IsNotNull();
        await Assert.That(_negotiationOutput).IsEquivalentTo(new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x1B,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
    }

    [Test]
    public async Task ClientHandlesForwardMaskSubnegotiation()
    {
        // Arrange - Complete LINEMODE negotiation
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });
        await _client_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Act - Client receives FORWARDMASK subnegotiation (not implemented, should be gracefully handled)
        var forwardmaskCommand = new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_FORWARDMASK, 0xFF, 0xFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await _client_ti.InterpretByteArrayAsync(forwardmaskCommand);
        await _client_ti.WaitForProcessingAsync();

        // Assert - Should complete without error (graceful handling)
        // No assertion on output since FORWARDMASK is not implemented
        // The important thing is that it doesn't crash
    }

    [Test]
    public async Task ClientHandlesSLCSubnegotiation()
    {
        // Arrange - Complete LINEMODE negotiation
        await _client_ti.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });
        await _client_ti.WaitForProcessingAsync();
        _negotiationOutput = null;

        // Act - Client receives SLC subnegotiation (not implemented, should be gracefully handled)
        var slcCommand = new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_SLC, 0x01, 0x02, 0x03,
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await _client_ti.InterpretByteArrayAsync(slcCommand);
        await _client_ti.WaitForProcessingAsync();

        // Assert - Should complete without error (graceful handling)
        // No assertion on output since SLC is not implemented
        // The important thing is that it doesn't crash
    }
}
