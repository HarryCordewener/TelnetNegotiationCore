using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class TerminalCellSizeResponderTests : BaseTest
{
    private sealed class Peer
    {
        internal TelnetInterpreter Interpreter = null!;
        internal readonly TerminalQueryProtocol Queries = new();
        internal readonly ConcurrentQueue<string> Sent = new();
        internal readonly List<byte[]> Lines = [];
        internal string Replies => string.Concat(Sent.Where(x => x.StartsWith("\u001b[6;", StringComparison.Ordinal)));
    }

    private static async Task<Peer> BuildAsync(Func<TerminalCellSize> provider,
        Action<TelnetInterpreterBuilder> configure = null, Func<string, ValueTask> send = null)
    {
        var peer = new Peer();
        if (provider is not null) peer.Queries.WithCellSizeProvider(provider);
        var builder = new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client).UseLogger(silentLogger)
            .OnSubmit((line, _, _) => { peer.Lines.Add(line); return ValueTask.CompletedTask; })
            .OnNegotiation(bytes =>
            {
                var text = Encoding.ASCII.GetString(bytes.Span);
                peer.Sent.Enqueue(text);
                return send?.Invoke(text) ?? ValueTask.CompletedTask;
            }).AddPlugin(peer.Queries);
        configure?.Invoke(builder);
        peer.Interpreter = await builder.BuildAsync();
        return peer;
    }

    private static Task FeedAsync(Peer peer, string input) =>
        InterpretAndWaitAsync(peer.Interpreter, Encoding.ASCII.GetBytes(input));

    [Test]
    public async Task AProviderIsOptInAndServerModeRejectsIt()
    {
        var peer = await BuildAsync(null);
        await using (peer.Interpreter)
        {
            await FeedAsync(peer, "\u001b[16t");
            await peer.Queries.ReportCellSizeAsync();
            await Assert.That(peer.Replies).IsEqualTo("");
        }
        await Assert.That(async () => await BuildAsync(() => new(10, 24), b => b.UseMode(TelnetInterpreter.TelnetMode.Server)))
            .Throws<InvalidOperationException>();
        await Assert.That(() => new TerminalQueryProtocol().WithCellSizeProvider(null)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ProviderIsAvailableThroughTheFluentPluginChain()
    {
        var sent = new List<byte>();
        await using var interpreter = await new TelnetInterpreterBuilder()
            .UseMode(TelnetInterpreter.TelnetMode.Client).UseLogger(silentLogger)
            .OnSubmit(NoOpSubmitCallback)
            .OnNegotiation(bytes => { sent.AddRange(bytes.ToArray()); return ValueTask.CompletedTask; })
            .AddPlugin<TerminalQueryProtocol>().WithCellSizeProvider(() => new(10, 24))
            .BuildAsync();
        await InterpretAndWaitAsync(interpreter, "\u001b[16t"u8.ToArray());
        await AssertByteArraysEqual(sent.ToArray(), "\u001b[6;24;10t"u8.ToArray());
        await Assert.That(() => interpreter.PluginManager!.GetPlugin<TerminalQueryProtocol>()!
            .WithCellSizeProvider(() => new(12, 24))).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AnswersWithoutATerminatorAndPreservesApplicationBytes(bool fragmented)
    {
        var peer = await BuildAsync(() => new(10, 24));
        await using var interpreter = peer.Interpreter;
        var bytes = "before\u001b[16t\u001b[6;99;99tafter"u8.ToArray();
        if (fragmented)
            foreach (var b in bytes) await InterpretAndWaitAsync(interpreter, [b]);
        else await InterpretAndWaitAsync(interpreter, bytes);
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t");
        await Assert.That(peer.Lines.Count).IsEqualTo(0);
        await FeedAsync(peer, "\r\n");
        await AssertByteArraysEqual(peer.Lines.Single(), bytes);
        await Assert.That(interpreter.PluginManager!.GetPlugin<EchoProtocol>()).IsNull();
    }

    [Test]
    [Arguments("\u001b]title\r\0\u001b[16t\a")]
    [Arguments("\u001bPdata\r\0\u001b[16t\u001b\\")]
    [Arguments("\u001b^data\u001b[16t\u001b\\")]
    [Arguments("\u001b]title\u001b[16t\a")]
    [Arguments("\u001bPdata\u001b[16t\u001b\\")]
    [Arguments("\u001b_Gpayload\u001b[16t\u001b\\")]
    [Arguments("\u001bXdata\u001b[16t\u001b\\")]
    [Arguments("\u001b[?16t\u001b[16;1t\u001b[016t\u001b[16 t")]
    [Arguments("\u001b[1\r6t\u001b[1\n6t\u001b[1\u00186t\u001b[1\u001a6t")]
    public async Task IgnoresNestedOrMalformedQueriesAndRecovers(string input)
    {
        var peer = await BuildAsync(() => new(10, 24));
        await using var interpreter = peer.Interpreter;
        foreach (var b in Encoding.ASCII.GetBytes(input)) await InterpretAndWaitAsync(interpreter, [b]);
        await Assert.That(peer.Replies).IsEqualTo("");
        await FeedAsync(peer, "\u001b[16t");
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t");
    }

    [Test]
    public async Task RefreshWaitsForPeerInterestAndReportsOnlyChangedValidMeasurements()
    {
        TerminalCellSize size = new(10, 24);
        var peer = await BuildAsync(() => size);
        await using var interpreter = peer.Interpreter;
        await peer.Queries.ReportCellSizeAsync();
        await Assert.That(peer.Replies).IsEqualTo("");
        await FeedAsync(peer, "\u001b[16t");
        await peer.Queries.ReportCellSizeAsync();
        size = new(16, 40);
        await peer.Queries.ReportCellSizeAsync();
        foreach (var invalid in new TerminalCellSize[] { null, new(0, 24), new(-1, 24), new(10, 0), new(10, 65536), new(65536, 24) })
        {
            size = invalid;
            await peer.Queries.ReportCellSizeAsync();
        }
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t\u001b[6;40;16t");
        size = new(16, 40);
        await FeedAsync(peer, "\u001b[16t");
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t\u001b[6;40;16t\u001b[6;40;16t");
    }

    [Test]
    public async Task UnknownMeasurementsCanBecomeAvailableAfterAQuery()
    {
        TerminalCellSize size = null;
        var peer = await BuildAsync(() => size);
        await using var interpreter = peer.Interpreter;
        await FeedAsync(peer, "\u001b[16t");
        await Assert.That(peer.Replies).IsEqualTo("");
        size = new(65535, 65535);
        await peer.Queries.ReportCellSizeAsync();
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;65535;65535t");
    }

    [Test]
    public async Task ConcurrentRefreshRemeasuresInsideTheGateAndPreservesABAOrder()
    {
        TerminalCellSize size = new(10, 24);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = await BuildAsync(() => size, send: async text =>
        {
            if (text == "\u001b[6;40;16t") { entered.TrySetResult(); await release.Task; }
        });
        await using var interpreter = peer.Interpreter;
        await FeedAsync(peer, "\u001b[16t");
        size = new(16, 40);
        var sendingB = peer.Queries.ReportCellSizeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        size = new(10, 24);
        var sendingA = peer.Queries.ReportCellSizeAsync().AsTask();
        release.TrySetResult();
        await Task.WhenAll(sendingB, sendingA).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t\u001b[6;40;16t\u001b[6;24;10t");
    }

    [Test]
    public async Task DisableStopsQueuedReportsAndReenableRequiresANewQuery()
    {
        TerminalCellSize size = new(10, 24);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = await BuildAsync(() => size, send: async text =>
        {
            if (text == "\u001b[6;40;16t") { entered.TrySetResult(); await release.Task; }
        });
        await using var interpreter = peer.Interpreter;
        await FeedAsync(peer, "\u001b[16t");
        size = new(16, 40);
        var active = peer.Queries.ReportCellSizeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        size = new(20, 48);
        var queued = peer.Queries.ReportCellSizeAsync().AsTask();
        var disabling = peer.Queries.OnDisabledAsync().AsTask();
        release.TrySetResult(); // The host closes/cancels transport before awaiting disable if a write is blocked.
        await Task.WhenAll(active, queued, disabling).WaitAsync(TimeSpan.FromSeconds(5));
        await FeedAsync(peer, "\u001b[16t");
        await peer.Queries.ReportCellSizeAsync();
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t\u001b[6;40;16t");
        await peer.Queries.OnEnabledAsync();
        await peer.Queries.ReportCellSizeAsync();
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t\u001b[6;40;16t");
        await FeedAsync(peer, "\u001b[16t");
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t\u001b[6;40;16t\u001b[6;48;20t");
    }

    [Test]
    public async Task InterpreterDisposalCancelsQueuedRefreshes()
    {
        TerminalCellSize size = new(10, 24);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = await BuildAsync(() => size, send: async text =>
        {
            if (text == "\u001b[6;40;16t") { entered.TrySetResult(); await release.Task; }
        });
        await FeedAsync(peer, "\u001b[16t");
        size = new(16, 40);
        var active = peer.Queries.ReportCellSizeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        size = new(20, 48);
        var queued = peer.Queries.ReportCellSizeAsync().AsTask();
        var disposal = peer.Interpreter.DisposeAsync().AsTask();
        release.TrySetResult();
        await Task.WhenAll(active, queued, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        await peer.Queries.ReportCellSizeAsync();
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t\u001b[6;40;16t");
    }

    [Test]
    public async Task NegotiationPayloadsAndEncodedNonAsciiControlsDoNotTriggerAReply()
    {
        var peer = await BuildAsync(() => new(10, 24));
        await using var interpreter = peer.Interpreter;
        await InterpretAndWaitAsync(interpreter, [255, 250, 99, 27, 91, 49, 54, 116, 255, 240, 0x9b, 49, 54, 116]);
        interpreter.CurrentEncoding = Encoding.Unicode;
        await InterpretAndWaitAsync(interpreter, Encoding.Unicode.GetBytes("\u001b[16t"));
        await Assert.That(peer.Replies).IsEqualTo("");
        interpreter.CurrentEncoding = Encoding.Latin1;
        await FeedAsync(peer, "\u001b[16t");
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t");
    }

    [Test]
    public async Task RefreshIsSuppressedAfterSwitchingToAnIncompatibleEncoding()
    {
        TerminalCellSize size = new(10, 24);
        var peer = await BuildAsync(() => size);
        await using var interpreter = peer.Interpreter;
        await FeedAsync(peer, "\u001b[16t");
        interpreter.CurrentEncoding = Encoding.Unicode;
        size = new(16, 40);
        await peer.Queries.ReportCellSizeAsync();
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t");
        interpreter.CurrentEncoding = Encoding.Latin1;
        await peer.Queries.ReportCellSizeAsync();
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t\u001b[6;40;16t");
    }

    [Test]
    public async Task CompressionAndInterleavedNegotiationStillDeliverTheQuery()
    {
        var peer = await BuildAsync(() => new(10, 24), b => b.AddPlugin<MCCPProtocol>());
        await using var interpreter = peer.Interpreter;
        await InterpretAndWaitAsync(interpreter, [255, 251, 86]);
        using var sink = new MemoryStream();
        using var compressor = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true);
        compressor.Write([27, 91, 49, 255, 241, 54, 116]);
        compressor.Flush();
        await InterpretAndWaitAsync(interpreter, [255, 250, 86, 255, 240, .. sink.ToArray()]);
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LineResetsParserEvenWhenConsumedOrOverlong(bool overflow)
    {
        var peer = await BuildAsync(() => new(10, 24), b => { if (overflow) b.WithMaxBufferSize(3); });
        await using var interpreter = peer.Interpreter;
        if (!overflow) interpreter.RegisterInputLineObserver((_, _) => ValueTask.FromResult<byte[]>(null));
        await FeedAsync(peer, "\u001b]unfinished\n\u001b[16t");
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t");
    }

    [Test]
    [Arguments(CarriageReturnMode.Drop)]
    [Arguments(CarriageReturnMode.Preserve)]
    [Arguments(CarriageReturnMode.EndOfLine)]
    public async Task CarriageReturnNullResetsOnlyWhenItActuallySubmitsALine(CarriageReturnMode mode)
    {
        var peer = await BuildAsync(() => new(10, 24), b => b.WithCarriageReturnMode(mode));
        await using var interpreter = peer.Interpreter;
        await FeedAsync(peer, "\u001b]unfinished\r\0\u001b[16t");
        var lineEnded = mode == CarriageReturnMode.EndOfLine;
        await Assert.That(peer.Replies).IsEqualTo(lineEnded ? "\u001b[6;24;10t" : "");
        await Assert.That(peer.Lines.Count).IsEqualTo(lineEnded ? 1 : 0);
        // In Drop/Preserve, CR NUL did not close OSC: only BEL releases its shielding.
        await FeedAsync(peer, "\a\u001b[16t");
        await Assert.That(peer.Replies).IsEqualTo(lineEnded
            ? "\u001b[6;24;10t\u001b[6;24;10t" : "\u001b[6;24;10t");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MarkedPromptResetsTheQueryParser(bool eor)
    {
        var peer = await BuildAsync(() => new(10, 24), b =>
        {
            if (eor) b.AddPlugin<EORProtocol>();
            else b.AddPlugin<SuppressGoAheadProtocol>();
        });
        await using var interpreter = peer.Interpreter;
        if (eor) await InterpretAndWaitAsync(interpreter, [255, 251, 25]);
        await FeedAsync(peer, "\u001b]unfinished");
        await InterpretAndWaitAsync(interpreter, [255, (byte)(eor ? 239 : 249)]);
        await FeedAsync(peer, "\u001b[16t");
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t");
    }

    [Test]
    public async Task InferredPromptResetsTheQueryParser()
    {
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = await BuildAsync(() => new(10, 24), b => b.AddPlugin(new PacketPatchProtocol()
            .WithHoldTime(TimeSpan.FromMilliseconds(30))
            .OnPrompt(() => { prompted.TrySetResult(); return ValueTask.CompletedTask; })));
        await using var interpreter = peer.Interpreter;
        await FeedAsync(peer, "\u001b]unfinished");
        await prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await FeedAsync(peer, "\u001b[16t");
        await Assert.That(peer.Replies).IsEqualTo("\u001b[6;24;10t");
    }

    [Test]
    public async Task ProviderFailureDoesNotDropInputAndAFailedSendCanBeRetried()
    {
        var failProvider = true;
        var failSend = true;
        var peer = await BuildAsync(() => failProvider ? throw new InvalidOperationException("measure failed") : new(10, 24),
            send: _ => failSend ? throw new IOException("write failed") : ValueTask.CompletedTask);
        await using var interpreter = peer.Interpreter;
        await FeedAsync(peer, "\u001b[16ttext\n");
        await AssertByteArraysEqual(peer.Lines.Single(), "\u001b[16ttext"u8.ToArray());
        failProvider = false;
        await Assert.That(async () => await peer.Queries.ReportCellSizeAsync()).Throws<IOException>();
        failSend = false;
        await peer.Queries.ReportCellSizeAsync();
        await Assert.That(peer.Sent.Count).IsEqualTo(2);
    }
}
