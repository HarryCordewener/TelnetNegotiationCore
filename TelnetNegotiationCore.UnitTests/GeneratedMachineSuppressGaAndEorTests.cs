using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// SUPPRESS-GO-AHEAD and EOR through the generated machine — both directions of negotiation, a bare command
/// each option gives its own meaning to, and the graceful no-response cases. Scenarios copied from
/// <see cref="SuppressGATests"/> and <see cref="EORTests"/>.
/// </summary>
public class GeneratedMachineSuppressGaAndEorTests : BaseTest
{
    [Test]
    public async Task ClientRespondsWithDoSuppressGAToServerWill()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<SuppressGoAheadProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

        await client.DisposeAsync();
    }

    [Test]
    public async Task AClientAnswersAnInboundDoSuppressGoAhead()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<SuppressGoAheadProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD });

        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.SUPPRESSGOAHEAD });

        await client.DisposeAsync();
    }

    [Test]
    public async Task ServerHandlesDontSuppressGA()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<SuppressGoAheadProtocol>());

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.SUPPRESSGOAHEAD });

        await Assert.That(negotiationOutput).IsNull();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ClientRespondsWithDoEORToServerWill()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<EORProtocol>());

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR });

        await client.DisposeAsync();
    }

    /// <summary>RFC 885: IAC EOR is a NOP when the option is not in effect.</summary>
    [Test]
    public async Task BareEorIsANopWhenNotNegotiated()
    {
        var promptReceived = false;
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EORProtocol>()
                .OnPrompt(() => { promptReceived = true; return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });

        await Assert.That(promptReceived).IsFalse();

        await client.DisposeAsync();
    }

    /// <summary>Once negotiated, a bare IAC EOR is a genuine prompt boundary.</summary>
    [Test]
    public async Task ClientReceivesEORPromptSignal()
    {
        var promptReceived = false;
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EORProtocol>()
                .OnPrompt(() => { promptReceived = true; return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.TELOPT_EOR });
        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.EOR });

        await Assert.That(promptReceived).IsTrue();

        await client.DisposeAsync();
    }
}
