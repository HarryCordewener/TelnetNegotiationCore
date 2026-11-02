using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// What each send method puts on the wire, as docs/guides/line-endings.md describes it. Issue #156.
/// </summary>
public class OutboundFramingTests : BaseTest
{
    private const byte IAC = (byte)Trigger.IAC;

    private static async Task<(TelnetInterpreter Server, List<byte[]> Sent)> ServerAsync(params byte[][] negotiation)
    {
        var sent = new List<byte[]>();
        var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Server)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { lock (sent) { sent.Add(bytes.ToArray()); } return default; })
            .AddPlugin<EORProtocol>()
            .AddPlugin<SuppressGoAheadProtocol>());
        foreach (var bytes in negotiation)
        {
            await InterpretAndWaitAsync(server, bytes);
        }
        await Task.Delay(100);
        lock (sent) { sent.Clear(); }
        return (server, sent);
    }

    private static byte[] Last(List<byte[]> sent)
    {
        lock (sent) { return sent.Last(); }
    }

    [Test]
    [Arguments("hello", "hello\r\n")]
    [Arguments("hello\r\n", "hello\r\n\r\n")]
    [Arguments("", "\r\n")]
    [Arguments("one\r\ntwo", "one\r\ntwo\r\n")]
    public async Task SendAsyncAlwaysAppendsCrLf(string text, string expected)
    {
        var (server, sent) = await ServerAsync();
        await using var _ = server;

        await server.SendAsync(Encoding.ASCII.GetBytes(text));

        await AssertByteArraysEqual(Last(sent), Encoding.ASCII.GetBytes(expected));
    }

    [Test]
    public async Task SendAsyncDoublesIacBeforeTheTerminator()
    {
        var (server, sent) = await ServerAsync();
        await using var _ = server;

        await server.SendAsync([(byte)'a', IAC, (byte)'b']);

        await AssertByteArraysEqual(Last(sent), [(byte)'a', IAC, IAC, (byte)'b', (byte)'\r', (byte)'\n']);
    }

    [Test]
    public async Task SendPromptAsyncEndsWithGoAheadByDefault()
    {
        var (server, sent) = await ServerAsync();
        await using var _ = server;

        await server.SendPromptAsync([(byte)'>', IAC]);

        await AssertByteArraysEqual(Last(sent), [(byte)'>', IAC, IAC, IAC, (byte)Trigger.GA]);
    }

    [Test]
    public async Task SendPromptAsyncEndsWithEorOnceThePeerAsksForIt()
    {
        var (server, sent) = await ServerAsync([IAC, (byte)Trigger.DO, (byte)Trigger.TELOPT_EOR]);
        await using var _ = server;

        await server.SendPromptAsync([(byte)'>']);

        await AssertByteArraysEqual(Last(sent), [(byte)'>', IAC, (byte)Trigger.EOR]);
    }

    [Test]
    public async Task SendPromptAsyncEndsWithCrLfWhenGoAheadIsSuppressedAndThereIsNoEor()
    {
        var (server, sent) = await ServerAsync([IAC, (byte)Trigger.DO, (byte)Trigger.SUPPRESSGOAHEAD]);
        await using var _ = server;

        await server.SendPromptAsync([(byte)'>']);

        await AssertByteArraysEqual(Last(sent), [(byte)'>', (byte)'\r', (byte)'\n']);
    }

    [Test]
    public async Task WriteToNetworkAsyncAddsNothing()
    {
        var (server, sent) = await ServerAsync();
        await using var _ = server;

        await server.WriteToNetworkAsync(new byte[] { (byte)'a', IAC, (byte)Trigger.NOP });

        await AssertByteArraysEqual(Last(sent), [(byte)'a', IAC, (byte)Trigger.NOP]);
    }
}
