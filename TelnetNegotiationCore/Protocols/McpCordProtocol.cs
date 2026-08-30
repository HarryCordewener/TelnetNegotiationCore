using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// <c>mcp-cord</c>: named, typed channels multiplexed over the one MCP session.
/// </summary>
/// <remarks>
/// <para>
/// <b>A cord is not a negotiation</b>, which is worth saying because its open/close lifecycle looks
/// like one. The negotiating happened before it, when <see cref="MudClientProtocol"/>'s package
/// negotiation established that both sides speak <c>mcp-cord</c>; opening one is use of a capability already
/// agreed. There is no offer, no counter-offer and no version intersection -- closer to opening a
/// connection on an agreed port than to agreeing which ports exist.
/// </para>
/// <para>
/// What it buys is that a consumer can define its own channel <em>without a new plugin in this
/// library</em>: declare a cord type, and everything sent on a cord of that type is yours. That is
/// why the specification calls implementing it strongly encouraged.
/// </para>
/// <para>
/// Identifiers carry a role prefix, which is the specification's own scheme for keeping the two ends
/// from choosing the same one: the endpoint that initiated MCP -- the server, which makes the offer
/// -- prefixes <c>I</c>, and the responder prefixes <c>R</c>. Each side then only has to be unique
/// against itself.
/// </para>
/// </remarks>
public class McpCordProtocol : TelnetProtocolPluginBase
{
	/// <summary>This package's name, as advertised.</summary>
	public const string PackageName = "mcp-cord";

	private const string OpenMessage = "mcp-cord-open";
	private const string CordMessage = "mcp-cord";
	private const string ClosedMessage = "mcp-cord-closed";

	/// <summary>
	/// How many cords the peer may hold open at once.
	/// </summary>
	/// <remarks>
	/// A cord costs memory until somebody closes it, and the peer decides whether anybody ever does.
	/// The ceiling is on cords the peer opened; cords this side opened are this side's own business.
	/// </remarks>
	private const int MaxPeerCords = 64;

	private readonly Dictionary<string, Func<McpCord, ValueTask>> _types =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Open cords, keyed by identifier. Case-sensitive, as MCP identifiers are.</summary>
	private readonly Dictionary<string, McpCord> _open = new(StringComparer.Ordinal);

	private long _nextId;

	/// <inheritdoc />
	public override Type ProtocolType => typeof(McpCordProtocol);

	/// <inheritdoc />
	public override string ProtocolName => "MCP mcp-cord";

	/// <summary>
	/// <see cref="MudClientProtocol"/>. A cord is carried on that layer's messages and is only usable
	/// once its package negotiation has told the peer this side speaks <c>mcp-cord</c> -- so a consumer
	/// who registers this alone is told at <c>BuildAsync</c> rather than finding out from silence.
	/// </summary>
	public override IReadOnlyCollection<Type> Dependencies => [typeof(MudClientProtocol)];

	/// <summary>The cords open on this session, this side's and the peer's alike.</summary>
	public IReadOnlyCollection<McpCord> Open
	{
		get { lock (_open) return [.. _open.Values]; }
	}

	/// <summary>
	/// Declares a cord type this side accepts, and what to do when the peer opens one.
	/// </summary>
	/// <remarks>
	/// A cord of an undeclared type is dropped: the specification says to verify the type is
	/// supported, and an unsupported one is an unrecognised message. Wire the cord's own
	/// <see cref="McpCord.OnMessage"/> and <see cref="McpCord.OnClosed"/> from inside
	/// <paramref name="onOpened"/> -- it runs before any message can arrive on that cord.
	/// </remarks>
	/// <param name="type">The cord type, matched case-insensitively</param>
	/// <param name="onOpened">Called with the new cord when the peer opens one of this type</param>
	/// <returns>This instance for fluent chaining</returns>
	public McpCordProtocol SupportsCordType(string type, Func<McpCord, ValueTask> onOpened)
	{
		if (string.IsNullOrEmpty(type)) throw new ArgumentException("A cord type is required.", nameof(type));
		if (onOpened is null) throw new ArgumentNullException(nameof(onOpened));

		lock (_types) _types[type] = onOpened;
		return this;
	}

	/// <inheritdoc />
	/// <remarks>A package adds no telnet states and no triggers: it is carried on MCP messages.</remarks>
	public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
	{
		context.Logger.LogInformation("Configuring {Package}", PackageName);
	}

	/// <inheritdoc />
	protected override ValueTask OnInitializeAsync()
	{
		Mcp.Supports(PackageName, new McpVersion(1, 0), new McpVersion(1, 0));

		Mcp.OnMessage(OpenMessage, OnOpenAsync);
		Mcp.OnMessage(CordMessage, OnCordAsync);
		Mcp.OnMessage(ClosedMessage, OnClosedAsync);

		return default(ValueTask);
	}

