using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// The client side of the GMCP <c>Core</c> package for one connection: introducing the client,
/// telling the server which modules it supports, and measuring the round trip.
/// </summary>
/// <remarks>
/// <para>
/// Mudlet's order is the one to follow: as soon as GMCP is agreed, <see cref="HelloAsync"/> and
/// then <see cref="SetSupportsAsync"/>. "Core.Hello needs to be the first message that the client
/// sends."
/// </para>
/// <para>
/// With TelnetNegotiationCore, <c>.AddPlugin&lt;GMCPProtocol&gt;().UseGmcpClientSession(out var gmcp, modules)</c>
/// does this with the name and version from <c>WithClientIdentity</c>. By hand:
/// <code>
/// TelnetInterpreter? telnet = null;
/// var gmcp = new GmcpClientSession((package, data) => telnet!.SendGMCPCommand(package, data));
/// telnet = await builder
///     .AddPlugin&lt;GMCPProtocol&gt;()
///         .OnGMCPMessage(gmcp.HandleAsync)
///         .OnGMCPNegotiated(async agreed =>
///         {
///             if (agreed)
///             {
///                 await gmcp.HelloAsync("MyClient", "1.0");
///                 await gmcp.SetSupportsAsync([new("Char", 1), new("Room", 1)]);
///             }
///         })
///     .BuildAsync();
/// </code>
/// </para>
/// </remarks>
/// <param name="send">How to send a GMCP message on this connection.</param>
public sealed class GmcpClientSession(GmcpSend send)
{
	private long _pingStartedAt;
	private double? _roundTripMilliseconds;

	/// <summary>
	/// The round-trip time of the last answered <see cref="PingAsync"/>, in milliseconds, or null.
	/// </summary>
	public double? RoundTripMilliseconds => _roundTripMilliseconds;

	/// <summary>
	/// Called when the server's answer to <see cref="PingAsync"/> arrives, with the round trip in
	/// milliseconds.
	/// </summary>
	public Func<double, ValueTask>? OnRoundTripAsync { get; set; }

	/// <summary>
	/// Called on <c>Core.Goodbye</c>, with the reason the server gave. The server is about to close
	/// the connection.
	/// </summary>
	public Func<string?, ValueTask>? OnGoodbyeAsync { get; set; }

	/// <summary>
	/// Called for every message this session does not handle itself, with its package name and data.
	/// </summary>
	public Func<string, string, ValueTask>? OnMessageAsync { get; set; }

	/// <summary>
	/// Handles one GMCP message, in the shape <c>GMCPProtocol.OnGMCPMessage</c> delivers it.
	/// </summary>
	public ValueTask HandleAsync((string Package, string Info) message) =>
		HandleAsync(message.Package, message.Info);

	/// <summary>
	/// Handles one GMCP message.
	/// </summary>
	/// <param name="package">The package name. Matched without regard to case.</param>
	/// <param name="data">The data, as JSON text; empty when the message had none.</param>
	public async ValueTask HandleAsync(string package, string data)
	{
		data ??= string.Empty;

		if (Is(package, CorePackages.Ping))
		{
			var startedAt = Interlocked.Exchange(ref _pingStartedAt, 0);

			if (startedAt == 0)
			{
				// "Sent either by client or server": a ping this side did not start is answered the
				// way a server answers one, without a body.
				await send(CorePackages.Ping, string.Empty);
				return;
			}

			var elapsed = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
			_roundTripMilliseconds = elapsed;

			if (OnRoundTripAsync is not null)
			{
				await OnRoundTripAsync(elapsed);
			}
		}
		else if (Is(package, CorePackages.Goodbye))
		{
			if (OnGoodbyeAsync is not null)
			{
				await OnGoodbyeAsync(GmcpJson.ReadString(data));
			}
		}
		else if (OnMessageAsync is not null)
		{
			await OnMessageAsync(package, data);
		}
	}

	/// <summary>
	/// Sends <c>Core.Hello {"client": ..., "version": ...}</c>, the first message a client sends.
	/// </summary>
	/// <param name="client">The client's name.</param>
	/// <param name="version">The client's version. Left out of the message when null.</param>
	public ValueTask HelloAsync(string client, string? version)
	{
		var hello = new JsonObject { ["client"] = client };

		if (version is not null)
		{
			hello["version"] = version;
		}

		return send(CorePackages.Hello, hello.ToJsonString());
	}

	/// <summary>
	/// Sends <c>Core.Supports.Set</c>: the full list of modules this client supports, replacing any
	/// list sent before.
	/// </summary>
	public ValueTask SetSupportsAsync(IEnumerable<GmcpModule> modules) =>
		send(CorePackages.SupportsSet, GmcpJson.Strings(modules.Select(module => module.ToString())));

	/// <summary>
	/// Sends <c>Core.Supports.Add</c>: modules to add to the list, or whose version changes.
	/// </summary>
	public ValueTask AddSupportsAsync(IEnumerable<GmcpModule> modules) =>
		send(CorePackages.SupportsAdd, GmcpJson.Strings(modules.Select(module => module.ToString())));

	/// <summary>
	/// Sends <c>Core.Supports.Remove</c>: modules to take off the list, by name alone.
	/// </summary>
	public ValueTask RemoveSupportsAsync(IEnumerable<string> moduleNames) =>
		send(CorePackages.SupportsRemove, GmcpJson.Strings(moduleNames));

	/// <summary>
	/// Sends a message.
	/// </summary>
	/// <param name="package">The package name.</param>
	/// <param name="data">The data as JSON text, or empty for none.</param>
	public ValueTask SendAsync(string package, string data = "") => send(package, data ?? string.Empty);

	/// <summary>
	/// Sends a typed message, such as <see cref="LoginCredentials"/> or <see cref="DiscordHello"/>.
	/// </summary>
	public ValueTask SendAsync(IGmcpMessage message) => send(message.Package, message.ToJson());

	/// <summary>
	/// Sends <c>Core.KeepAlive</c>, which resets the server's idle timeout for the character.
	/// </summary>
	public ValueTask KeepAliveAsync() => send(CorePackages.KeepAlive, string.Empty);

	/// <summary>
	/// Sends <c>Core.Ping</c> and starts timing. The answer sets <see cref="RoundTripMilliseconds"/>.
	/// "The messages sent by the client get the last observed round trip time as a parameter", so
	/// every ping after the first carries the previous result.
	/// </summary>
	public ValueTask PingAsync()
	{
		var last = _roundTripMilliseconds;
		Interlocked.Exchange(ref _pingStartedAt, Math.Max(1, Stopwatch.GetTimestamp()));

		return send(CorePackages.Ping, last is { } milliseconds
			? Math.Round(milliseconds).ToString(CultureInfo.InvariantCulture)
			: string.Empty);
	}

	private static bool Is(string package, string expected) =>
		string.Equals(package, expected, StringComparison.OrdinalIgnoreCase);
}
