using System;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;

namespace TelnetNegotiationCore.Connections;

/// <summary>
/// One running telnet connection: the interpreter, the read loop feeding it, and the transport under both.
/// </summary>
/// <remarks>
/// <para>
/// Made by <see cref="TelnetInterpreterBuilder.StartAsync(ITelnetTransport, CancellationToken)"/>, and handed to an
/// <see cref="ITelnetSession"/>. The connection owns the interpreter and disposes it; the transport stays the
/// caller's.
/// </para>
/// <para>
/// It deconstructs as the older <c>(interpreter, readTask)</c> pair, so <c>var (telnet, read) = await builder.StartAsync(...)</c>
/// reads the same as <see cref="TelnetInterpreterBuilder.BuildAndStartAsync(System.IO.Pipelines.IDuplexPipe, CancellationToken)"/>.
/// </para>
/// </remarks>
public sealed class TelnetConnection : IAsyncDisposable
{
	private const int Open = 0, ClosedHere = 1, Ended = 2;

	private readonly ITelnetTransport _transport;
	private readonly CancellationToken _startToken;
	private readonly CancellationTokenSource _closed = new();
	private readonly CancellationTokenSource _reading;
	private int _state;
	private int _disposed;
	private Task? _disposeTask;

	private TelnetConnection(ITelnetTransport transport, CancellationToken cancellationToken)
	{
		_transport = transport;
		_startToken = cancellationToken;
		_reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
		Closed = _closed.Token;
	}

	/// <summary>The connection's interpreter.</summary>
	public TelnetInterpreter Interpreter { get; private set; } = null!;

	/// <summary>
	/// The read loop. Completes when the peer leaves, when this side closes or disposes the connection, or when the
	/// start token is cancelled; faults when the transport fails.
	/// </summary>
	public Task Completion { get; private set; } = Task.CompletedTask;

	/// <summary>
	/// Cancelled once the connection has ended, whichever side ended it, or the start token was cancelled.
	/// </summary>
	public CancellationToken Closed { get; }

	/// <summary>Whether this side ended the connection with <see cref="CloseAsync"/> while the peer was still there.</summary>
	public bool ClosedByServer => Volatile.Read(ref _state) == ClosedHere;

	/// <summary>Deconstructs into the interpreter and the read loop, as <c>BuildAndStartAsync</c> returns them.</summary>
	public void Deconstruct(out TelnetInterpreter interpreter, out Task readTask)
	{
		interpreter = Interpreter;
		readTask = Completion;
	}

	/// <summary>
	/// Builds the interpreter on <paramref name="transport"/>, runs <see cref="TelnetInterpreterBuilder.OnStarting"/>,
	/// and starts reading.
	/// </summary>
	internal static async Task<TelnetConnection> StartAsync(
		TelnetInterpreterBuilder builder, ITelnetTransport transport, CancellationToken cancellationToken)
	{
		var connection = new TelnetConnection(transport, cancellationToken);
		try
		{
			connection.Interpreter = await builder.OnNegotiation(connection.WriteAsync).BuildAsync();
		}
		catch
		{
			connection.DisposeTokens();
			throw;
		}

		if (builder.StartingCallback is { } starting)
		{
			// Closed is the hook's only cancellation signal, and the read loop that would cancel it with the start
			// token has not started yet.
			var startCancelled = cancellationToken.Register(static state => ((TelnetConnection)state!).CancelClosed(), connection);
			try
			{
				await starting(connection);
			}
			catch
			{
				// The connection ends here, so a hook that kept it sees Closed cancelled.
				Volatile.Write(ref connection._disposed, 1);
				connection.CancelClosed();
				try
				{
					await connection.Interpreter.DisposeAsync();
				}
				catch
				{
					// The hook's exception is the one the caller needs.
				}
				finally
				{
					startCancelled.Dispose();
					connection.DisposeTokens();
				}

				throw;
			}

			startCancelled.Dispose();
		}

		var feed = builder.InputFeed(connection.Interpreter);
		connection.Completion = connection.ReadAsync(data => connection.IsStopped ? default : feed(data));
		return connection;
	}

	/// <summary>
	/// Builds the interpreter on <paramref name="transport"/>, runs <paramref name="session"/> on it, and closes and
	/// disposes the connection when the session returns.
	/// </summary>
	/// <remarks>
	/// The session's token is <see cref="Closed"/>. The task completes when the session does, and carries its
	/// exception.
	/// </remarks>
	public static async Task RunAsync(ITelnetSession session, ITelnetTransport transport, CancellationToken cancellationToken = default)
	{
		if (session is null) throw new ArgumentNullException(nameof(session));
		if (transport is null) throw new ArgumentNullException(nameof(transport));

		var builder = session.CreateBuilder() ?? throw new InvalidOperationException("The session returned no builder.");
		var connection = await StartAsync(builder, transport, cancellationToken);
		await using (connection)
		{
			try
			{
				await session.RunAsync(connection, connection.Closed);
			}
			finally
			{
				await connection.CloseIfOpenAsync();
			}
		}
	}

	private bool IsStopped => Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _state) == ClosedHere;

	private ValueTask WriteAsync(ReadOnlyMemory<byte> data) =>
		// A write after this side closed has nowhere to go; the interpreter may still write while it shuts down.
		IsStopped ? default : _transport.WriteAsync(data, _startToken);

	private async Task ReadAsync(Func<ReadOnlyMemory<byte>, ValueTask> onData)
	{
		try
		{
			await _transport.ReadAsync(onData, _reading.Token);
		}
		catch (OperationCanceledException) when (_reading.IsCancellationRequested)
		{
			// Stopped on purpose: a close, a dispose or the start token. Not a failure.
		}
		finally
		{
			Interlocked.CompareExchange(ref _state, Ended, Open);
			CancelClosed();
		}
	}

	/// <summary>
	/// Ends the connection from this side through the transport, and stops reading. Writes after it do nothing.
	/// A second call, or a call after the peer left, does nothing.
	/// </summary>
	public async ValueTask CloseAsync()
	{
		if (Interlocked.CompareExchange(ref _state, ClosedHere, Open) != Open) return;
		try
		{
			await _transport.CloseAsync(_startToken);
		}
		finally
		{
			CancelClosed();
		}
	}

	internal ValueTask CloseIfOpenAsync() => Volatile.Read(ref _state) == Open ? CloseAsync() : default;

	/// <summary>
	/// Stops the read loop and disposes the interpreter. A read loop that failed does not fail this;
	/// <see cref="Completion"/> still reports it. The transport is not closed: call <see cref="CloseAsync"/> for that.
	/// </summary>
	public ValueTask DisposeAsync()
	{
		lock (_closed)
		{
			_disposeTask ??= DisposeCoreAsync();
			return new ValueTask(_disposeTask);
		}
	}

	private async Task DisposeCoreAsync()
	{
		Volatile.Write(ref _disposed, 1);
		CancelClosed();

		var interpreterStopped = Interpreter is null ? Task.CompletedTask : Interpreter.DisposeAsync().AsTask();
		try
		{
			await Completion;
		}
		catch
		{
			// Reported by Completion.
		}

		try
		{
			await interpreterStopped;
		}
		finally
		{
			DisposeTokens();
		}
	}

	private void CancelClosed() => _closed.Cancel();

	// Only the linked source holds a registration on the start token. _closed is never disposed, so Closed stays
	// usable after the connection is gone; a source with no timer holds nothing that needs it.
	private void DisposeTokens() => _reading.Dispose();
}
