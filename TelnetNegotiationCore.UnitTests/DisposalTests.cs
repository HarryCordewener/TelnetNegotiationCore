using System;
using System.Threading.Tasks;
using TUnit.Core;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
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
}