	/// <inheritdoc />
	/// <remarks>
	/// Emptying the table is not enough. A cord handed to a consumer outlives the table, and one that
	/// still reports itself open is one <see cref="McpCord.SendAsync"/> will still write to the wire --
	/// the MCP session underneath is untouched, so those messages would go out for real, naming a cord
	/// this side no longer knows about.
	/// </remarks>
	protected override ValueTask OnProtocolDisabledAsync()
	{
		CloseAll();

		return default(ValueTask);
	}

	/// <inheritdoc />
	protected override ValueTask OnDisposeAsync()
	{
		CloseAll();

		return default(ValueTask);
	}

	private void CloseAll()
	{
		lock (_open)
		{
			foreach (var cord in _open.Values) cord.IsOpen = false;

			_open.Clear();
		}
	}

	private MudClientProtocol Mcp =>
		Context.GetPlugin<MudClientProtocol>()
		?? throw new InvalidOperationException($"{nameof(McpCordProtocol)} requires {nameof(MudClientProtocol)}.");

	/// <summary>
	/// Opens a cord of the given type and tells the peer.
	/// </summary>
	/// <remarks>
	/// <paramref name="configure"/> runs <em>before</em> <c>mcp-cord-open</c> is written, which is the
	/// point of it: wiring handlers after this method returns is a race the caller cannot win on a
	/// real socket, because the peer is free to send on the cord before the next statement runs.
	/// </remarks>
	/// <param name="type">The cord type</param>
	/// <param name="configure">Runs on the new cord before it is announced -- where its
	/// <see cref="McpCord.OnMessage"/> and <see cref="McpCord.OnClosed"/> handlers belong</param>
	/// <returns>The new cord.</returns>
	/// <exception cref="InvalidOperationException">There is no MCP session on this connection.</exception>
	public async ValueTask<McpCord> OpenAsync(string type, Action<McpCord>? configure = null)
	{
		if (string.IsNullOrEmpty(type)) throw new ArgumentException("A cord type is required.", nameof(type));

		if (!IsEnabled)
		{
			throw new InvalidOperationException(
				$"{nameof(McpCordProtocol)} is disabled on this connection, so it will not open a cord.");
		}

		// Checked before a cord exists rather than left to the send: failing afterwards would leave a
		// cord this side believes is open that the peer was never told about.
		if (!Mcp.IsNegotiated)
		{
			throw new InvalidOperationException(
				"There is no MCP session on this connection, so a cord cannot be opened on it.");
		}

		// And cords are an optional package, not part of the session layer. Opening one against a peer
		// that never advertised mcp-cord sends a message it is obliged to drop, while this side goes on
		// believing the cord exists. A consumer that wants one waits for negotiation to finish --
		// OnMcpNegotiationComplete, or Agreed filling in.
		if (!Mcp.Agreed.ContainsKey(PackageName))
		{
			throw new InvalidOperationException(
				$"The peer has not agreed the {PackageName} package, so a cord cannot be opened.");
		}

		McpCord cord;

		// Generated and reserved in one critical section. The role prefixes keep the two ends apart by
		// convention, but a peer is not obliged to honour them and an identifier that arrives is an
		// opaque string -- so a peer that opened "R1" against this client would otherwise have its cord
		// silently replaced here by the client's own first "R1": still open as far as the peer is
		// concerned, and unreachable from this side.
		lock (_open)
		{
			string id;

			do
			{
				id = NewId();
			}
			while (_open.ContainsKey(id));

			cord = new McpCord(this, id, type);
			_open[id] = cord;
		}

		try
		{
			// Before anything is written, so a peer that answers at once cannot beat the caller to it --
			// and inside the cleanup, so a callback that throws does not leave the identifier reserved
			// for a cord the peer was never told about.
			configure?.Invoke(cord);

			await Mcp.SendAsync(OpenMessage, ("_id", cord.Id), ("_type", type));
		}
		catch
		{
			// The peer was never told, so this side must not go on believing otherwise.
			lock (_open) _open.Remove(cord.Id);
			cord.IsOpen = false;
			throw;
		}

		Context.Logger.LogDebug("Opened cord {Id} of type {Type}", cord.Id, type);
		return cord;
	}

	/// <summary>
	/// The specification's identifier scheme: the endpoint that initiated MCP prefixes <c>I</c>, the
	/// responder prefixes <c>R</c>, and each is then only obliged to be unique against itself.
	/// </summary>
	private string NewId()
	{
		var role = Context.Mode == Interpreters.TelnetInterpreter.TelnetMode.Server ? "I" : "R";

		return role + Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
	}

