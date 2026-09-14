using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>MCCP through the generated machine: MCCP2 and MCCP3 share one protocol class across two
/// option numbers, each mode-gated the opposite way, and MCCP1's marker is recognised by both sides
/// but only ever inflated by whichever side originally inflated MCCP2's.</summary>
public class GeneratedMachineMccpTests : BaseTest
{
    [Test]
    [Arguments((byte)Trigger.MCCP2)]
    [Arguments((byte)Trigger.MCCP3)]
    public async Task ClientRefusesWrongDirectionDoWithWont(byte option)
    {
        byte[] negotiationOutput = null;

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(data => { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; })
            .AddPlugin<MCCPProtocol>());

        var mccpPlugin = client.PluginManager!.GetPlugin<MCCPProtocol>()!;
        await InterpretAndWaitAsync(client,
            [(byte)Trigger.IAC, (byte)Trigger.DO, option]);

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput,
            [(byte)Trigger.IAC, (byte)Trigger.WONT, option]);
        await Assert.That(mccpPlugin.IsMCCP2Enabled).IsFalse();
        await Assert.That(mccpPlugin.IsMCCP3Enabled).IsFalse();

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientRespondsWithDoOnServerWillMccp2()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<MCCPProtocol>());

        negotiationOutput = null;
        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2 });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerSendsMarkerOnClientDoMccp2()
    {
        byte[] negotiationOutput = null;
        var compressionVersion = 0;
        var compressionEnabled = false;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }
        ValueTask OnCompressionEnabled(int version, bool enabled) { compressionVersion = version; compressionEnabled = enabled; return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<MCCPProtocol>()
                .OnCompressionEnabled(OnCompressionEnabled));

        var mccpPlugin = server.PluginManager!.GetPlugin<MCCPProtocol>();

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP2 });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP2, (byte)Trigger.IAC, (byte)Trigger.SE,
        });
        await Assert.That(mccpPlugin!.IsMCCP2Enabled).IsTrue();
        await Assert.That(compressionVersion).IsEqualTo(2);
        await Assert.That(compressionEnabled).IsTrue();

        await server.DisposeAsync();
    }

    /// <summary>MCCP3 differs from MCCP2 in when compression starts: the client that accepts WILL
    /// MCCP3 starts deflating its own output right away, not on a marker from the server.</summary>
    [Test]
    public async Task ClientStartsDeflatingOnServerWillMccp3()
    {
        byte[] negotiationOutput = null;

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(data => { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; })
            .AddPlugin<MCCPProtocol>());

        var mccpPlugin = client.PluginManager!.GetPlugin<MCCPProtocol>();

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP3 });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP3, (byte)Trigger.IAC, (byte)Trigger.SE,
        });
        await Assert.That(mccpPlugin!.IsMCCP3Enabled).IsTrue();

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerInflatesOnMccp3MarkerAfterDo()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<MCCPProtocol>());

        var mccpPlugin = server.PluginManager!.GetPlugin<MCCPProtocol>();

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MCCP3 });
        await Assert.That(mccpPlugin!.IsMCCP3Enabled).IsFalse();

        await InterpretAndWaitAsync(server, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP3, (byte)Trigger.IAC, (byte)Trigger.SE,
        });
        await Assert.That(mccpPlugin!.IsMCCP3Enabled).IsTrue();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerHandlesDontMccp3AfterDo()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<MCCPProtocol>());

        var mccpPlugin = server.PluginManager!.GetPlugin<MCCPProtocol>();

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MCCP3 });

        await Assert.That(mccpPlugin!.IsMCCP3Enabled).IsFalse();

        await server.DisposeAsync();
    }

    /// <summary>A client honours MCCP v1's odd marker (no doubled IAC) as the same server-to-client
    /// stream MCCP2's marker starts, even though option 85 itself is never negotiated.</summary>
    [Test]
    public async Task ClientInflatesOnMccp1Marker()
    {
        var compressionVersion = 0;
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<MCCPProtocol>()
                .OnCompressionEnabled((version, enabled) => { compressionVersion = version; return ValueTask.CompletedTask; }));

        var mccpPlugin = client.PluginManager!.GetPlugin<MCCPProtocol>();

        await InterpretAndWaitAsync(client, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP1, (byte)Trigger.WILL, (byte)Trigger.SE,
        });

        await Assert.That(mccpPlugin!.IsMCCP2Enabled).IsTrue();
        await Assert.That(compressionVersion).IsEqualTo(1);

        await client.DisposeAsync();
    }

    /// <summary>A server never configured a route to consume the MCCP1 marker as anything but noise;
    /// it must not start inflating from it.</summary>
    [Test]
    public async Task ServerIgnoresMccp1Marker()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<MCCPProtocol>());

        var mccpPlugin = server.PluginManager!.GetPlugin<MCCPProtocol>();

        await InterpretAndWaitAsync(server, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP1, (byte)Trigger.WILL, (byte)Trigger.SE,
        });

        await Assert.That(mccpPlugin!.IsMCCP2Enabled).IsFalse();
        await Assert.That(mccpPlugin.IsMCCP3Enabled).IsFalse();

        await server.DisposeAsync();
    }
}
