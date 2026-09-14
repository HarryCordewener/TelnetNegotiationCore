using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>LINEMODE (RFC 1184) through the generated machine. Every verb is answered identically
/// regardless of which side receives it -- DO and WILL both trigger the same WillLineModeAsync in both
/// ConfigureAsClient and ConfigureAsServer -- mirrored here from LineModeTests.cs.</summary>
public class GeneratedMachineLineModeTests : BaseTest
{
    [Test]
    public async Task ClientRespondsWithWillOnServerDo()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<LineModeProtocol>());

        negotiationOutput = null;
        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });

        var plugin = client.PluginManager!.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin!.IsLineModeEnabled).IsTrue();

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerAcceptsClientWill()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<LineModeProtocol>());

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });

        var plugin = server.PluginManager!.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin!.IsLineModeEnabled).IsTrue();

        await server.DisposeAsync();
    }

    /// <summary>Mirrors LineModeTests.ClientReceivesModeCommand: a mode proposed without the ACK bit
    /// is acknowledged, and the plugin's own state updates from it.</summary>
    [Test]
    public async Task ClientAcknowledgesAndAppliesAProposedMode()
    {
        byte[] negotiationOutput = null;
        byte? modeChanged = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }
        ValueTask CaptureModeChanged(byte mode) { modeChanged = mode; return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<LineModeProtocol>()
                .OnModeChanged(CaptureModeChanged));

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.LINEMODE });
        negotiationOutput = null;

        await InterpretAndWaitAsync(client, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x03, // EDIT | TRAPSIG
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x07, // EDIT | TRAPSIG | MODE_ACK
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(modeChanged).IsEqualTo((byte)0x03);

        var plugin = client.PluginManager!.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin!.IsEditModeEnabled).IsTrue();
        await Assert.That(plugin.IsTrapSigModeEnabled).IsTrue();

        await client.DisposeAsync();
    }

    /// <summary>Mirrors LineModeTests.ServerReceivesModeAcknowledgment: a mode carrying the ACK bit is
    /// stored (without that bit) but not re-acknowledged.</summary>
    [Test]
    public async Task ServerStoresAnAcknowledgedModeWithoutReplying()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<LineModeProtocol>());

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.LINEMODE });
        negotiationOutput = null;

        await InterpretAndWaitAsync(server, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.LINEMODE,
            (byte)Trigger.LINEMODE_MODE, 0x05, // EDIT | MODE_ACK
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(negotiationOutput).IsNull();

        var plugin = server.PluginManager!.GetPlugin<LineModeProtocol>();
        await Assert.That(plugin!.CurrentMode).IsEqualTo((byte)0x01);
        await Assert.That(plugin.IsEditModeEnabled).IsTrue();

        await server.DisposeAsync();
    }
}
