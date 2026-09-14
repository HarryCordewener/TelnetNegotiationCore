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

/// <summary>
/// The Stage-3 vertical slice: the same scenarios <see cref="NAWSTests"/> exercises, driven by the generated
/// machine instead of Stateless. Proof that the seam works end to end against the interpreter's own behaviour,
/// not a parallel sample harness — every assertion here is copied from a real, currently-passing test.
/// </summary>
public class GeneratedMachineNawsTests : BaseTest
{
    [Test]
    public async Task ServerReceivesNAWSData()
    {
        int receivedHeight = 0;
        int receivedWidth = 0;

        ValueTask CaptureNAWS(int height, int width)
        {
            receivedHeight = height;
            receivedWidth = width;
            return ValueTask.CompletedTask;
        }
        ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<NAWSProtocol>()
                .OnNAWS(CaptureNAWS)
            .BuildAsync();

        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NAWS });
        await server.WaitForProcessingAsync();
        receivedWidth = 0;
        receivedHeight = 0;

        var nawsData = new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
            0, 80, 0, 24,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        };

        await server.InterpretByteArrayAsync(nawsData);
        await server.WaitForProcessingAsync();

        await Assert.That(receivedWidth).IsEqualTo(80);
        await Assert.That(receivedHeight).IsEqualTo(24);
        await Assert.That(server.ClientWidth).IsEqualTo(80);
        await Assert.That(server.ClientHeight).IsEqualTo(24);

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientCanSendNAWSData()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }
        ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<NAWSProtocol>()
            .BuildAsync();

        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
        await client.WaitForProcessingAsync();
        negotiationOutput = null;

        await client.PluginManager!.GetPlugin<NAWSProtocol>()!.SendWindowSizeAsync(100, 40);

        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput[0]).IsEqualTo((byte)Trigger.IAC);
        await Assert.That(negotiationOutput[1]).IsEqualTo((byte)Trigger.SB);
        await Assert.That(negotiationOutput[2]).IsEqualTo((byte)Trigger.NAWS);
        await Assert.That(negotiationOutput[^2]).IsEqualTo((byte)Trigger.IAC);
        await Assert.That(negotiationOutput[^1]).IsEqualTo((byte)Trigger.SE);

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerHandlesDontNAWS()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }
        ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<NAWSProtocol>()
            .BuildAsync();

        negotiationOutput = null;
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.NAWS });
        await server.WaitForProcessingAsync();

        await Assert.That(negotiationOutput).IsNull();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerWontNAWSIfAskedToDo()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }
        ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<NAWSProtocol>()
            .BuildAsync();

        negotiationOutput = null;
        await server.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS });
        await server.WaitForProcessingAsync();

        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WONT, (byte)Trigger.NAWS });

        await server.DisposeAsync();
    }

    /// <summary>The core framing: ordinary text still assembles into lines when NAWS drives the machine.</summary>
    [Test]
    public async Task OrdinaryTextStillSubmitsALine()
    {
        byte[] submitted = null;
        ValueTask OnSubmit(byte[] a, Encoding e, TelnetInterpreter t) { submitted = a; return ValueTask.CompletedTask; }

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(OnSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<NAWSProtocol>()
            .BuildAsync();

        await server.InterpretByteArrayAsync(Encoding.ASCII.GetBytes("hello\n"));
        await server.WaitForProcessingAsync();

        await Assert.That(submitted).IsNotNull();
        await Assert.That(Encoding.ASCII.GetString(submitted)).IsEqualTo("hello");

        await server.DisposeAsync();
    }

    /// <summary>An option nothing here is wired to yet is still refused by its own number.</summary>
    [Test]
    public async Task AnUnwiredNamedOptionIsStillRefusedByNumber()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }
        ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(WriteBackToOutput)
            .OnNegotiation(CaptureNegotiation)
            .BuildAsync();

        await client.InterpretByteArrayAsync(new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP });
        await client.WaitForProcessingAsync();

        await Assert.That(negotiationOutput).IsNotNull();
        await Assert.That(negotiationOutput).IsEquivalentTo(new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MSDP });

        await client.DisposeAsync();
    }
}
