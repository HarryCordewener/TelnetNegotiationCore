using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Tests for the inbound/outbound byte transform seam itself, independently of MCCP: a transform is
/// disposed when it is swapped out, and writes are in flight from application threads while the
/// byte-processing loop installs and removes them.
/// </summary>
public class ByteStreamTransformTests : BaseTest
{
	/// <summary>
	/// A plugin that does nothing except hand its context to the test, so the seam can be driven
	/// through the same public API a real protocol uses.
	/// </summary>
	private class SeamProbePlugin : TelnetProtocolPluginBase
	{
		public override Type ProtocolType => typeof(SeamProbePlugin);
		public override string ProtocolName => "Seam Probe";

		public IProtocolContext ProbeContext => Context;

		public override void ConfigureStateMachine(Stateless.StateMachine<State, Trigger> stateMachine, IProtocolContext context)
		{
		}
	}

	/// <summary>
	/// An outbound transform that parks inside <see cref="Encode"/> until the test lets it out, and
	/// remembers whether it was disposed while it was in there.
	/// </summary>
	private sealed class GatedOutboundTransform : IOutboundByteTransform
	{
		private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly ManualResetEventSlim _release = new(false);
		private volatile bool _disposed;
		private volatile bool _encoding;

		public Task Entered => _entered.Task;
		public bool DisposedWhileEncoding { get; private set; }

		public void Release() => _release.Set();

		public ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> data)
		{
			_encoding = true;
			_entered.TrySetResult();
			_release.Wait();
			_encoding = false;

			// A real encoder here is a zlib deflater: writing to it after Dispose is an
			// ObjectDisposedException at best and native use-after-free at worst.
			ObjectDisposedException.ThrowIf(_disposed, this);
			return data;
		}

