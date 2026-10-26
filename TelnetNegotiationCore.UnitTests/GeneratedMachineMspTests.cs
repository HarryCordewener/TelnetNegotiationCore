using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>MSP (option 90) through the generated machine: server-offered, no subnegotiation.</summary>
public class GeneratedMachineMspTests : BaseTest
{
    private static byte[] Frame(Trigger verb) => [(byte)Trigger.IAC, (byte)verb, (byte)Trigger.MSP];

    private async Task<(TelnetInterpreter Interpreter, MSPProtocol Msp, List<byte[]> Sent, List<string> Events)> BuildAsync(
        TelnetInterpreter.TelnetMode mode)
    {
        var sent = new List<byte[]>();
        var events = new List<string>();
        ValueTask Capture(System.ReadOnlyMemory<byte> data) { sent.Add(data.ToArray()); return ValueTask.CompletedTask; }

        var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(mode)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(Capture)
            .AddPlugin<MSPProtocol>()
                .OnMSPEnabled(() => { events.Add("on"); return ValueTask.CompletedTask; })
                .OnMSPDisabled(() => { events.Add("off"); return ValueTask.CompletedTask; }));

        return (interpreter, interpreter.PluginManager!.GetPlugin<MSPProtocol>()!, sent, events);
    }

    [Test]
    public async Task ServerOffersMspAtStart()
    {
        var (server, _, sent, _) = await BuildAsync(TelnetInterpreter.TelnetMode.Server);

        await Assert.That(sent.Exists(s => s.AsSpan().SequenceEqual(Frame(Trigger.WILL)))).IsTrue();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientAnswersWillWithDoAndTellsTheHostOnce()
    {
        var (client, msp, sent, events) = await BuildAsync(TelnetInterpreter.TelnetMode.Client);
        sent.Clear();

        await InterpretAndWaitAsync(client, Frame(Trigger.WILL));
        await InterpretAndWaitAsync(client, Frame(Trigger.WILL));

        await Assert.That(msp.IsMSPActive).IsTrue();
        await Assert.That(sent.Count).IsEqualTo(1);
        await AssertByteArraysEqual(sent[0], Frame(Trigger.DO));
        await Assert.That(events).IsEquivalentTo(new[] { "on" });

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientAcknowledgesAWithdrawalAndIgnoresOneForAnOptionNeverAgreed()
    {
        var (client, msp, sent, events) = await BuildAsync(TelnetInterpreter.TelnetMode.Client);
        sent.Clear();

        await InterpretAndWaitAsync(client, Frame(Trigger.WONT));
        await Assert.That(sent).IsEmpty();

        await InterpretAndWaitAsync(client, Frame(Trigger.WILL));
        sent.Clear();
        await InterpretAndWaitAsync(client, Frame(Trigger.WONT));

        await Assert.That(msp.IsMSPActive).IsFalse();
        await Assert.That(sent.Count).IsEqualTo(1);
        await AssertByteArraysEqual(sent[0], Frame(Trigger.DONT));
        await Assert.That(events).IsEquivalentTo(new[] { "on", "off" });

        await client.DisposeAsync();
    }

    [Test]
    public async Task ClientRefusesADo()
    {
        var (client, msp, sent, _) = await BuildAsync(TelnetInterpreter.TelnetMode.Client);
        sent.Clear();

        await InterpretAndWaitAsync(client, Frame(Trigger.DO));

        await Assert.That(msp.IsMSPActive).IsFalse();
        await AssertByteArraysEqual(sent[0], Frame(Trigger.WONT));

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerTurnsMspOnAtDoAndOffAtDont()
    {
        var (server, msp, sent, events) = await BuildAsync(TelnetInterpreter.TelnetMode.Server);

        await InterpretAndWaitAsync(server, Frame(Trigger.DO));
        await Assert.That(msp.IsMSPActive).IsTrue();

        sent.Clear();
        await InterpretAndWaitAsync(server, Frame(Trigger.DONT));

        await Assert.That(msp.IsMSPActive).IsFalse();
        await AssertByteArraysEqual(sent[0], Frame(Trigger.WONT));
        await Assert.That(events).IsEquivalentTo(new[] { "on", "off" });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerRefusesAWill()
    {
        var (server, msp, sent, _) = await BuildAsync(TelnetInterpreter.TelnetMode.Server);
        sent.Clear();

        await InterpretAndWaitAsync(server, Frame(Trigger.WILL));

        await Assert.That(msp.IsMSPActive).IsFalse();
        await AssertByteArraysEqual(sent[0], Frame(Trigger.DONT));

        await server.DisposeAsync();
    }

    [Test]
    public async Task AClientWithoutThePluginRefusesTheOffer()
    {
        var sent = new List<byte[]>();
        ValueTask Capture(System.ReadOnlyMemory<byte> data) { sent.Add(data.ToArray()); return ValueTask.CompletedTask; }
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(Capture)
            .AddPlugin<NAWSProtocol>());
        sent.Clear();

        await InterpretAndWaitAsync(client, Frame(Trigger.WILL));

        await AssertByteArraysEqual(sent[0], Frame(Trigger.DONT));

        await client.DisposeAsync();
    }
}
