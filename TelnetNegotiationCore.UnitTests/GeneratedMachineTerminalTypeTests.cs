using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>TTYPE (RFC 1091) through the generated machine, negotiation and the multi-round cycling response both.</summary>
public class GeneratedMachineTerminalTypeTests : BaseTest
{
    [Test]
    public async Task ServerRequestsTerminalTypeOnWill()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<TerminalTypeProtocol>());

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await server.DisposeAsync();
    }

    /// <summary>The server keeps asking for the next type until the client repeats one, and the interpreter's
    /// selection tracks the most recently reported type.</summary>
    [Test]
    public async Task ServerCollectsTerminalTypesAndPublishesSelection()
    {
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<TerminalTypeProtocol>());

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE });
        await Assert.That(server.CurrentTerminalType).IsEqualTo("unknown");

        (string Reported, string Selected)[] exchanges = [("ANSI", "ANSI"), ("VT100", "VT100"), ("VT100", "ANSI")];

        foreach (var (reported, selected) in exchanges)
        {
            await InterpretAndWaitAsync(server,
            [
                (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS,
                .. Encoding.ASCII.GetBytes(reported),
                (byte)Trigger.IAC, (byte)Trigger.SE
            ]);
            await Assert.That(server.CurrentTerminalType).IsEqualTo(selected);
        }

        await Assert.That(server.TerminalTypes).IsEquivalentTo(["ANSI", "VT100"]);

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientRespondsWithWillToServerDo()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<TerminalTypeProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE });

        await client.DisposeAsync();
    }

    /// <summary>No client identity is configured, so the client names nobody: UNKNOWN.</summary>
    [Test]
    public async Task ClientReportsUnknownWhenAskedToSend()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<TerminalTypeProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TTYPE });
        negotiationOutput = null;

        await InterpretAndWaitAsync(client, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(negotiationOutput).IsNotNull();
        var expectedBytes = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS }
            .Concat(Encoding.ASCII.GetBytes("UNKNOWN"))
            .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
            .ToArray();
        await AssertByteArraysEqual(negotiationOutput, expectedBytes);

        await client.DisposeAsync();
    }

    /// <summary>
    /// A server never calls the client-side auto-detection that guarantees at least one entry, so its
    /// own terminal type list is empty by construction. A peer that sends it a SEND anyway (never a
    /// server's own role under RFC 1091, but nothing stops a misbehaving one) used to throw
    /// <see cref="System.ArgumentOutOfRangeException"/> indexing that empty list with
    /// <c>_currentTerminalType</c> advanced from -1 to 0. It now reports UNKNOWN instead, the same
    /// fallback a client with nothing detected reports.
    /// </summary>
    [Test]
    public async Task ServerWithNoConfiguredTypesReportsUnknownRatherThanThrowing()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<TerminalTypeProtocol>());

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TTYPE });
        negotiationOutput = null;

        await InterpretAndWaitAsync(server, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(negotiationOutput).IsNotNull();
        var expectedBytes = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.TTYPE, (byte)Trigger.IS }
            .Concat(Encoding.ASCII.GetBytes(TerminalTypeProtocol.UnknownTerminalType))
            .Concat(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE })
            .ToArray();
        await AssertByteArraysEqual(negotiationOutput, expectedBytes);

        await server.DisposeAsync();
    }
}
