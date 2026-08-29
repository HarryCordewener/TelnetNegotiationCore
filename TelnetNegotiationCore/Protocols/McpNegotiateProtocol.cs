using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// <c>mcp-negotiate</c>: the package that tells the other side which packages this side speaks, and
/// works out which version of each both can use.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate plugin from <see cref="MudClientProtocol"/>, because it is a separate thing.</b>
/// <c>mcp-negotiate</c> is not part of the MCP session layer: it is a package carried over it,
/// versioned on its own, exactly as <c>dns-org-mud-moo-simpleedit</c> is. The dependency runs one
/// way -- the session layer works with this absent, and this is meaningless without it -- which is
/// what makes the split a real boundary rather than a filing decision.
/// </para>
/// <para>
/// Version 1.0 is the list of <c>mcp-negotiate-can</c> lines. Version 2.0 adds
/// <c>mcp-negotiate-end</c>, the line that says the list is finished. Both are read here, which is
/// why agreement is computed as each <c>can</c> arrives rather than waiting for an end that a 1.0
/// peer never sends; <see cref="IsComplete"/> is the extra thing 2.0 buys, not a precondition for
/// agreeing on anything.
/// </para>
/// </remarks>
public class McpNegotiateProtocol : TelnetProtocolPluginBase
{
	/// <summary>This package's own name, which it advertises like any other.</summary>
	public const string PackageName = "mcp-negotiate";

	private const string CanMessage = "mcp-negotiate-can";
	private const string EndMessage = "mcp-negotiate-end";

	private readonly Dictionary<string, (McpVersion Min, McpVersion Max)> _supported =
		new(StringComparer.OrdinalIgnoreCase)
		{
			[PackageName] = (new McpVersion(1, 0), new McpVersion(2, 0)),
		};

	private readonly Dictionary<string, McpVersion> _agreed = new(StringComparer.OrdinalIgnoreCase);

	private Func<IReadOnlyDictionary<string, McpVersion>, ValueTask>? _onComplete;

	/// <inheritdoc />
	public override Type ProtocolType => typeof(McpNegotiateProtocol);

	/// <inheritdoc />
	public override string ProtocolName => "MCP mcp-negotiate";

	/// <summary>
	/// <see cref="MudClientProtocol"/>. This package is carried over that session layer and has no
	/// meaning without it, so a consumer who registers this alone is told at <c>BuildAsync</c> rather
	/// than finding out from silence on the wire.
	/// </summary>
	public override IReadOnlyCollection<Type> Dependencies => [typeof(MudClientProtocol)];

	/// <summary>
	/// The packages both sides speak, and the highest version each agreed on.
	/// </summary>
	/// <remarks>
	/// A package is in here only if this side declared it through <see cref="Supports"/>, the peer
	/// offered it, and the two ranges overlap. A peer's <c>can</c> line on its own is a claim about
	/// the peer, not an agreement.
	/// </remarks>
	public IReadOnlyDictionary<string, McpVersion> Agreed
	{
		get { lock (_agreed) return new Dictionary<string, McpVersion>(_agreed, StringComparer.OrdinalIgnoreCase); }
	}

	/// <summary>
	/// Whether the peer has said its list is finished, by sending <c>mcp-negotiate-end</c>.
	/// </summary>
	/// <remarks>
	/// False forever against a 1.0 peer, which has no such line. That is not the same as "no packages
	/// agreed" -- see <see cref="Agreed"/>, which fills in as the list arrives.
	/// </remarks>
	public bool IsComplete { get; private set; }

	/// <summary>
	/// Declares a package this side speaks, and the range of versions it can speak.
	/// </summary>
	/// <remarks>
	/// Call this before <c>BuildAsync</c>, or from a package plugin's own <c>InitializeAsync</c>. The
	/// whole list goes out in one burst the moment the session comes up, closed by
	/// <c>mcp-negotiate-end</c>, so a package that registers after that has told the peer nothing.
	/// </remarks>
	/// <param name="package">The package name</param>
	/// <param name="minimum">The lowest version this side can speak</param>
	/// <param name="maximum">The highest version this side can speak</param>
	/// <returns>This instance for fluent chaining</returns>
	/// <exception cref="ArgumentOutOfRangeException">The range is empty.</exception>
	public McpNegotiateProtocol Supports(string package, McpVersion minimum, McpVersion maximum)
	{
		if (string.IsNullOrEmpty(package))
		{
			throw new ArgumentException("A package name is required.", nameof(package));
		}

		if (minimum > maximum)
		{
			throw new ArgumentOutOfRangeException(
				nameof(minimum), minimum, $"The minimum version is above the maximum ({maximum}).");
		}

		lock (_supported) _supported[package] = (minimum, maximum);
		return this;
	}

