#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
/// A peer that compresses well is cheap to listen to; a peer that compresses <i>absurdly</i> well is
/// buying the server's CPU with its own bandwidth. Four kilobytes of deflate holding four megabytes
/// of zeros costs this process about 430 ms of a core in Release — roughly 430 times what the same
/// four kilobytes of plain telnet would — and nothing downstream notices, because the line buffer's
/// ceiling is about memory, not about work done getting there.
/// </summary>
public class MCCPExpansionLimitTests : BaseTest
{
    private static readonly byte[] WillMccp2 =
        [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MCCP2];

    private static readonly byte[] StartMccp2 =
        [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MCCP2, (byte)Trigger.IAC, (byte)Trigger.SE];

    private static byte[] Deflate(byte[] payload)
    {
        using var sink = new MemoryStream();
        using (var z = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(payload, 0, payload.Length);
            z.Flush();
        }

        return sink.ToArray();
    }

    private static async Task<(TelnetInterpreter Client, CapturingLogger Log, List<(int, bool)> Events)>
        ClientUnderMccp2Async()
    {
        var log = new CapturingLogger(logger);
        var events = new List<(int, bool)>();

        var builder = new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(log)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask);

        var client = await BuildAndWaitAsync(builder
            .AddPlugin<MCCPProtocol>()
                .OnCompressionEnabled((v, e) => { events.Add((v, e)); return ValueTask.CompletedTask; }));

        await InterpretAndWaitAsync(client, WillMccp2);
        await InterpretAndWaitAsync(client, StartMccp2);

        return (client, log, events);
    }

    [Test]
    public async Task ACompressionBombStopsTheInflater()
    {
        var (client, log, events) = await ClientUnderMccp2Async();
        var plugin = client.PluginManager!.GetPlugin<MCCPProtocol>()!;

        await Assert.That(plugin.IsMCCP2Enabled).IsTrue();

        // 4 MiB of zeros in about 4 KiB of deflate: a ratio of roughly 1,000 to 1.
        await InterpretAndWaitAsync(client, Deflate(new byte[4 * 1024 * 1024]));

        // A generous wait, and the finding in one line: the bomb is refused a little past a mebibyte
        // of output, which is about a thousand wire bytes in — and getting that far already costs
        // this process the best part of a second.
        await PollUntilAsync(() => !plugin.IsMCCP2Enabled, timeoutMs: 30_000);

        await Assert.That(plugin.IsMCCP2Enabled).IsFalse();
        await Assert.That(log.Entries(LogLevel.Error).Any(x => x.Contains("expand"))).IsTrue();
        await Assert.That(events).Contains((2, false));

        await client.DisposeAsync();
    }

    [Test]
    public async Task AnOrdinaryStreamIsLeftAlone()
    {
        var (client, log, _) = await ClientUnderMccp2Async();
        var plugin = client.PluginManager!.GetPlugin<MCCPProtocol>()!;

        // What a MUD actually sends: English text, which deflate gets maybe five-fold.
        var line = Encoding.ASCII.GetBytes(
            "You are standing in an open field west of a white house, with a boarded front door.\r\n");
        var realistic = Enumerable.Range(0, 4000).SelectMany(_ => line).ToArray();

        await InterpretAndWaitAsync(client, Deflate(realistic));

        await Assert.That(plugin.IsMCCP2Enabled).IsTrue();
        await Assert.That(log.Entries(LogLevel.Error)).IsEmpty();

        await client.DisposeAsync();
    }

    [Test]
    public async Task TheCeilingIsConfigurable()
    {
        var client = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client)
            .UseLogger(logger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(_ => ValueTask.CompletedTask)
            .AddPlugin<MCCPProtocol>()
                .WithMaxExpansionRatio(2));       // absurdly strict, to prove the knob is wired

        await InterpretAndWaitAsync(client, WillMccp2);
        await InterpretAndWaitAsync(client, StartMccp2);

        var plugin = client.PluginManager!.GetPlugin<MCCPProtocol>()!;

        var line = Encoding.ASCII.GetBytes(
            "You are standing in an open field west of a white house, with a boarded front door.\r\n");
        var realistic = Enumerable.Range(0, 40000).SelectMany(_ => line).ToArray();

        await InterpretAndWaitAsync(client, Deflate(realistic));
        await PollUntilAsync(() => !plugin.IsMCCP2Enabled, timeoutMs: 30_000);

        await Assert.That(plugin.IsMCCP2Enabled).IsFalse();

        await client.DisposeAsync();
    }
}
