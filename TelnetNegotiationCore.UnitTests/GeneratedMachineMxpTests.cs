using System.Collections.Generic;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>MXP through the generated machine. Servers normally receive DO/DONT and clients normally
/// receive WILL/WONT. A client explicitly refuses the wrong-direction DO used by some deployed servers
/// before accepting their conventional WILL offer.</summary>
public class GeneratedMachineMxpTests : BaseTest
{
    private static readonly byte[] MxpStartMarker =
        [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MXP, (byte)Trigger.IAC, (byte)Trigger.SE];

    [Test]
    public async Task ServerSendsStartMarkerAndEnablesModeOnClientDo()
    {
        byte[] negotiationOutput = null;
        var callbackFired = false;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<MXPProtocol>()
                .OnMXPEnabled(() => { callbackFired = true; return ValueTask.CompletedTask; }));

        var mxpPlugin = server.PluginManager!.GetPlugin<MXPProtocol>();

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP });

        await Assert.That(mxpPlugin!.IsMXPActive).IsTrue();
        await Assert.That(mxpPlugin.IsMxpModeStarted).IsTrue();
        await Assert.That(callbackFired).IsTrue();
        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, MxpStartMarker);

        await server.DisposeAsync();
    }

    /// <summary>WILL/DO settles the option; MXP mode itself only begins at the start marker.</summary>
    [Test]
    public async Task ClientNegotiatesButDoesNotStartModeOnServerWill()
    {
        var callbackFired = false;
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<MXPProtocol>()
                .OnMXPEnabled(() => { callbackFired = true; return ValueTask.CompletedTask; }));

        var mxpPlugin = client.PluginManager!.GetPlugin<MXPProtocol>();

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });

        await Assert.That(mxpPlugin!.IsMXPActive).IsTrue();
        await Assert.That(mxpPlugin.IsMxpModeStarted).IsFalse();
        await Assert.That(callbackFired).IsFalse();
        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput,
            [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP]);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientRefusesProbeThenAcceptsConventionalOffer()
    {
        var negotiationOutput = new List<byte[]>();
        var callbackCount = 0;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data)
        {
            negotiationOutput.Add(data.ToArray());
            return ValueTask.CompletedTask;
        }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<MXPProtocol>()
                .OnMXPEnabled(() => { callbackCount++; return ValueTask.CompletedTask; }));
        var mxpPlugin = client.PluginManager!.GetPlugin<MXPProtocol>()!;

        await InterpretAndWaitAsync(client,
            [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP]);
        await Assert.That(mxpPlugin.IsMXPActive).IsFalse();
        await Assert.That(mxpPlugin.IsMxpModeStarted).IsFalse();
        await Assert.That(callbackCount).IsEqualTo(0);

        await InterpretAndWaitAsync(client,
            [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP]);
        await Assert.That(mxpPlugin.IsMXPActive).IsTrue();
        await Assert.That(mxpPlugin.IsMxpModeStarted).IsFalse();
        await Assert.That(callbackCount).IsEqualTo(0);

        await InterpretAndWaitAsync(client, MxpStartMarker);

        await Assert.That(negotiationOutput).Count().IsEqualTo(2);
        await AssertByteArraysEqual(negotiationOutput[0],
            [(byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MXP]);
        await AssertByteArraysEqual(negotiationOutput[1],
            [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MXP]);
        await Assert.That(mxpPlugin.IsMxpModeStarted).IsTrue();
        await Assert.That(callbackCount).IsEqualTo(1);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientMxpModeStartsOnServerSubnegotiation()
    {
        var callbackCount = 0;

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<MXPProtocol>()
                .OnMXPEnabled(() => { callbackCount++; return ValueTask.CompletedTask; }));

        var mxpPlugin = client.PluginManager!.GetPlugin<MXPProtocol>();

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MXP });
        await InterpretAndWaitAsync(client, MxpStartMarker);

        await Assert.That(mxpPlugin!.IsMxpModeStarted).IsTrue();
        await Assert.That(callbackCount).IsEqualTo(1);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerDisabledOnClientDont()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<MXPProtocol>());

        var mxpPlugin = server.PluginManager!.GetPlugin<MXPProtocol>();

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MXP });

        await Assert.That(mxpPlugin!.IsMXPActive).IsFalse();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientDisabledOnServerWont()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseGeneratedMachine()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<MXPProtocol>());

        var mxpPlugin = client.PluginManager!.GetPlugin<MXPProtocol>();

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.MXP });

        await Assert.That(mxpPlugin!.IsMXPActive).IsFalse();

        await client.DisposeAsync();
    }
}
