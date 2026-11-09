using System;
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
using TelnetNegotiationCore.Plugins;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class InputByteObservationTests : BaseTest
{
    private static TelnetInterpreterBuilder Builder() => new TelnetInterpreterBuilder()
        .UseMode(TelnetInterpreter.TelnetMode.Client)
        .UseLogger(silentLogger)
        .OnSubmit(NoOpSubmitCallback)
        .OnNegotiation(_ => ValueTask.CompletedTask);

    [Test]
    public async Task QueryArrivesBeforeBufferingWithoutLineOrPromptOrEcho()
    {
        var bytes = new List<byte>();
        var sent = new List<byte[]>();
        var submitted = new List<byte[]>();
        var firstWasUnbuffered = false;
        TelnetInterpreter interpreter = null;
        interpreter = await Builder()
            .ObserveForTest((b, _) =>
            {
                if (bytes.Count == 0) firstWasUnbuffered = !interpreter.HasPartialLine;
                bytes.Add(b);
                return ValueTask.CompletedTask;
            })
            .OnSubmit((line, _, _) => { submitted.Add(line); return ValueTask.CompletedTask; })
            .OnNegotiation(data => { sent.Add(data.ToArray()); return ValueTask.CompletedTask; })
            .BuildAsync();
        await using (interpreter)
        {
            foreach (var b in "\u001b[16t"u8.ToArray())
                await InterpretAndWaitAsync(interpreter, [b]);
            await AssertByteArraysEqual(bytes.ToArray(), "\u001b[16t"u8.ToArray());
            await Assert.That(firstWasUnbuffered).IsTrue();
            await Assert.That(submitted.Count).IsEqualTo(0);
            await Assert.That(sent.Count).IsEqualTo(0);
            await Assert.That(interpreter.PluginManager!.GetPlugin<EchoProtocol>()).IsNull();
            await InterpretAndWaitAsync(interpreter, [10]);
            await AssertByteArraysEqual(submitted.Single(), "\u001b[16t"u8.ToArray());
        }
    }

    [Test]
    public async Task NegotiationAndSubnegotiationAreExcludedAndLiteralIacIsObservedOnce()
    {
        var bytes = new List<byte>();
        await using var interpreter = await Builder()
            .ObserveForTest((b, _) => { bytes.Add(b); return ValueTask.CompletedTask; })
            .BuildAsync();
        byte[] wire = [27, 91, 255, 251, 99, 49, 255, 250, 99, 27, 91, 49, 54, 116, 255, 240, 54, 116, 255, 255];
        foreach (var b in wire) await InterpretAndWaitAsync(interpreter, [b]);
        await AssertByteArraysEqual(bytes.ToArray(), [27, 91, 49, 54, 116, 255]);
    }

    [Test]
    [Arguments(CarriageReturnMode.Drop)]
    [Arguments(CarriageReturnMode.Preserve)]
    [Arguments(CarriageReturnMode.EndOfLine)]
    public async Task OriginalLineControlBytesAreObservedExactlyOnceInEveryMode(CarriageReturnMode mode)
    {
        var bytes = new List<byte>();
        var lines = new List<string>();
        await using var interpreter = await Builder().WithCarriageReturnMode(mode)
            .ObserveForTest((b, _) => { bytes.Add(b); return ValueTask.CompletedTask; })
            .OnSubmit((line, encoding, _) => { lines.Add(encoding.GetString(line)); return ValueTask.CompletedTask; })
            .BuildAsync();
        byte[] wire = [13, 10, (byte)'a', 13, 13, 0, (byte)'b', 13, (byte)'c', 0, 13, 10, 10, 13];
        foreach (var b in wire) await InterpretAndWaitAsync(interpreter, [b]);
        await AssertByteArraysEqual(bytes.ToArray(), wire);
        var expected = mode switch
        {
            CarriageReturnMode.Preserve => new[] { "", "a\r\rb\rc\0", "" },
            CarriageReturnMode.EndOfLine => new[] { "", "a", "bc\0", "" },
            _ => new[] { "", "abc\0", "" },
        };
        await Assert.That(lines).IsEquivalentTo(expected);
    }

    [Test]
    public async Task LineBoundaryIsVisibleEvenWhenLineIsConsumedOrOverflows()
    {
        var bytes = new List<byte>();
        var submitted = 0;
        await using var interpreter = await Builder().WithMaxBufferSize(3)
            .ObserveForTest((b, _) => { bytes.Add(b); return ValueTask.CompletedTask; })
            .OnSubmit((_, _, _) => { submitted++; return ValueTask.CompletedTask; })
            .BuildAsync();
        interpreter.RegisterInputLineObserver((_, _) => ValueTask.FromResult<byte[]>(null));
        var wire = "\u001b[1\n6t\nlong\r\n"u8.ToArray();
        await InterpretAndWaitAsync(interpreter, wire);
        await AssertByteArraysEqual(bytes.ToArray(), wire);
        await Assert.That(submitted).IsEqualTo(0);
    }

    [Test]
    public async Task ObserverSeesBytesBeforeTerminalReportFilterAndDoesNotReplaceEcho()
    {
        var bytes = new List<byte>();
        var echoed = new List<byte>();
        var reports = 0;
        await using var interpreter = await Builder().UseMode(TelnetInterpreter.TelnetMode.Server)
            .ObserveForTest((b, _) => { bytes.Add(b); return ValueTask.CompletedTask; })
            .AddPlugin(new TerminalQueryProtocol().OnTerminalReport(_ => { reports++; return ValueTask.CompletedTask; }))
            .AddPlugin(new EchoProtocol().WithEchoHandler((b, _) => { echoed.Add(b); return ValueTask.CompletedTask; }))
            .BuildAsync();
        var wire = "\u001b[6;20;10ttext\n"u8.ToArray();
        await InterpretAndWaitAsync(interpreter, wire);
        await AssertByteArraysEqual(bytes.ToArray(), wire);
        await AssertByteArraysEqual(echoed.ToArray(), "text"u8.ToArray());
        await Assert.That(reports).IsEqualTo(1);
    }

    [Test]
    public async Task DefaultEchoStillNegotiatesAndEscapesLiteralIac()
    {
        var bytes = new List<byte>();
        var sent = new List<byte>();
        await using var interpreter = await Builder().UseMode(TelnetInterpreter.TelnetMode.Server)
            .ObserveForTest((b, _) => { bytes.Add(b); return ValueTask.CompletedTask; })
            .OnNegotiation(data => { sent.AddRange(data.ToArray()); return ValueTask.CompletedTask; })
            .AddPlugin(new EchoProtocol().UseDefaultEchoHandler())
            .BuildAsync();
        await InterpretAndWaitAsync(interpreter, [255, 253, 1]);
        sent.Clear();
        await InterpretAndWaitAsync(interpreter, [(byte)'a', 255, 255, (byte)'b', 13, 10]);
        await AssertByteArraysEqual(bytes.ToArray(), [(byte)'a', 255, (byte)'b', 13, 10]);
        await AssertByteArraysEqual(sent.ToArray(), [(byte)'a', 255, 255, (byte)'b']);
    }

    [Test]
    public async Task AsynchronousObserverFinishesBeforeLaterBytesAndLineSubmission()
    {
        var events = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var interpreter = await Builder()
            .ObserveForTest(async (b, _) =>
            {
                events.Add($"start:{b}");
                if (b == (byte)'a') { entered.SetResult(); await release.Task; }
                else await Task.Yield();
                events.Add($"end:{b}");
            })
            .OnSubmit((_, _, _) => { events.Add("submit"); return ValueTask.CompletedTask; })
            .BuildAsync();
        try
        {
            await interpreter.InterpretByteArrayAsync("ab\r\n"u8.ToArray());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(events).IsEquivalentTo(new[] { "start:97" });
        }
        finally { release.TrySetResult(); }
        await interpreter.WaitForProcessingAsync();
        await Assert.That(string.Join(",", events)).IsEqualTo("start:97,end:97,start:98,end:98,start:13,end:13,start:10,end:10,submit");
    }

    [Test]
    public async Task ObserverFailureDoesNotConsumeTheByteOrStopTheConnection()
    {
        var seen = new List<byte>();
        var lines = new List<string>();
        await using var interpreter = await Builder()
            .ObserveForTest(async (b, _) =>
            {
                seen.Add(b);
                await Task.Yield();
                throw new InvalidOperationException("observer failure");
            })
            .OnSubmit((line, encoding, _) => { lines.Add(encoding.GetString(line)); return ValueTask.CompletedTask; })
            .BuildAsync();
        await InterpretAndWaitAsync(interpreter, "ab\r\ncd\n"u8.ToArray());
        await AssertByteArraysEqual(seen.ToArray(), "ab\r\ncd\n"u8.ToArray());
        await Assert.That(lines).IsEquivalentTo(new[] { "ab", "cd" });
    }

    [Test]
    public async Task InflatedApplicationBytesAreObservedWithoutALineTerminator()
    {
        var seen = new List<byte>();
        await using var interpreter = await Builder()
            .AddPlugin<MCCPProtocol>()
            .ObserveForTest((b, _) => { seen.Add(b); return ValueTask.CompletedTask; })
            .BuildAsync();
        await InterpretAndWaitAsync(interpreter, [255, 251, 86]);
        using var sink = new MemoryStream();
        using var compressor = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true);
        compressor.Write([27, 91, 49, 54, 116, 255, 255]);
        compressor.Flush();
        await InterpretAndWaitAsync(interpreter, [255, 250, 86, 255, 240, .. sink.ToArray()]);
        await AssertByteArraysEqual(seen.ToArray(), [27, 91, 49, 54, 116, 255]);
    }

    [Test]
    public async Task ObserverGetsCurrentEncodingEvenAcrossDeferredBytes()
    {
        var encodings = new List<Encoding>();
        await using var interpreter = await Builder()
            .ObserveForTest(async (_, encoding) => { await Task.Yield(); encodings.Add(encoding); })
            .BuildAsync();
        interpreter.CurrentEncoding = Encoding.Latin1;
        await InterpretAndWaitAsync(interpreter, [0xe9, 13, 10]);
        await Assert.That(encodings.Count).IsEqualTo(3);
        await Assert.That(encodings.All(x => x == Encoding.Latin1)).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task SynchronousObserverDoesNotAddAPerByteAllocation()
    {
        const int count = 200_000;
        var observed = 0;
        await using var interpreter = await Builder().ObserveForTest((_, _) =>
        {
            observed++;
            return ValueTask.CompletedTask;
        }).BuildAsync();
        await using var baseline = await Builder().BuildAsync();
        var warmup = Encoding.ASCII.GetBytes(new string('x', count) + "\n");
        await InterpretAndWaitAsync(baseline, warmup);
        await InterpretAndWaitAsync(interpreter, warmup);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        await InterpretAndWaitAsync(baseline, warmup);
        var baselinePerByte = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)count;
        before = GC.GetTotalAllocatedBytes(precise: true);
        await InterpretAndWaitAsync(interpreter, warmup);
        var observedPerByte = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)count;
        // The channel/machine has its own allocation cost in Debug builds. Compare like for like:
        // 16 bytes of headroom allows scheduling noise but catches even one object per byte.
        await Assert.That(observedPerByte).IsLessThan(baselinePerByte + 16);
        await Assert.That(observed).IsEqualTo(2 * (count + 1));
    }

}

internal static class InputByteObservationTestExtensions
{
    internal static TelnetInterpreterBuilder ObserveForTest(this TelnetInterpreterBuilder builder,
        Func<byte, Encoding, ValueTask> observer) => builder.AddPlugin(new ObserverPlugin(observer));

    internal static TelnetInterpreterBuilder ObserveForTest<T>(this PluginConfigurationContext<T> context,
        Func<byte, Encoding, ValueTask> observer) where T : ITelnetProtocolPlugin
    {
        TelnetInterpreterBuilder builder = context;
        return builder.ObserveForTest(observer);
    }

    private sealed class ObserverPlugin(Func<byte, Encoding, ValueTask> observer) : TelnetProtocolPluginBase
    {
        public override Type ProtocolType => typeof(ObserverPlugin);
        public override string ProtocolName => "Test input observer";
        public override void ConfigureStateMachine(IProtocolContext context) =>
            context.Interpreter.RegisterInputByteObserver(observer, () => { });
    }
}
