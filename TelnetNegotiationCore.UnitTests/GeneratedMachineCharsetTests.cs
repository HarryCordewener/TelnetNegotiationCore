using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>CHARSET (RFC 2066) through the generated machine: negotiation is symmetric (whichever side
/// receives WILL answers DO, whichever side receives DO answers with the charset list), and TTABLE
/// piggybacks on the same option.</summary>
public class GeneratedMachineCharsetTests : BaseTest
{
    [Test]
    public async Task RespondsWithDoOnWill()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<CharsetProtocol>());

        negotiationOutput = null;
        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET });

        await server.DisposeAsync();
    }

    /// <summary>Only the initial offer is mode-specific: the server announces WILL CHARSET on connect.</summary>
    [Test]
    public async Task ServerOffersCharsetOnConnect()
    {
        var initialNegotiation = new List<byte>();
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { initialNegotiation.AddRange(data.ToArray()); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<CharsetProtocol>());

        await AssertByteArraysEqual(initialNegotiation.ToArray(), new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });

        await server.DisposeAsync();
    }

    /// <summary>The side that receives DO CHARSET, then a REQUEST naming a single charset, picks it (there is
    /// nothing else to pick) and reports the change -- mirrors CHARSETTests.ServerReportsCharsetChangeWhenItChoosesFromTheOfferedCharsets.</summary>
    [Test]
    public async Task ChoosesFromASingleOfferedCharsetAndReportsTheChange()
    {
        var reported = new List<System.Text.Encoding>();
        ValueTask OnCharsetChange(System.Text.Encoding encoding) { reported.Add(encoding); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<CharsetProtocol>());

        server.PluginManager!.GetPlugin<CharsetProtocol>()!.OnCharsetChange(OnCharsetChange);

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });

        var request = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST, (byte)';' };
        request.AddRange(Encoding.ASCII.GetBytes("iso-8859-1"));
        request.Add((byte)Trigger.IAC);
        request.Add((byte)Trigger.SE);
        await InterpretAndWaitAsync(server, request.ToArray());

        await Assert.That(server.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");
        await Assert.That(reported.Count).IsEqualTo(1);
        await Assert.That(reported[0].WebName).IsEqualTo("iso-8859-1");

        await server.DisposeAsync();
    }

    /// <summary>The offerer, on being told ACCEPTED, applies the peer's pick -- mirrors
    /// CHARSETTests.ClientReportsCharsetChangeWhenServerAcceptsOurCharset.</summary>
    [Test]
    public async Task AppliesThePeersAcceptedCharsetAndReportsTheChange()
    {
        var reported = new List<System.Text.Encoding>();
        ValueTask OnCharsetChange(System.Text.Encoding encoding) { reported.Add(encoding); return ValueTask.CompletedTask; }

        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<CharsetProtocol>());

        client.PluginManager!.GetPlugin<CharsetProtocol>()!.OnCharsetChange(OnCharsetChange);

        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
        await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET });

        var accepted = new List<byte> { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED };
        accepted.AddRange(Encoding.ASCII.GetBytes("iso-8859-1"));
        accepted.Add((byte)Trigger.IAC);
        accepted.Add((byte)Trigger.SE);
        await InterpretAndWaitAsync(client, accepted.ToArray());

        await Assert.That(client.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");
        await Assert.That(reported.Count).IsEqualTo(1);
        await Assert.That(reported[0].WebName).IsEqualTo("iso-8859-1");

        await client.DisposeAsync();
    }

    /// <summary>Mirrors CHARSETTests.TTableReceivedCallback_ShouldBeInvoked.</summary>
    [Test]
    public async Task TTableReceivedCallbackIsInvokedAndAcked()
    {
        var wasCallbackInvoked = false;
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<CharsetProtocol>());

        var charsetPlugin = server.PluginManager!.GetPlugin<CharsetProtocol>()!;
        charsetPlugin.EnableTTableSupport = true;
        charsetPlugin.OnTTableReceived(data => { wasCallbackInvoked = true; return ValueTask.FromResult(true); });

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });

        var ttableMessage = new List<byte>
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_IS,
            1, (byte)';',
            (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', (byte)';',
            8, 0, 0, 10,
            (byte)'u', (byte)'s', (byte)'-', (byte)'a', (byte)'s', (byte)'c', (byte)'i', (byte)'i', (byte)';',
            8, 0, 0, 10,
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
            (byte)Trigger.IAC, (byte)Trigger.SE
        };
        await InterpretAndWaitAsync(server, ttableMessage.ToArray());

        await Assert.That(wasCallbackInvoked).IsTrue();
        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_ACK,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await server.DisposeAsync();
    }

    /// <summary>Mirrors CHARSETTests.TTableBeyondTheConfiguredCeiling_IsRejectedNotTruncated.</summary>
    [Test]
    public async Task TTableBeyondTheConfiguredCeilingIsRejected()
    {
        var wasCallbackInvoked = false;
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<CharsetProtocol>());

        var charsetPlugin = server.PluginManager!.GetPlugin<CharsetProtocol>()!;
        charsetPlugin.EnableTTableSupport = true;
        charsetPlugin.MaxTTableSize = 1024;
        charsetPlugin.OnTTableReceived(data => { wasCallbackInvoked = true; return ValueTask.FromResult(true); });

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });

        var ttableMessage = new List<byte>
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_IS,
            1, (byte)';',
            (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', (byte)';',
            8, 0, 0, 10,
            (byte)'u', (byte)'s', (byte)'-', (byte)'a', (byte)'s', (byte)'c', (byte)'i', (byte)'i', (byte)';',
            8, 0, 0, 10,
        };
        ttableMessage.AddRange(System.Linq.Enumerable.Repeat((byte)0x41, 2048));
        ttableMessage.Add((byte)Trigger.IAC);
        ttableMessage.Add((byte)Trigger.SE);

        await InterpretAndWaitAsync(server, ttableMessage.ToArray());

        await Assert.That(wasCallbackInvoked).IsFalse();
        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_REJECTED,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await server.DisposeAsync();
    }

    /// <summary>
    /// <c>IAC SB CHARSET REQUEST IAC SE</c> -- a REQUEST with no separator and no charset list at all --
    /// used to throw <see cref="System.ArgumentOutOfRangeException"/> reading the separator out of an
    /// empty array. It names nothing to choose from, so it is rejected the same way an offer with no
    /// charset this side supports is.
    /// </summary>
    [Test]
    public async Task EmptyRequestIsRejectedRatherThanThrowing()
    {
        byte[] negotiationOutput = null;
        ValueTask CaptureNegotiation(System.ReadOnlyMemory<byte> data) { negotiationOutput = data.ToArray(); return ValueTask.CompletedTask; }

        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(CaptureNegotiation)
            .AddPlugin<CharsetProtocol>());

        await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
        negotiationOutput = null;

        await InterpretAndWaitAsync(server, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await Assert.That(negotiationOutput).IsNotNull();
        await AssertByteArraysEqual(negotiationOutput, new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await server.DisposeAsync();
    }
}
