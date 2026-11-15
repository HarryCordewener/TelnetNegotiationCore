using System;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;

namespace TelnetNegotiationCore.Connections;

/// <summary>
/// What an application does with one telnet connection, whatever carries it: configure the interpreter, then run
/// alongside the read loop until the connection ends.
/// </summary>
/// <remarks>
/// <para>
/// One object per connection, so it can hold that connection's state, and the same object serves every transport:
/// <see cref="TelnetConnection.RunAsync(ITelnetSession, ITelnetTransport, CancellationToken)"/> runs it on a pipe,
/// stream or socket, and <see cref="WebSockets.TelnetWebSocketRegistryExtensions.AddTelnetSession(WebSockets.WebSocketSubprotocolRegistry, Func{ITelnetSession}, bool)"/>
/// runs it on a WebSocket endpoint.
/// </para>
/// <para>
/// <see cref="RunAsync"/> starts once the interpreter is reading, so it can wait for the opening negotiation. The
/// connection is closed and disposed when it returns.
/// </para>
/// </remarks>
public interface ITelnetSession
{
	/// <summary>
	/// A configured builder for this connection: mode, logger, plugins, callbacks and hooks. Leave the negotiation
	/// callback unset; the connection wires it to the transport.
	/// </summary>
	TelnetInterpreterBuilder CreateBuilder();

	/// <summary>
	/// Runs the connection. Usually ends by awaiting <see cref="TelnetConnection.Completion"/>.
	/// </summary>
	/// <param name="connection">The started connection.</param>
	/// <param name="cancellationToken"><see cref="TelnetConnection.Closed"/>: cancelled once the connection has ended.</param>
	Task RunAsync(TelnetConnection connection, CancellationToken cancellationToken);
}

/// <summary>
/// Makes an <see cref="ITelnetSession"/> from two lambdas.
/// </summary>
public static class TelnetSession
{
	/// <summary>
	/// A session whose builder comes from <paramref name="createBuilder"/> and whose run is <paramref name="run"/>.
	/// </summary>
	public static ITelnetSession Create(
		Func<TelnetInterpreterBuilder> createBuilder,
		Func<TelnetConnection, CancellationToken, Task> run) =>
		new DelegateSession(
			createBuilder ?? throw new ArgumentNullException(nameof(createBuilder)),
			run ?? throw new ArgumentNullException(nameof(run)));

	private sealed class DelegateSession(
		Func<TelnetInterpreterBuilder> createBuilder,
		Func<TelnetConnection, CancellationToken, Task> run) : ITelnetSession
	{
		public TelnetInterpreterBuilder CreateBuilder() => createBuilder();

		public Task RunAsync(TelnetConnection connection, CancellationToken cancellationToken) =>
			run(connection, cancellationToken);
	}
}