	internal async ValueTask SendOnAsync(
		McpCord cord,
		string message,
		IReadOnlyCollection<(string Key, string Value)> values,
		(string Key, IReadOnlyCollection<string> Lines)[]? multiline = null)
	{
		if (string.IsNullOrEmpty(message))
		{
			throw new ArgumentException("A cord message name is required.", nameof(message));
		}

		// Refused rather than dropped. A cord the peer has closed will not carry this, and quietly
		// discarding it would leave the caller believing it had been sent.
		if (!cord.IsOpen)
		{
			throw new InvalidOperationException($"Cord {cord.Id} is closed.");
		}

		(string, string)[] all = [("_id", cord.Id), ("_message", message), .. values];

		if (multiline is null || multiline.Length == 0)
		{
			await Mcp.SendAsync(CordMessage, all);
			return;
		}

		await Mcp.SendMultilineAsync(CordMessage, all, multiline);
	}

	internal async ValueTask CloseAsync(McpCord cord)
	{
		bool wasOpen;

		lock (_open)
		{
			wasOpen = cord.IsOpen && _open.Remove(cord.Id);
			cord.IsOpen = false;
		}

		// Saying it twice is not an error, but it is not said twice either.
		if (!wasOpen) return;

		Context.Logger.LogDebug("Closing cord {Id}", cord.Id);
		await Mcp.SendAsync(ClosedMessage, ("_id", cord.Id));
	}

	private async ValueTask OnOpenAsync(McpMessage message)
	{
		// Registered on the session layer for the life of the connection, so a disabled plugin has to
		// decline for itself rather than rely on being unhooked.
		if (!IsEnabled) return;

		// The same gate OpenAsync applies, because it is a gate on the capability rather than on which
		// side asked for it. Ungated, an authenticated peer could put this side into a cord
		// conversation the two of them had never agreed to have.
		if (!Mcp.Agreed.ContainsKey(PackageName))
		{
			Context.Logger.LogDebug(
				"Ignoring {Message}: the {Package} package is not agreed on this session",
				OpenMessage, PackageName);
			return;
		}

		var id = message.Value("_id");
		var type = message.Value("_type");

		if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type))
		{
			Context.Logger.LogDebug("Ignoring a {Message} that names no cord or no type", OpenMessage);
			return;
		}

		Func<McpCord, ValueTask>? onOpened;
		lock (_types) _types.TryGetValue(type!, out onOpened);

		// "Implementations should verify the cord type is supported" -- and an unsupported one is an
		// unrecognised message, which MCP drops rather than answers.
		if (onOpened is null)
		{
			Context.Logger.LogDebug("Ignoring a cord of unsupported type {Type}", type);
			return;
		}

		var cord = new McpCord(this, id!, type!) { OpenedByPeer = true };

		lock (_open)
		{
			// Only the peer's own cords count against the peer's ceiling. Cords this side opened are
			// this side's business, and letting them spend the allowance would have this endpoint
			// refuse the peer's first valid cord after opening 64 of its own.
			if (_open.Values.Count(existing => existing.OpenedByPeer) >= MaxPeerCords)
			{
				Context.Logger.LogWarning(
					"Refusing cord {Id}: the peer already has {Count} open", id, MaxPeerCords);
				return;
			}

			// A repeated identifier would replace a cord already in flight, which is the substitution
			// the identifier scheme exists to prevent.
			if (_open.ContainsKey(id!))
			{
				Context.Logger.LogDebug("Ignoring a cord that reuses the identifier {Id}", id);
				return;
			}

			_open[id!] = cord;
		}

		Context.Logger.LogDebug("The peer opened cord {Id} of type {Type}", id, type);
		await onOpened(cord);
	}

	private async ValueTask OnCordAsync(McpMessage message)
	{
		// Registered on the session layer for the life of the connection, so a disabled plugin has to
		// decline for itself rather than rely on being unhooked.
		if (!IsEnabled) return;

		var id = message.Value("_id");

		if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(message.Value("_message")))
		{
			Context.Logger.LogDebug("Ignoring a {Message} that names no cord or no message", CordMessage);
			return;
		}

		McpCord? cord;
		lock (_open) _open.TryGetValue(id!, out cord);

		// "Treat as an unrecognized MCP message, silently dropping it."
		if (cord is null)
		{
			Context.Logger.LogDebug("Dropping a message for cord {Id}, which is not open", id);
			return;
		}

		await cord.DeliverAsync(message);
	}

	private async ValueTask OnClosedAsync(McpMessage message)
	{
		// Registered on the session layer for the life of the connection, so a disabled plugin has to
		// decline for itself rather than rely on being unhooked.
		if (!IsEnabled) return;

		var id = message.Value("_id");

		if (string.IsNullOrEmpty(id)) return;

		McpCord? cord;

		lock (_open)
		{
			if (!_open.TryGetValue(id!, out cord)) cord = null;
			else _open.Remove(id!);
		}

		// "Race conditions may result in receiving duplicate closure messages, which should be
		// ignored." The second one finds nothing open and stops here.
		if (cord is null) return;

		cord.IsOpen = false;

		Context.Logger.LogDebug("The peer closed cord {Id}", id);
		await cord.AnnounceClosedAsync();
	}
}
