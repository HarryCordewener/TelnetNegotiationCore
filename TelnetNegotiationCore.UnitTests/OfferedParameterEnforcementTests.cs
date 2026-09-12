#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// A peer answering AUTHENTICATION or ENCRYPT names the mechanism it has chosen, and nothing on the
/// wire obliges it to choose one this side offered. Honouring our own advertisement is this side's
/// job: a value we never offered is refused before the consumer's callback — and therefore before
/// any credential validation or decryption setup — rather than being handed over to be checked by
/// a consumer who may not think to.
/// </summary>
public class OfferedParameterEnforcementTests : BaseTest
{
    private static ValueTask NoSubmit(byte[] data, Encoding encoding, TelnetInterpreter t) => ValueTask.CompletedTask;

    private static async Task<TelnetInterpreter> ServerAsync(
        TelnetInterpreterBuilder builder, CapturingLogger log)
    {
        var server = await builder.BuildAsync();
        await Task.Delay(100);
        return server;
    }

    // ---------------------------------------------------------------- AUTHENTICATION

    [Test]
    public async Task ServerIgnoresAnAuthenticationTypeItNeverOffered()
    {
        var log = new CapturingLogger(logger);
        byte[]? received = null;

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(log)
            .OnSubmit(NoSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<AuthenticationProtocol>()
                .WithAuthenticationTypes(() => new ValueTask<List<(byte AuthType, byte Modifiers)>>(
                    [((byte)5, (byte)0), ((byte)6, (byte)2)]))       // SRP, RSA+MUTUAL
                .OnAuthenticationResponse(data => { received = data; return ValueTask.CompletedTask; })
            .BuildAsync();

        // The client accepts, which is when this side sends its SEND and so decides what it offered.
        await InterpretAndWaitAsync(server,
            [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.AUTHENTICATION]);

        // …and then answers with KERBEROS_V5, which was not on that list.
        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION,
            0,          // IS
            2, 0,       // KERBEROS_V5, no modifiers
            0x01, 0x02,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await Assert.That(received).IsNull();
        await Assert.That(log.Entries(LogLevel.Warning).Any(x => x.Contains("2"))).IsTrue();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerAcceptsAnAuthenticationTypeItOffered()
    {
        byte[]? received = null;

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<AuthenticationProtocol>()
                .WithAuthenticationTypes(() => new ValueTask<List<(byte AuthType, byte Modifiers)>>(
                    [((byte)5, (byte)0), ((byte)6, (byte)2)]))
                .OnAuthenticationResponse(data => { received = data; return ValueTask.CompletedTask; })
            .BuildAsync();

        await InterpretAndWaitAsync(server,
            [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.AUTHENTICATION]);

        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION,
            0,          // IS
            5, 0,       // SRP, no modifiers — offered
            0x01, 0x02, 0x03,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await Assert.That(received).IsNotNull();
        await Assert.That(received!).IsEquivalentTo(new byte[] { 0, 5, 0, 0x01, 0x02, 0x03 });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerWithNoAuthenticationTypesConfiguredIsUnchanged()
    {
        byte[]? received = null;

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<AuthenticationProtocol>()
                .OnAuthenticationResponse(data => { received = data; return ValueTask.CompletedTask; })
            .BuildAsync();

        await InterpretAndWaitAsync(server,
            [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.AUTHENTICATION]);

        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION,
            0, 5, 0, 0x01,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        // No provider means this side advertised nothing and already refuses with NULL; the guard
        // has no list to enforce and must not start dropping what a consumer asked to see.
        await Assert.That(received).IsNotNull();

        await server.DisposeAsync();
    }

    /// <summary>
    /// An empty list is an advertisement, not the absence of one: this side told the peer it
    /// accepts nothing, so nothing is what it accepts. That is the line between a configured
    /// provider returning no types and no provider at all, and it is deliberate.
    /// </summary>
    [Test]
    public async Task ServerOfferingAnEmptyListAcceptsNothing()
    {
        byte[]? received = null;

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<AuthenticationProtocol>()
                .WithAuthenticationTypes(() => new ValueTask<List<(byte AuthType, byte Modifiers)>>([]))
                .OnAuthenticationResponse(data => { received = data; return ValueTask.CompletedTask; })
            .BuildAsync();

        await InterpretAndWaitAsync(server,
            [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.AUTHENTICATION]);

        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.AUTHENTICATION,
            0, 5, 0, 0x01,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await Assert.That(received).IsNull();

        await server.DisposeAsync();
    }

    // ---------------------------------------------------------------- ENCRYPT

    [Test]
    public async Task ServerIgnoresAnEncryptionTypeItNeverOffered()
    {
        var log = new CapturingLogger(logger);
        byte[]? received = null;

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(log)
            .OnSubmit(NoSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EncryptionProtocol>()
                .WithEncryptionTypes(() => new ValueTask<List<byte>>([1, 3]))   // DES_CFB64, DES3_CFB64
                .OnEncryptionRequest(data => { received = data; return ValueTask.CompletedTask; })
            .BuildAsync();

        await InterpretAndWaitAsync(server,
            [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENCRYPT]);

        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            0,          // IS
            2,          // DES_OFB64 — not offered
            0x01, 0x02,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await Assert.That(received).IsNull();
        await Assert.That(log.Entries(LogLevel.Warning).Any(x => x.Contains("2"))).IsTrue();

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerAcceptsAnEncryptionTypeItOffered()
    {
        byte[]? received = null;

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EncryptionProtocol>()
                .WithEncryptionTypes(() => new ValueTask<List<byte>>([1, 3]))
                .OnEncryptionRequest(data => { received = data; return ValueTask.CompletedTask; })
            .BuildAsync();

        await InterpretAndWaitAsync(server,
            [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENCRYPT]);

        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            0,          // IS
            3,          // DES3_CFB64 — offered
            0x0A, 0x0B,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await Assert.That(received).IsNotNull();
        await Assert.That(received!).IsEquivalentTo(new byte[] { 0, 3, 0x0A, 0x0B });

        await server.DisposeAsync();
    }

    [Test]
    public async Task ServerWithNoEncryptionTypesConfiguredIsUnchanged()
    {
        byte[]? received = null;

        var server = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoSubmit)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<EncryptionProtocol>()
                .OnEncryptionRequest(data => { received = data; return ValueTask.CompletedTask; })
            .BuildAsync();

        await InterpretAndWaitAsync(server,
            [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENCRYPT]);

        await InterpretAndWaitAsync(server,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENCRYPT,
            0, 1, 0x01,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        await Assert.That(received).IsNotNull();

        await server.DisposeAsync();
    }
}
