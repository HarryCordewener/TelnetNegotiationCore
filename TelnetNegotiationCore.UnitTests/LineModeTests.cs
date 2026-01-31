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
    [Test]
    public async Task ClientRespondsWithWillLineModeToServerDo()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var client_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Client)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
        );

        // Act - Client receives DO LINEMODE from server
        await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });

        // Assert - Client should respond with WILL LINEMODE
        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });
        
        // Line mode should be enabled
        var plugin = client_ti.PluginManager.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin.IsLineModeEnabled).IsTrue();

        // Cleanup
        await client_ti.DisposeAsync();
    }

    [Test]
    public async Task ServerAcceptsWillLineMode()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var server_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
        );
        negotiationOutput = null;

        // Act - Server receives WILL LINEMODE from client
        await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });

        // Assert - Server should accept without additional response
        await Assert.That(negotiationOutput).IsNull();

        // Cleanup
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task ClientHandlesDontLineMode()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var client_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Client)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
        );

        // Act - Client receives DONT LINEMODE from server
        await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.LINEMODE });

        // Assert - Client should accept the rejection gracefully
        await Assert.That(negotiationOutput).IsNull();
        
        // Line mode should be disabled
        var plugin = client_ti.PluginManager.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin.IsLineModeEnabled).IsFalse();

        // Cleanup
        await client_ti.DisposeAsync();
    }

    [Test]
    public async Task ServerHandlesWontLineMode()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var server_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
        );
        negotiationOutput = null;

        // Act - Server receives WONT LINEMODE from client
        await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.LINEMODE });

        // Assert - Server should accept the rejection gracefully
        await Assert.That(negotiationOutput).IsNull();

        // Cleanup
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task ClientReceivesModeCommand()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;
        byte? modeChanged = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureModeChanged(byte mode)
        {
            modeChanged = mode;
            logger.LogInformation("Line mode changed: {Mode:X2}", mode);
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var client_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Client)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
                    .OnModeChanged(CaptureModeChanged)
        );

        // Arrange - Complete LINEMODE negotiation
        await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });
        await Task.Delay(100);
        negotiationOutput = null;
        modeChanged = null;

        // Act - Client receives MODE command with EDIT (0x01) set
        var modeCommand = new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x01, // EDIT mode
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await InterpretAndWaitAsync(client_ti, modeCommand);
        await Task.Delay(50);

        // Assert - Client should acknowledge the mode
        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x05, // EDIT | MODE_ACK
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        
        // Mode should be updated
        await Assert.That(modeChanged).IsNotNull();
        await Assert.That(modeChanged.Value).IsEqualTo((byte)0x01);
        
        // Plugin should reflect the mode
        var plugin = client_ti.PluginManager.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin.IsEditModeEnabled).IsTrue();
        await Assert.That(plugin.IsTrapSigModeEnabled).IsFalse();

        // Cleanup
        await client_ti.DisposeAsync();
    }

    [Test]
    public async Task ClientReceivesEditAndTrapSigMode()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;
        byte? modeChanged = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        ValueTask CaptureModeChanged(byte mode)
        {
            modeChanged = mode;
            logger.LogInformation("Line mode changed: {Mode:X2}", mode);
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var client_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Client)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
                    .OnModeChanged(CaptureModeChanged)
        );

        // Arrange - Complete LINEMODE negotiation
        await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });
        await Task.Delay(100);
        negotiationOutput = null;
        modeChanged = null;

        // Act - Client receives MODE command with EDIT (0x01) and TRAPSIG (0x02)
        var modeCommand = new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x03, // EDIT | TRAPSIG
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await InterpretAndWaitAsync(client_ti, modeCommand);
        await Task.Delay(50);

        // Assert - Client should acknowledge the mode
        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x07, // EDIT | TRAPSIG | MODE_ACK
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        
        // Mode should be updated
        await Assert.That(modeChanged).IsNotNull();
        await Assert.That(modeChanged.Value).IsEqualTo((byte)0x03);
        
        // Plugin should reflect both modes
        var plugin = client_ti.PluginManager.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin.IsEditModeEnabled).IsTrue();
        await Assert.That(plugin.IsTrapSigModeEnabled).IsTrue();

        // Cleanup
        await client_ti.DisposeAsync();
    }

    [Test]
    public async Task ServerReceivesModeAcknowledgment()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var server_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
        );

        // Arrange - Complete LINEMODE negotiation
        await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });
        await Task.Delay(100);
        negotiationOutput = null;

        // Act - Server receives MODE acknowledgment with EDIT | MODE_ACK
        var ackCommand = new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x05, // EDIT | MODE_ACK
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await InterpretAndWaitAsync(server_ti, ackCommand);
        await Task.Delay(50);

        // Assert - Server should update its mode state
        var plugin = server_ti.PluginManager.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin.CurrentMode).IsEqualTo((byte)0x01); // EDIT (without ACK bit)
        await Assert.That(plugin.IsEditModeEnabled).IsTrue();

        // Cleanup
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task ServerCanSendModeCommand()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var server_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
        );

        // Arrange - Get server plugin
        var plugin = server_ti.PluginManager.GetPlugin<LineModeProtocol>();

        // Complete negotiation first
        await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });
        negotiationOutput = null;

        // Act - Server sets EDIT mode
        await plugin.EnableEditModeAsync();
        await server_ti.WaitForProcessingAsync();

        // Assert - Server should send MODE command
        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x01, // EDIT
            (byte)Trigger.IAC, (byte)Trigger.SE
        });

        // Cleanup
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task ServerCanSetCustomMode()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var server_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Server)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
        );

        // Arrange - Get server plugin
        var plugin = server_ti.PluginManager.GetPlugin<LineModeProtocol>();

        // Complete negotiation first
        await InterpretAndWaitAsync(server_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });
        negotiationOutput = null;

        // Act - Server sets custom mode with multiple flags
        await plugin.SetModeAsync(0x1B); // EDIT | TRAPSIG | SOFT_TAB | LIT_ECHO
        await server_ti.WaitForProcessingAsync();

        // Assert - Server should send MODE command with all flags
        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x1B,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });

        // Cleanup
        await server_ti.DisposeAsync();
    }

    [Test]
    public async Task ClientHandlesForwardMaskSubnegotiation()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var client_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Client)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
        );

        // Arrange - Complete LINEMODE negotiation
        await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });
        negotiationOutput = null;

        // Act - Client receives FORWARDMASK subnegotiation (not implemented, should be gracefully handled)
        var forwardmaskCommand = new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_FORWARDMASK, 0xFF, 0xFF,
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await InterpretAndWaitAsync(client_ti, forwardmaskCommand);

        // Assert - Should complete without error (graceful handling)
        // No assertion on output since FORWARDMASK is not implemented
        // The important thing is that it doesn't crash

        // Cleanup
        await client_ti.DisposeAsync();
    }

    [Test]
    public async Task ClientHandlesSLCSubnegotiation()
    {
        // Arrange - Local variables
        byte[] negotiationOutput = null;

        // Arrange - Local callbacks
        ValueTask CaptureNegotiation(byte[] data)
        {
            negotiationOutput = data;
            return ValueTask.CompletedTask;
        }

        // Arrange - Build TelnetInterpreter instances
        var client_ti = await BuildAndWaitAsync(
            new TelnetInterpreterBuilder()
                .UseMode(TelnetInterpreter.TelnetMode.Client)
                .UseLogger(logger)
                .OnSubmit(NoOpSubmitCallback)
                .OnNegotiation(CaptureNegotiation)
                .AddPlugin<LineModeProtocol>()
        );

        // Arrange - Complete LINEMODE negotiation
        await InterpretAndWaitAsync(client_ti, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });
        negotiationOutput = null;

        // Act - Client receives SLC subnegotiation (not implemented, should be gracefully handled)
        var slcCommand = new byte[] 
        { 
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_SLC, 0x01, 0x02, 0x03,
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await InterpretAndWaitAsync(client_ti, slcCommand);

        // Assert - Should complete without error (graceful handling)
        // No assertion on output since SLC is not implemented
        // The important thing is that it doesn't crash

        // Cleanup
        await client_ti.DisposeAsync();
    }
}
