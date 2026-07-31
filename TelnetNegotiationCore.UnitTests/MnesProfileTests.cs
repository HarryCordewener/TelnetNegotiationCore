using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

#nullable enable

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The MNES profile's vocabulary: the variable names a MUD client reports, spelled once, and the two
/// rules the standard states about them.
/// </summary>
/// <remarks>
/// What a client reports, and the guarantee that it reports nothing it was not given, is covered by
/// <see cref="EnvironPrivacyTests"/> and <see cref="ClientIdentityTests"/>. This file is about the
/// names themselves.
/// </remarks>
public class MnesProfileTests : BaseTest
{
    /// <summary>The vocabulary is the standard's, and every name in it satisfies its own rule.</summary>
    [Test]
    public async Task TheStandardVariableNamesAreAllLegalNames()
    {
        foreach (var name in MnesVariables.All)
        {
            await Assert.That(MnesVariables.IsLegalName(name)).IsTrue();
        }

        await Assert.That(MnesVariables.All).IsEquivalentTo(
        [
            "CHARSET", "CLIENT_NAME", "CLIENT_VERSION", "IPADDRESS", "MTTS", "TERMINAL_TYPE"
        ]);
    }

    /// <summary>
    /// MNES: <i>"Variables should solely exist of upper case letters and underscores."</i>
    /// </summary>
    [Test]
    public async Task ANameIsUpperCaseLettersAndUnderscores()
    {
        await Assert.That(MnesVariables.IsLegalName("CLIENT_NAME")).IsTrue();
        await Assert.That(MnesVariables.IsLegalName("client_name")).IsFalse();
        await Assert.That(MnesVariables.IsLegalName("CLIENT NAME")).IsFalse();
        await Assert.That(MnesVariables.IsLegalName("CLIENT-NAME")).IsFalse();
        await Assert.That(MnesVariables.IsLegalName("CLIENT_NAME_2")).IsFalse();
        await Assert.That(MnesVariables.IsLegalName(string.Empty)).IsFalse();
        await Assert.That(MnesVariables.IsLegalName(null)).IsFalse();
    }

    /// <summary>
    /// MNES: <i>"Values cannot contain the VAR, VAL, ESC, USERVAR, or IAC byte."</i>
    /// </summary>
    /// <remarks>
    /// This is a question an application can ask about a value it is about to configure. It is not a
    /// gate on the send path: NEW-ENVIRON is RFC 1572 first and MNES second, and RFC 1572 defines an
    /// ESC escape precisely so those bytes can be carried, so the writer escapes rather than refuses.
    /// A value that fails this predicate is one an MNES peer is entitled to reject, which is worth
    /// knowing before the connection rather than after.
    /// </remarks>
    [Test]
    public async Task AValueMayNotCarryAFramingByte()
    {
        await Assert.That(MnesVariables.IsLegalValue("MUINDEX-CRAWLER")).IsTrue();
        await Assert.That(MnesVariables.IsLegalValue(string.Empty)).IsTrue();
        await Assert.That(MnesVariables.IsLegalValue(null)).IsFalse();

        foreach (var framing in new[] { (char)0, (char)1, (char)2, (char)3, (char)255 })
        {
            await Assert.That(MnesVariables.IsLegalValue("bad" + framing + "value")).IsFalse();
        }
    }

    /// <summary>
    /// The names the library sends for a configured identity are the ones in the vocabulary, so the
    /// constants and the wire cannot drift apart.
    /// </summary>
    [Test]
    public async Task AnIdentityIsReportedUnderTheStandardNames()
    {
        byte[]? negotiation = null;

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
            .WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER")
            {
                Version = "0.1",
                TerminalType = "XTERM"
            })
            .AddPlugin<NewEnvironProtocol>()
                .WithClientEnvironmentVariables(new Dictionary<string, string>
                {
                    { MnesVariables.Charset, "UTF-8" },
                    { MnesVariables.IpAddress, "203.0.113.7" }
                })
            .BuildAsync();

        await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
        negotiation = null;

        await InterpretAndWaitAsync(client,
        [
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND,
            (byte)Trigger.IAC, (byte)Trigger.SE
        ]);

        var expected = new List<byte>
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS
        };
        ClientIdentityTests.AppendVariable(expected, MnesVariables.ClientName, "MUINDEX-CRAWLER");
        ClientIdentityTests.AppendVariable(expected, MnesVariables.ClientVersion, "0.1");
        ClientIdentityTests.AppendVariable(expected, MnesVariables.TerminalType, "XTERM");
        ClientIdentityTests.AppendVariable(expected, MnesVariables.Mtts, "516");
        ClientIdentityTests.AppendVariable(expected, MnesVariables.Charset, "UTF-8");
        ClientIdentityTests.AppendVariable(expected, MnesVariables.IpAddress, "203.0.113.7");
        expected.Add((byte)Trigger.IAC);
        expected.Add((byte)Trigger.SE);

        await AssertByteArraysEqual(negotiation!, expected.ToArray());

        await client.DisposeAsync();
    }

    /// <summary>
    /// Every name this library puts on the wire for an identity is one the profile allows, checked
    /// through the predicate rather than by eye.
    /// </summary>
    [Test]
    public async Task EveryNameTheLibrarySendsIsLegal()
    {
        var names = new[]
        {
            MnesVariables.ClientName, MnesVariables.ClientVersion, MnesVariables.TerminalType,
            MnesVariables.Mtts
        };

        await Assert.That(names.All(MnesVariables.IsLegalName)).IsTrue();
        await Assert.That(names.All(MnesVariables.All.Contains)).IsTrue();
    }
}
