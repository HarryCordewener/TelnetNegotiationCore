using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Plugins;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The contract a caller who does not know what a <see cref="TelnetInterpreter"/> is can rely on:
/// that it announces itself as disposable, and that disposing it twice is harmless.
/// </summary>
/// <remarks>
/// Both of these are about code the library never sees. <c>await using</c> binds to a
/// <c>DisposeAsync</c> method by pattern, so the interpreter's disposal ran correctly for years for
/// anyone who wrote that by hand — and not at all for a DI container, a
/// <c>List&lt;IAsyncDisposable&gt;</c>, or anything else that finds disposables by their type. The
/// same generic callers are the reason the second call has to be a no-op: a container that owns the
/// interpreter will dispose it whether or not the consumer already did.
/// </remarks>
public class DisposalTests : BaseTest
{
	private sealed class CountingScope : IDisposable
	{
		public int Disposals { get; private set; }
		public void Dispose() => Disposals++;
	}

	private sealed class TrackingMemoryStream : MemoryStream
	{
		public bool IsDisposed { get; private set; }
		protected override void Dispose(bool disposing)
		{
			IsDisposed = true;
			base.Dispose(disposing);
		}
	}

	private sealed class ScopeLogger(CountingScope scope) : ILogger
	{
		public IDisposable BeginScope<TState>(TState state) where TState : notnull => scope;
		public bool IsEnabled(LogLevel logLevel) => false;
		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
			Func<TState, Exception, string> formatter)
		{
		}
	}
	private sealed class DisposalProbeProtocol : TelnetProtocolPluginBase
	{
		public bool Disposed { get; private set; }
		public override Type ProtocolType => typeof(DisposalProbeProtocol);
		public override string ProtocolName => "Disposal probe";
		protected override ValueTask OnDisposeAsync()
		{
			Disposed = true;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class SecondDisposalProbeProtocol : TelnetProtocolPluginBase
	{
		public bool Disposed { get; private set; }
		public override Type ProtocolType => typeof(SecondDisposalProbeProtocol);
		public override string ProtocolName => "Second disposal probe";
		protected override ValueTask OnDisposeAsync()
		{
			Disposed = true;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class MissingDependencyProtocol : TelnetProtocolPluginBase
	{
		public bool Disposed { get; private set; }
		public override Type ProtocolType => typeof(MissingDependencyProtocol);
		public override string ProtocolName => "Missing dependency probe";
		public override IReadOnlyCollection<Type> Dependencies => [typeof(UnregisteredDependencyProtocol)];
		protected override ValueTask OnDisposeAsync()
		{
			Disposed = true;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class UnregisteredDependencyProtocol : TelnetProtocolPluginBase
	{
		public override Type ProtocolType => typeof(UnregisteredDependencyProtocol);
		public override string ProtocolName => "Unregistered dependency";
	}

	private sealed class ThrowingDisposalProtocol : TelnetProtocolPluginBase
	{
		public override Type ProtocolType => typeof(ThrowingDisposalProtocol);
		public override string ProtocolName => "Throwing disposal probe";
		protected override ValueTask OnDisposeAsync() =>
			ValueTask.FromException(new InvalidOperationException("dispose failed"));
	}

	private sealed class SecondThrowingDisposalProtocol : TelnetProtocolPluginBase
	{
		public override Type ProtocolType => typeof(SecondThrowingDisposalProtocol);
		public override string ProtocolName => "Second throwing disposal probe";
		protected override ValueTask OnDisposeAsync() =>
			ValueTask.FromException(new ArgumentException("second dispose failed"));
	}

	private sealed class TransformInstallingProtocol(CountingOutboundTransform transform) : TelnetProtocolPluginBase
	{
		public override Type ProtocolType => typeof(TransformInstallingProtocol);
		public override string ProtocolName => "Transform installer";
		protected override ValueTask OnInitializeAsync() => Context.SetOutboundByteTransformAsync(transform);
	}

	private sealed class InitializationThrowingProtocol : TelnetProtocolPluginBase
	{
		public override Type ProtocolType => typeof(InitializationThrowingProtocol);
		public override string ProtocolName => "Throwing initialization probe";
		protected override ValueTask OnInitializeAsync() =>
			ValueTask.FromException(new InvalidOperationException("initialization failed"));
	}

	private sealed class InitialNegotiationThrowingProtocol : TelnetProtocolPluginBase
	{
		public bool DisposedAfterCancellation { get; private set; }
		public override Type ProtocolType => typeof(InitialNegotiationThrowingProtocol);
		public override string ProtocolName => "Throwing initial negotiation probe";

		public override void ConfigureStateMachine(IProtocolContext context) =>
			context.RegisterInitialNegotiation(() =>
				ValueTask.FromException(new IOException("initial negotiation failed")));

		protected override ValueTask OnDisposeAsync()
		{
			DisposedAfterCancellation = Context.Interpreter.ProcessingToken.IsCancellationRequested;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class BlockingDisposalProtocol(TaskCompletionSource entered, TaskCompletionSource release)
		: TelnetProtocolPluginBase
	{
		public int Disposals { get; private set; }
		public override Type ProtocolType => typeof(BlockingDisposalProtocol);
		public override string ProtocolName => "Blocking disposal probe";
		protected override async ValueTask OnDisposeAsync()
		{
			Disposals++;
			entered.SetResult();
			await release.Task;
		}
	}

	private sealed class CountingOutboundTransform : IOutboundByteTransform
	{
		public int Disposals { get; private set; }
		public ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> data) => data;
		public void Dispose() => Disposals++;
	}

	/// <summary>
	/// Builds an interpreter that is complete enough to dispose: a real processing task, a real
	/// write lock, and a plugin so that <c>PluginManager.DisposeAllAsync</c> is on the path too.
	/// </summary>
	private static Task<TelnetInterpreter> BuildInterpreterAsync() =>
		BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<SuppressGoAheadProtocol>());

	/// <summary>
	/// The regression guard for the defect itself. Having a <c>DisposeAsync</c> method is not the
	/// same as being an <see cref="IAsyncDisposable"/>, and only the latter is visible to a caller
	/// holding the object as something other than a <see cref="TelnetInterpreter"/>.
	/// </summary>
	[Test]
	public async Task TelnetInterpreterDeclaresItselfAsyncDisposable()
	{
		await Assert.That(typeof(IAsyncDisposable).IsAssignableFrom(typeof(TelnetInterpreter))).IsTrue();
	}

	[Test]
	public async Task PluginAndManagerDeclareTheirAsyncDisposalContracts()
	{
		await Assert.That(typeof(IAsyncDisposable).IsAssignableFrom(typeof(ITelnetProtocolPlugin))).IsTrue();
		await Assert.That(typeof(IAsyncDisposable).IsAssignableFrom(typeof(ProtocolPluginManager))).IsTrue();
	}

	[Test]
	public async Task DuplicatePluginRegistrationIsRejectedBeforeOwnershipIsLost()
	{
		var manager = new ProtocolPluginManager(logger);
		var registered = new DisposalProbeProtocol();
		manager.RegisterPlugin(registered);

		await Assert.That(() => manager.RegisterPlugin(new DisposalProbeProtocol()))
			.Throws<InvalidOperationException>();
		await manager.DisposeAsync();
		await Assert.That(registered.Disposed).IsTrue();
	}

	[Test]
	public async Task InterpreterDisposesItsLoggerScope()
	{
		var scope = new CountingScope();
		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(new ScopeLogger(scope))
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask));

		await interpreter.DisposeAsync();

		await Assert.That(scope.Disposals).IsEqualTo(1);
	}

	[Test]
	public async Task StreamAdapterCleanupLeavesTheCallerOwnedStreamOpen()
	{
		var stream = new TrackingMemoryStream();
		var (interpreter, readTask) = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.BuildAndStartAsync(stream);

		await readTask;
		await interpreter.DisposeAsync();

		await Assert.That(stream.IsDisposed).IsFalse();
		stream.Dispose();
	}

	/// <summary>
	/// What a DI container does: it never sees a <see cref="TelnetInterpreter"/>, only an
	/// <see cref="IAsyncDisposable"/> it collected at registration time, and it disposes through
	/// that. The cast is deliberately <c>as</c> rather than a direct one so this test compiles
	/// either way and fails on the assertion rather than at the compiler.
	/// </summary>
	[Test]
	public async Task ACallerHoldingOnlyTheInterfaceCanDisposeIt()
	{
		var interpreter = await BuildInterpreterAsync();

		var asDisposable = interpreter as IAsyncDisposable;

		await Assert.That(asDisposable).IsNotNull();
		await asDisposable!.DisposeAsync();
	}

	/// <summary>
	/// The .NET contract requires <c>DisposeAsync</c> to tolerate being called more than once, and
	/// declaring the interface makes that happen in practice — a container disposing an interpreter
	/// the consumer already disposed, or an explicit call inside an <c>await using</c>.
	/// </summary>
	[Test]
	public async Task DisposingTwiceDoesNotThrow()
	{
		var interpreter = await BuildInterpreterAsync();

		await interpreter.DisposeAsync();

		await Assert.That(async () => await interpreter.DisposeAsync()).ThrowsNothing();
	}

	/// <summary>
	/// A third call is no different from a second one. This is here because a guard that clears
	/// itself, or one that only skips part of the work, passes <see cref="DisposingTwiceDoesNotThrow"/>
	/// and still fails a container that disposes late after a consumer disposed twice.
	/// </summary>
	[Test]
	public async Task DisposingRepeatedlyDoesNotThrow()
	{
		var interpreter = await BuildInterpreterAsync();

		await interpreter.DisposeAsync();
		await interpreter.DisposeAsync();

		await Assert.That(async () => await interpreter.DisposeAsync()).ThrowsNothing();
	}

	/// <summary>
	/// Concurrent disposal, which is the shape the read loop makes likely: the loop notices the
	/// connection is gone and disposes at the same moment as the owner. Only one of these calls may
	/// perform the shutdown, and none of them may throw.
	/// </summary>
	[Test]
	public async Task ConcurrentDisposalDoesNotThrow()
	{
		var interpreter = await BuildInterpreterAsync();

		var callers = new Task[8];
		for (var i = 0; i < callers.Length; i++)
		{
			callers[i] = Task.Run(async () => await interpreter.DisposeAsync());
		}

		await Assert.That(async () => await Task.WhenAll(callers)).ThrowsNothing();
	}

	[Test]
	public async Task DisposalFailureStillReleasesRemainingPluginsAndTransforms()
	{
		var survivor = new DisposalProbeProtocol();
		var transform = new CountingOutboundTransform();
		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin(survivor)
			.AddPlugin(new TransformInstallingProtocol(transform))
			.AddPlugin(new ThrowingDisposalProtocol()));

		await Assert.That(async () => await interpreter.DisposeAsync())
			.Throws<InvalidOperationException>();
		await Assert.That(survivor.Disposed).IsTrue();
		await Assert.That(transform.Disposals).IsEqualTo(1);
	}

	[Test]
	public async Task MultipleDisposalFailuresAreAggregatedAfterAllPluginsAreReleased()
	{
		var survivor = new DisposalProbeProtocol();
		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin(survivor)
			.AddPlugin(new ThrowingDisposalProtocol())
			.AddPlugin(new SecondThrowingDisposalProtocol()));

		var exception = await Assert.That(async () => await interpreter.DisposeAsync())
			.Throws<AggregateException>();
		await Assert.That(exception!.InnerExceptions).Count().IsEqualTo(2);
		await Assert.That(survivor.Disposed).IsTrue();
	}

	[Test]
	public async Task BuildFailureDisposesPartiallyInitializedInterpreter()
	{
		var survivor = new DisposalProbeProtocol();
		var transform = new CountingOutboundTransform();
		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin(survivor)
			.AddPlugin(new TransformInstallingProtocol(transform))
			.AddPlugin(new InitializationThrowingProtocol());

		await Assert.That(async () => await builder.BuildAsync()).Throws<InvalidOperationException>();
		await Assert.That(survivor.Disposed).IsTrue();
		await Assert.That(transform.Disposals).IsEqualTo(1);
	}

	[Test]
	public async Task InitialNegotiationFailureStopsTheStartedInterpreterBeforeDisposingPlugins()
	{
		var plugin = new InitialNegotiationThrowingProtocol();
		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin(plugin);

		await Assert.That(async () => await builder.BuildAsync()).Throws<IOException>();
		await Assert.That(plugin.DisposedAfterCancellation).IsTrue();
	}

	[Test]
	public async Task InitializationFailureDoesNotMakeAnUnstartedMccpPluginMaskTheCause()
	{
		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin(new InitializationThrowingProtocol())
			.AddPlugin(new MCCPProtocol());

		await Assert.That(async () => await builder.BuildAsync()).Throws<InvalidOperationException>();
	}

	[Test]
	public async Task DependencyResolutionFailureDisposesEveryRegisteredPlugin()
	{
		var first = new DisposalProbeProtocol();
		var failing = new MissingDependencyProtocol();
		var last = new SecondDisposalProbeProtocol();
		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin(first)
			.AddPlugin(failing)
			.AddPlugin(last);

		await Assert.That(async () => await builder.BuildAsync()).Throws<InvalidOperationException>();
		await Assert.That(first.Disposed).IsTrue();
		await Assert.That(failing.Disposed).IsTrue();
		await Assert.That(last.Disposed).IsTrue();
	}

	[Test]
	public async Task ConcurrentManagerDisposalIsSingleFlight()
	{
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var plugin = new BlockingDisposalProtocol(entered, release);
		var manager = new ProtocolPluginManager(logger);
		manager.RegisterPlugin(plugin);

		var first = manager.DisposeAsync().AsTask();
		await entered.Task;
		var second = manager.DisposeAsync().AsTask();
		await Task.Delay(25);

		await Assert.That(second.IsCompleted).IsFalse();
		release.SetResult();
		await Task.WhenAll(first, second);
		await Assert.That(plugin.Disposals).IsEqualTo(1);
	}

	[Test]
	public async Task ConcurrentDisposalWaitsForTheSharedShutdown()
	{
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin(new BlockingDisposalProtocol(entered, release)));

		var first = interpreter.DisposeAsync().AsTask();
		await entered.Task;
		var second = interpreter.DisposeAsync().AsTask();
		await Task.Delay(25);

		await Assert.That(second.IsCompleted).IsFalse();
		release.SetResult();
		await Task.WhenAll(first, second);
	}

	[Test]
	public async Task FailedTransformInstallationDisposesTheUnacceptedTransform()
	{
		var transform = new CountingOutboundTransform();
		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.FromException(new IOException("write failed"))));

		await Assert.That(async () =>
				await interpreter.SetOutboundByteTransformAsync(transform, new byte[] { 1 }))
			.Throws<IOException>();
		await Assert.That(transform.Disposals).IsEqualTo(1);
		await interpreter.DisposeAsync();
	}
}
