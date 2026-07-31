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
/// What a client says about itself over NEW-ENVIRON, and — first — what it must not say.
/// </summary>
public class MnesProfileTests : BaseTest
{
    /// <summary>
    /// A client that was told nothing reports nothing, and above all not the machine's login account.
    /// </summary>
    /// <remarks>
    /// This is the regression that matters. The previous implementation answered every SEND with
    /// <c>USER=$USER</c> and <c>LANG=en_US.UTF-8</c>, taken from the environment of whoever was
    /// running the client — so any MUD server that negotiated NEW-ENVIRON learned the operating-system
    /// account name of a player who had never been asked. There was no way for a consumer to turn it
    /// off; the dictionary was a local in a private method.
    /// </remarks>
    [Test]
    public async Task AClientToldNothingSendsNothingAndNeverTheOsUsername()
    {
        var (client, sent) = await ClientAsync(configure: null);

        await RequestAsync(client, variables: Array.Empty<string>());

        var answer = LastIs(sent);

        await Assert.That(answer).IsNotNull();
        await Assert.That(Ascii(answer!)).DoesNotContain("USER");
        await Assert.That(Ascii(answer!)).DoesNotContain(Environment.UserName);
        await Assert.That(Ascii(answer!)).DoesNotContain("LANG");

        // An empty IS rather than silence: a well-formed "none of those", so a server is not left
        // waiting on a reply that is never coming.
        await Assert.That(answer!).IsEquivalentTo(new byte[]
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS,
            (byte)Trigger.IAC, (byte)Trigger.SE,
        });

        await client.DisposeAsync();
    }

    /// <summary>An application's own name reaches the wire, which is the whole point of MNES.</summary>
    [Test]
    public async Task AConfiguredProfileIsWhatGetsReported()
    {
        var (client, sent) = await ClientAsync(p => p.ReportVariables(new Dictionary<string, string>
        {
            [MnesVariables.ClientName] = "MUINDEX-CRAWLER",
            [MnesVariables.ClientVersion] = "0.1",
        }));

        await RequestAsync(client, variables: Array.Empty<string>());

        var answer = Ascii(LastIs(sent)!);

        await Assert.That(answer).Contains("CLIENT_NAME");
        await Assert.That(answer).Contains("MUINDEX-CRAWLER");
        await Assert.That(answer).Contains("CLIENT_VERSION");

        await client.DisposeAsync();
    }

    /// <summary>
    /// A SEND naming several variables gets several answers, and only the ones it named.
    /// </summary>
    /// <remarks>
    /// The requested names were parsed and then discarded: each <c>VAR</c> cleared the buffer the
    /// previous name was in, so a server asking for three variables left only the third — and the
    /// send path ignored even that, answering with its own fixed pair regardless.
    /// </remarks>
    [Test]
    public async Task EveryRequestedVariableIsAnsweredAndNothingElseIs()
    {
        var (client, sent) = await ClientAsync(p => p.ReportVariables(new Dictionary<string, string>
        {
            [MnesVariables.ClientName] = "TESTCLIENT",
            [MnesVariables.ClientVersion] = "9.9",
            [MnesVariables.Charset] = "UTF-8",
        }));

        await RequestAsync(client, new[] { MnesVariables.ClientName, MnesVariables.Charset });

        var answer = Ascii(LastIs(sent)!);

        await Assert.That(answer).Contains("CLIENT_NAME");
        await Assert.That(answer).Contains("TESTCLIENT");
        await Assert.That(answer).Contains("CHARSET");
        await Assert.That(answer).Contains("UTF-8");

        // Held but not asked for, so not volunteered.
        await Assert.That(answer).DoesNotContain("CLIENT_VERSION");

        await client.DisposeAsync();
    }

    /// <summary>A variable we do not hold is omitted rather than answered with a blank.</summary>
    [Test]
    public async Task AVariableWeDoNotHoldIsOmitted()
    {
        var (client, sent) = await ClientAsync(p =>
            p.ReportVariable(MnesVariables.ClientName, "TESTCLIENT"));

        await RequestAsync(client, new[] { MnesVariables.ClientName, MnesVariables.IpAddress });

        var answer = Ascii(LastIs(sent)!);

        await Assert.That(answer).Contains("CLIENT_NAME");
        await Assert.That(answer).DoesNotContain("IPADDRESS");

        await client.DisposeAsync();
    }

    /// <summary>
    /// A value carrying a framing byte is refused by the caller rather than sent as a broken stream.
    /// </summary>
    [Test]
    public async Task AValueThatWouldCorruptTheSubnegotiationIsRefused()
    {
        var protocol = new NewEnvironProtocol();

        await Assert.That(() => protocol.ReportVariable(MnesVariables.ClientName, "bad" + (char)255 + "value"))
            .Throws<ArgumentException>();
        await Assert.That(() => protocol.ReportVariable(MnesVariables.ClientName, "bad" + (char)1 + "value"))
            .Throws<ArgumentException>();
        await Assert.That(() => protocol.ReportVariable("client name", "fine"))
            .Throws<ArgumentException>();
        await Assert.That(() => protocol.ReportVariable("CLIENT_NAME", "fine"))
            .ThrowsNothing();
    }

    /// <summary>The vocabulary is the standard's, spelled once.</summary>
    [Test]
    public async Task TheStandardVariableNamesAreAllLegalNames()
    {
        foreach (var name in MnesVariables.All)
        {
            await Assert.That(MnesVariables.IsLegalName(name)).IsTrue();
        }

        await Assert.That(MnesVariables.All).Contains("CLIENT_NAME");
        await Assert.That(MnesVariables.All).Contains("MTTS");
    }

    private async Task<(TelnetInterpreter Client, List<byte[]> Sent)> ClientAsync(
        Action<NewEnvironProtocol>? configure)
    {
        var sent = new List<byte[]>();
        var protocol = new NewEnvironProtocol();

        configure?.Invoke(protocol);

        var client = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
            .OnNegotiation(bytes =>
            {
                sent.Add(bytes.ToArray());
                return ValueTask.CompletedTask;
            })
            .AddPlugin(protocol)
            .BuildAsync();

        await client.InterpretByteArrayAsync(
            new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NEWENVIRON });
        await client.WaitForProcessingAsync();

        return (client, sent);
    }

    /// <summary>Sends the server half: <c>IAC SB NEW-ENVIRON SEND [VAR name]... IAC SE</c>.</summary>
    private static async Task RequestAsync(TelnetInterpreter client, IReadOnlyList<string> variables)
    {
        var request = new List<byte>
        {
            (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND,
        };

        foreach (var name in variables)
        {
            request.Add((byte)Trigger.NEWENVIRON_VAR);
            request.AddRange(Encoding.ASCII.GetBytes(name));
        }

        request.Add((byte)Trigger.IAC);
        request.Add((byte)Trigger.SE);

        await client.InterpretByteArrayAsync(request.ToArray());
        await client.WaitForProcessingAsync();
    }

    /// <summary>The last NEW-ENVIRON IS subnegotiation the client wrote, or null.</summary>
    private static byte[]? LastIs(List<byte[]> sent) =>
        sent.LastOrDefault(b =>
            b.Length >= 4
            && b[0] == (byte)Trigger.IAC
            && b[1] == (byte)Trigger.SB
            && b[2] == (byte)Trigger.NEWENVIRON
            && b[3] == (byte)Trigger.IS);

    private static string Ascii(byte[] bytes) => Encoding.ASCII.GetString(bytes);
}
