using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>ECHO's negotiation, and PACKET-PATCH's timer-based prompt heuristic, through the generated machine.</summary>
public class GeneratedMachineEchoAndPacketPatchTests : BaseTest
{
    [Test]
    public async Task ServerAcceptsDoEcho()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>());

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });

        await Assert.That(negotiationOutput).IsNull();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientAcceptsWillEcho()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EchoProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ECHO });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ECHO });

        await client.DisposeAsync();
    }

    /// <summary>PacketPatch never touches the state machine at all -- its timer hooks the byte-processed
    /// notification the outer loop already calls regardless of which machine drove the byte.</summary>
    [Test]
    public async Task AnUnterminatedFragmentBecomesAPromptAfterTheHoldTime()
    {
        var hold = TimeSpan.FromMilliseconds(400);
        var lines = new List<string>();
        var prompts = 0;

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, _, _) => { lines.Add(Encoding.ASCII.GetString(data)); return ValueTask.CompletedTask; })
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<PacketPatchProtocol>()
            .WithHoldTime(hold)
            .OnPrompt(() => { prompts++; return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(client, Encoding.ASCII.GetBytes("What's your name, freejack?"));

        await Assert.That(await PollUntilAsync(() => prompts == 1)).IsTrue();
        await Assert.That(Encoding.ASCII.GetString(client.LastPromptBytes.Span))
            .IsEqualTo("What's your name, freejack?");
        await Assert.That(lines.Count).IsEqualTo(0);

        await client.DisposeAsync();
    }
}