		public void Dispose()
		{
			if (_encoding)
			{
				DisposedWhileEncoding = true;
			}

			_disposed = true;
			_release.Dispose();
		}
	}

	/// <summary>Marks everything it encodes, so the test can tell encoded bytes from plain ones.</summary>
	private sealed class MarkingOutboundTransform : IOutboundByteTransform
	{
		public ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> data)
		{
			var marked = new byte[data.Length + 1];
			marked[0] = (byte)'#';
			data.Span.CopyTo(marked.AsSpan(1));
			return marked;
		}

		public void Dispose()
		{
		}
	}

	private static async Task<(TelnetInterpreter Interpreter, SeamProbePlugin Probe)> BuildProbeAsync(
		Func<ReadOnlyMemory<byte>, ValueTask> onNegotiation)
	{
		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(onNegotiation)
			.AddPlugin<SeamProbePlugin>());

		return (interpreter, interpreter.PluginManager!.GetPlugin<SeamProbePlugin>()!);
	}

	[Test]
	public async Task SwappingTheOutboundTransformWaitsForAWriteThatIsAlreadyUsingIt()
	{
		var (interpreter, probe) = await BuildProbeAsync(_ => ValueTask.CompletedTask);
		var gate = new GatedOutboundTransform();
		await probe.ProbeContext.SetOutboundByteTransformAsync(gate);

		// A write from an application thread reaches the transform and parks inside it. It needs its
		// own thread: WriteToNetworkAsync runs synchronously all the way into Encode.
		var write = Task.Run(async () => await interpreter.WriteToNetworkAsync(Encoding.ASCII.GetBytes("hello")));
		await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));

		// Meanwhile the byte-processing loop decides compression is over and swaps the transform
		// out. It must not dispose an encoder a write is inside.
		var swap = probe.ProbeContext.SetOutboundByteTransformAsync(null).AsTask();
		await Task.Delay(50);
		await Assert.That(swap.IsCompleted).IsFalse();

		gate.Release();
		await write;
		await swap;

		await Assert.That(gate.DisposedWhileEncoding).IsFalse();

		await interpreter.DisposeAsync();
	}

	[Test]
	public async Task NothingCanBeWrittenBetweenTheFinalPlaintextWriteAndTheSwitchOver()
	{
		// A protocol that announces its switch-over with a marker (MCCP: IAC SB MCCP2 IAC SE) needs
		// the marker and the install to be one step. If another thread's write lands in between it
		// goes out in the clear after the peer has already started inflating, and the peer's zlib
		// stream is destroyed.
		var wire = new List<string>();
		var firstWriteReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var blockNext = true;

		var (interpreter, probe) = await BuildProbeAsync(async data =>
		{
			if (blockNext)
			{
				blockNext = false;
				firstWriteReached.TrySetResult();
				await releaseFirstWrite.Task;
			}

			wire.Add(Encoding.ASCII.GetString(data.Span));
		});

		var slowWrite = Task.Run(async () => await interpreter.WriteToNetworkAsync(Encoding.ASCII.GetBytes("plain")));
		await firstWriteReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var install = probe.ProbeContext
			.SetOutboundByteTransformAsync(new MarkingOutboundTransform(), Encoding.ASCII.GetBytes("MARKER"))
			.AsTask();
		var lateWrite = Task.Run(async () =>
			await interpreter.WriteToNetworkAsync(Encoding.ASCII.GetBytes("late")));

		await Task.Delay(50);
		await Assert.That(install.IsCompleted).IsFalse();

		releaseFirstWrite.TrySetResult();
		await slowWrite;
		await install;
		await lateWrite;

		// The marker goes out in the clear, and nothing plain follows it.
		await Assert.That(wire).IsEquivalentTo(new[] { "plain", "MARKER", "#late" });

		await interpreter.DisposeAsync();
	}

	[Test]
	public async Task DisposingTheInterpreterDisposesAnInstalledTransform()
	{
		var (interpreter, probe) = await BuildProbeAsync(_ => ValueTask.CompletedTask);
		var transform = new CountingOutboundTransform();
		await probe.ProbeContext.SetOutboundByteTransformAsync(transform);

		await interpreter.DisposeAsync();

		await Assert.That(transform.Disposals).IsEqualTo(1);
	}

	private sealed class CountingOutboundTransform : IOutboundByteTransform
	{
		public int Disposals { get; private set; }

		public ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> data) => data;

		public void Dispose() => Disposals++;
	}

	/// <summary>
	/// An inbound transform that parks inside <see cref="DecodeAsync"/> until the test lets it out,
	/// and remembers whether it was disposed while it was in there.
	/// </summary>
	private sealed class GatedInboundTransform : IInboundByteTransform
	{
		private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly ManualResetEventSlim _release = new(false);
		private readonly byte[] _one = new byte[1];
		private volatile bool _decoding;

		public Task Entered => _entered.Task;
		public bool DisposedWhileDecoding { get; private set; }
		public int Disposals { get; private set; }

		public void Release() => _release.Set();

		public ValueTask<ReadOnlyMemory<byte>> DecodeAsync(byte raw)
		{
			_decoding = true;
			_entered.TrySetResult();
			_release.Wait();
			_decoding = false;

			_one[0] = raw;
			return new ValueTask<ReadOnlyMemory<byte>>(_one.AsMemory());
		}

		public void Dispose()
		{
			// A real decoder here is a zlib inflater. Disposing one while the byte loop is reading
			// from it is a native use-after-free, not merely an ObjectDisposedException.
			if (_decoding)
			{
				DisposedWhileDecoding = true;
			}

			Disposals++;
		}
	}

	[Test]
	public async Task SwappingTheInboundTransformFromAnotherThreadDoesNotDisposeItMidDecode()
	{
		// PluginManager.DisablePluginAsync<T>() is public and reaches this from any thread, so the
		// byte loop can be inside DecodeAsync when a transform is swapped out.
		var (interpreter, probe) = await BuildProbeAsync(_ => ValueTask.CompletedTask);
		var gate = new GatedInboundTransform();
		probe.ProbeContext.SetInboundByteTransform(gate);

		await interpreter.InterpretByteArrayAsync(new byte[] { (byte)'x' });
		await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));

		// The byte loop is parked inside the transform. Retire it from this thread.
		probe.ProbeContext.SetInboundByteTransform(null);
		await Assert.That(gate.Disposals).IsEqualTo(0);

		gate.Release();
		await interpreter.WaitForProcessingAsync();
		await Assert.That(gate.DisposedWhileDecoding).IsFalse();

		// It is still disposed — by the loop, once it is provably out of the transform.
		await interpreter.InterpretByteArrayAsync(new byte[] { (byte)'y' });
		await PollUntilAsync(() => gate.Disposals == 1);
		await Assert.That(gate.Disposals).IsEqualTo(1);
		await Assert.That(gate.DisposedWhileDecoding).IsFalse();

		await interpreter.DisposeAsync();
		await Assert.That(gate.Disposals).IsEqualTo(1);
	}

	[Test]
	public async Task ARetiredInboundTransformIsDisposedAtTeardownIfNoMoreBytesArrive()
	{
		var (interpreter, probe) = await BuildProbeAsync(_ => ValueTask.CompletedTask);
		var retired = new CountingInboundTransform();
		probe.ProbeContext.SetInboundByteTransform(retired);
		probe.ProbeContext.SetInboundByteTransform(null);

		await interpreter.DisposeAsync();

		await Assert.That(retired.Disposals).IsEqualTo(1);
	}

	private sealed class CountingInboundTransform : IInboundByteTransform
	{
		private readonly byte[] _one = new byte[1];

		public int Disposals { get; private set; }

		public ValueTask<ReadOnlyMemory<byte>> DecodeAsync(byte raw)
		{
			_one[0] = raw;
			return new ValueTask<ReadOnlyMemory<byte>>(_one.AsMemory());
		}

		public void Dispose() => Disposals++;
	}

	/// <summary>Passes bytes straight through, recording how it was fed.</summary>
	private sealed class FeedRecordingInboundTransform : IInboundByteTransform
	{
		private readonly byte[] _one = new byte[1];

		public int Calls { get; private set; }
		public int BytesIn { get; private set; }

		public ValueTask<ReadOnlyMemory<byte>> DecodeAsync(byte raw)
		{
			Calls++;
			BytesIn++;
			_one[0] = raw;
			return new ValueTask<ReadOnlyMemory<byte>>(_one.AsMemory());
		}

		public void Dispose()
		{
		}
	}

	[Test]
	public async Task TheInboundTransformIsFedExactlyOneBytePerCall()
	{
		// Load-bearing, not incidental. A decoder's output buffer is bounded by DEFLATE's maximum
		// expansion from ONE input byte (1032); feeding it in batches makes that bound 1032 x batch
		// size, which the peer chooses. Anyone who "optimizes" the loop to hand over a span at a
		// time removes that bound, and this test is what tells them.
		var (interpreter, probe) = await BuildProbeAsync(_ => ValueTask.CompletedTask);
		var recorder = new FeedRecordingInboundTransform();
		probe.ProbeContext.SetInboundByteTransform(recorder);

		var payload = Encoding.ASCII.GetBytes("a stream of bytes arriving in one buffer\n");
		await interpreter.InterpretByteArrayAsync(payload);
		await interpreter.WaitForProcessingAsync();
		await PollUntilAsync(() => recorder.BytesIn >= payload.Length);

		await Assert.That(recorder.BytesIn).IsEqualTo(payload.Length);
		await Assert.That(recorder.Calls).IsEqualTo(payload.Length);

		await interpreter.DisposeAsync();
	}
}
