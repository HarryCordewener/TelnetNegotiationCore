#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Every plugin setting has to be reachable from the builder chain, because that is the only place a
/// consumer can set it before negotiation starts: a plugin fetched with <c>GetPlugin</c> after
/// <c>BuildAsync</c> has already had its initial negotiation sent. ENCRYPT's five callbacks and
/// CHARSET's <c>OnCharsetChange</c> had no <see cref="PluginConfigurationContext{T}"/> extension, so
/// the documented chain did not compile. These tests hold that door open.
/// </summary>
public class PluginFluentConfigurationTests : BaseTest
{
    private static ValueTask NoSubmit(byte[] data, Encoding encoding, TelnetInterpreter t) => ValueTask.CompletedTask;

    [Test]
    public async Task EncryptionCallbacksConfiguredOnTheChainAreWired()
    {
        byte[]? offeredTypes = null;

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EncryptionProtocol>()
                .WithEncryptionTypes(() => new ValueTask<List<byte>>(new List<byte> { 1, 3 }))
                .OnEncryptionSupport(types =>
                {
                    offeredTypes = types;
                    return new ValueTask<byte[]?>((byte[]?)null);   // decline: NULL response
                })
                .OnEncryptionRequest(_ => ValueTask.CompletedTask)
                .OnEncryptionStart(_ => ValueTask.CompletedTask)
                .OnEncryptionEnd(() => ValueTask.CompletedTask)
            .BuildAsync();

        // Server asks, then offers DES_CFB64 and DES3_CFB64.
        await client.InterpretByteArrayAsync(
            new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.ENCRYPT });
        await client.WaitForProcessingAsync();

        await client.InterpretByteArrayAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT, (byte)Trigger.SEND,
            1, 3,
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();

        // The payload is the subnegotiation body as it arrived: the SUPPORT command byte, then the
        // types. Documented as-is on docs/protocols/encryption.md rather than trimmed here, because
        // trimming it would change what every existing consumer's callback is handed.
        await Assert.That(offeredTypes).IsNotNull();
        await Assert.That(offeredTypes!).IsEquivalentTo(new byte[] { 1, 1, 3 });

        await client.DisposeAsync();
    }

    /// <summary>
    /// The same shape on the authentication side, asserted so the documentation can state it: the
    /// callback is handed the subnegotiation body, command byte included.
    /// </summary>
    [Test]
    public async Task AuthenticationRequestCallbackReceivesTheCommandByteFirst()
    {
        byte[]? offered = null;

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<AuthenticationProtocol>()
                .OnAuthenticationRequest(pairs =>
                {
                    offered = pairs;
                    return new ValueTask<byte[]?>((byte[]?)null);   // decline: NULL response
                })
            .BuildAsync();

        await client.InterpretByteArrayAsync(
            new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.AUTHENTICATION });
        await client.WaitForProcessingAsync();

        await client.InterpretByteArrayAsync(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION,
            1,        // SEND
            5, 0,     // SRP, no modifiers
            6, 2,     // RSA, mutual
            (byte)Trigger.IAC, (byte)Trigger.SE
        });
        await client.WaitForProcessingAsync();

        await Assert.That(offered).IsNotNull();
        await Assert.That(offered!).IsEquivalentTo(new byte[] { 1, 5, 0, 6, 2 });

        await client.DisposeAsync();
    }

    [Test]
    public async Task CharsetChangeCallbackConfiguredOnTheChainIsWired()
    {
        var reported = new List<Encoding>();

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<CharsetProtocol>()
                .WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
                .OnCharsetChange(encoding =>
                {
                    reported.Add(encoding);
                    return ValueTask.CompletedTask;
                })
            .BuildAsync();

        await client.InterpretByteArrayAsync(
            new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET });
        await client.WaitForProcessingAsync();

        var request = new List<byte>
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
            (byte)';'
        };
        request.AddRange(Encoding.ASCII.GetBytes("iso-8859-1"));
        request.AddRange(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE });

        await client.InterpretByteArrayAsync(request.ToArray());
        await client.WaitForProcessingAsync();

        await Assert.That(client.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");
        await Assert.That(reported.Count).IsEqualTo(1);
        await Assert.That(reported[0].WebName).IsEqualTo("iso-8859-1");

        await client.DisposeAsync();
    }
}