	/// <summary>
	/// Sets the callback that runs when the peer says its list is finished, with the agreed packages.
	/// </summary>
	/// <remarks>
	/// <b>Fires on the peer's <c>mcp-negotiate-end</c>, so against a 1.0 peer it never fires at all</b>
	/// -- that version has no such line. It is therefore the wrong place to hang anything needed
	/// against every peer; read <see cref="Agreed"/>, which fills in as each <c>can</c> arrives.
	/// Once the end line has been seen the negotiation is terminal, so the set handed to this callback
	/// stays the set that was agreed.
	/// </remarks>
	/// <param name="callback">The callback, or null to remove one</param>
	/// <returns>This instance for fluent chaining</returns>
	public McpNegotiateProtocol OnNegotiationComplete(
		Func<IReadOnlyDictionary<string, McpVersion>, ValueTask>? callback)
	{
		_onComplete = callback;
		return this;
	}

	/// <inheritdoc />
	/// <remarks>
	/// A package adds no telnet states and no triggers: it is carried on MCP messages, which are text.
	/// </remarks>
	public override void ConfigureStateMachine(StateMachine<State, Trigger> stateMachine, IProtocolContext context)
	{
		context.Logger.LogInformation("Configuring {Package}", PackageName);
	}

	/// <inheritdoc />
	protected override ValueTask OnInitializeAsync()
	{
		Mcp.OnMessage(CanMessage, OnCanAsync);
		Mcp.OnMessage(EndMessage, OnEndAsync);
		Mcp.OnEstablished(SendPackageListAsync);

		return default(ValueTask);
	}

	/// <summary>
	/// The <see cref="MudClientProtocol"/> this package is carried over. Present by construction:
	/// <see cref="Dependencies"/> makes registering without it a <c>BuildAsync</c> failure.
	/// </summary>
	private MudClientProtocol Mcp =>
		Context.GetPlugin<MudClientProtocol>()
		?? throw new InvalidOperationException($"{nameof(McpNegotiateProtocol)} requires {nameof(MudClientProtocol)}.");

	/// <summary>
	/// Sends the whole list, then the line that says it is finished.
	/// </summary>
	private async ValueTask SendPackageListAsync()
	{
		KeyValuePair<string, (McpVersion Min, McpVersion Max)>[] packages;
		lock (_supported) packages = [.. _supported];

		foreach (var (package, range) in packages)
		{
			await Mcp.SendAsync(
				CanMessage,
				("package", package),
				("min-version", range.Min.ToString()),
				("max-version", range.Max.ToString()));
		}

		await Mcp.SendAsync(EndMessage);

		Context.Logger.LogDebug("Offered {Count} MCP packages", packages.Length);
	}

	/// <summary>
	/// One package the peer says it speaks. Agreement is worked out here rather than at the end of the
	/// list, because a 1.0 peer never sends an end.
	/// </summary>
	private ValueTask OnCanAsync(McpMessage message)
	{
		// The list is finished when the peer says it is finished. A can line arriving afterwards must
		// not change what was agreed: OnNegotiationComplete has already been handed a snapshot, and a
		// table that keeps moving under it is a snapshot of nothing.
		if (IsComplete)
		{
			Context.Logger.LogDebug("Ignoring {Message}: the peer already ended its package list", CanMessage);
			return default(ValueTask);
		}

		var package = message.Value("package");

		if (string.IsNullOrEmpty(package)
			|| !message.TryGetVersion("min-version", out var theirMin)
			|| !message.TryGetVersion("max-version", out var theirMax))
		{
			Context.Logger.LogDebug("Ignoring a malformed {Message}", CanMessage);
			return default(ValueTask);
		}

		(McpVersion Min, McpVersion Max) ours;
		lock (_supported)
		{
			if (!_supported.TryGetValue(package!, out ours))
			{
				Context.Logger.LogDebug("The peer offers {Package}, which this side does not speak", package);
				return default(ValueTask);
			}
		}

		var min = theirMin > ours.Min ? theirMin : ours.Min;
		var max = theirMax < ours.Max ? theirMax : ours.Max;

		if (min > max)
		{
			Context.Logger.LogDebug(
				"No overlap on {Package}: the peer speaks {TheirMin} to {TheirMax} and this side {OurMin} to {OurMax}",
				package, theirMin, theirMax, ours.Min, ours.Max);
			return default(ValueTask);
		}

		lock (_agreed) _agreed[package!] = max;

		Context.Logger.LogDebug("Agreed on {Package} {Version}", package, max);
		return default(ValueTask);
	}

	/// <summary>The peer's list is finished.</summary>
	private async ValueTask OnEndAsync(McpMessage message)
	{
		// Announced once. A repeated end line is not a second negotiation.
		if (IsComplete)
		{
			Context.Logger.LogDebug("Ignoring a repeated {Message}", EndMessage);
			return;
		}

		IsComplete = true;
		await OnNegotiatedAsync(true);

		var agreed = Agreed;

		Context.Logger.LogDebug("The peer finished its package list; {Count} agreed", agreed.Count);

		if (_onComplete is not null)
		{
			await _onComplete(agreed);
		}
	}
}
