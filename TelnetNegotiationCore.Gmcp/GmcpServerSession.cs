using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// The server side of the GMCP <c>Core</c> package for one connection: who the client is, which
/// modules it supports, and the answers Core asks of a server.
/// </summary>
/// <remarks>
/// <para>
/// Feed it every GMCP message the connection receives with <see cref="HandleAsync(string, string)"/>.
/// It handles <c>Core.Hello</c>, <c>Core.Supports.Set</c>/<c>Add</c>/<c>Remove</c>,
/// <c>Core.Ping</c> and <c>Core.KeepAlive</c>, and hands everything else to
/// <see cref="OnMessageAsync"/>.
/// </para>
/// <para>
/// With TelnetNegotiationCore:
/// <code>
/// TelnetInterpreter? telnet = null;
/// var gmcp = new GmcpServerSession((package, data) => telnet!.SendGMCPCommand(package, data));
/// telnet = await builder
///     .AddPlugin&lt;GMCPProtocol&gt;().OnGMCPMessage(gmcp.HandleAsync)
///     .BuildAsync();
/// </code>
/// </para>
/// <para>
/// Mudlet sends <c>Core.Hello</c> and then <c>Core.Supports.Set</c> as soon as GMCP is agreed.
/// Blightmud sends <c>Core.Hello</c> and only <c>Core.Supports.Add</c>, one module at a time, when a
/// script registers one. TinTin++ and MUSHclient send neither unless a script does. So an empty
/// <see cref="SupportedModules"/> usually means "never said", not "supports nothing", which is why
/// <see cref="SendAsync(string, string)"/> does not consult it and <see cref="SendIfSupportedAsync(string, string)"/> is opt-in.
/// </para>
/// </remarks>
/// <param name="send">How to send a GMCP message on this connection.</param>
public sealed class GmcpServerSession(GmcpSend send)
{
	private readonly object _lock = new();
	private Dictionary<string, int> _modules = new(StringComparer.OrdinalIgnoreCase);
	private int _pingSent;

	/// <summary>
	/// The client's name from <c>Core.Hello</c>, or null before one arrives.
	/// </summary>
	public string? ClientName { get; private set; }

	/// <summary>
	/// The client's version from <c>Core.Hello</c>, or null before one arrives.
	/// </summary>
	public string? ClientVersion { get; private set; }

	/// <summary>
	/// The round-trip time in milliseconds the client last reported in a <c>Core.Ping</c>, or null.
	/// </summary>
	public double? ReportedRoundTripMilliseconds { get; private set; }

	/// <summary>
	/// The modules the client listed through <c>Core.Supports</c>, and the version of each. Names
	/// are matched without regard to case.
	/// </summary>
	public IReadOnlyDictionary<string, int> SupportedModules
	{
		get
		{
			lock (_lock)
			{
				return _modules;
			}
		}
	}

	/// <summary>
	/// Called after a <c>Core.Hello</c>, with the client's name and version.
	/// </summary>
	public Func<string?, string?, ValueTask>? OnHelloAsync { get; set; }

	/// <summary>
	/// Called after <c>Core.Supports.Set</c>, <c>Add</c> or <c>Remove</c> changed
	/// <see cref="SupportedModules"/>.
	/// </summary>
	public Func<IReadOnlyDictionary<string, int>, ValueTask>? OnSupportsChangedAsync { get; set; }

	/// <summary>
	/// Called on <c>Core.KeepAlive</c>: the client asks that its idle timeout be reset.
	/// </summary>
	public Func<ValueTask>? OnKeepAliveAsync { get; set; }

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

		if (Is(package, CorePackages.Hello))
		{
			await HandleHelloAsync(data);
		}
		else if (Is(package, CorePackages.SupportsSet))
		{
			await ChangeSupportsAsync(data, replace: true, remove: false);
		}
		else if (Is(package, CorePackages.SupportsAdd))
		{
			await ChangeSupportsAsync(data, replace: false, remove: false);
		}
		else if (Is(package, CorePackages.SupportsRemove))
		{
			await ChangeSupportsAsync(data, replace: false, remove: true);
		}
		else if (Is(package, CorePackages.Ping))
		{
			// The answer to a ping this server sent. Answering it again would start the client
			// answering in turn, and the two would ping each other for as long as they are connected.
			if (Interlocked.Exchange(ref _pingSent, 0) == 1)
			{
				return;
			}

			// "The server responds to the request by replying with Core.Ping without a body."
			ReportedRoundTripMilliseconds = GmcpJson.ReadNumber(data) ?? ReportedRoundTripMilliseconds;
			await send(CorePackages.Ping, string.Empty);
		}
		else if (Is(package, CorePackages.KeepAlive))
		{
			if (OnKeepAliveAsync is not null)
			{
				await OnKeepAliveAsync();
			}
		}
		else if (OnMessageAsync is not null)
		{
			await OnMessageAsync(package, data);
		}
	}

	/// <summary>
	/// True when the client listed <paramref name="package"/> or a module it belongs to:
	/// <c>Char.Vitals</c> is supported when the client listed <c>Char.Vitals</c> or <c>Char</c>.
	/// </summary>
	public bool Supports(string package)
	{
		var modules = SupportedModules;
		var name = package;

		while (true)
		{
			if (modules.ContainsKey(name))
			{
				return true;
			}

			var dot = name.LastIndexOf('.');

			if (dot <= 0)
			{
				return false;
			}

			name = name.Substring(0, dot);
		}
	}

	/// <summary>
	/// Sends a message.
	/// </summary>
	/// <param name="package">The package name, spelled as the client expects it.</param>
	/// <param name="data">The data as JSON text, or empty for none.</param>
	/// <remarks>
	/// A <c>Core.Ping</c> sent this way expects an answer, and that answer is not answered again.
	/// </remarks>
	public ValueTask SendAsync(string package, string data = "") => SendAndTrackAsync(package, data);

	/// <summary>
	/// Sends a typed message, such as <see cref="MediaPlay"/> or <see cref="RoomInfo"/>.
	/// </summary>
	public ValueTask SendAsync(IGmcpMessage message) => SendAndTrackAsync(message.Package, message.ToJson());

	/// <summary>
	/// Sends a typed message only if <see cref="Supports"/> says the client listed its module.
	/// </summary>
	/// <returns>True if the message was sent.</returns>
	public ValueTask<bool> SendIfSupportedAsync(IGmcpMessage message) =>
		Supports(message.Package) ? SendSupportedAsync(message) : new ValueTask<bool>(false);

	private async ValueTask<bool> SendSupportedAsync(IGmcpMessage message)
	{
		await SendAndTrackAsync(message.Package, message.ToJson());
		return true;
	}

	/// <summary>
	/// Sends a message only if <see cref="Supports"/> says the client listed its module.
	/// </summary>
	/// <returns>True if the message was sent.</returns>
	public async ValueTask<bool> SendIfSupportedAsync(string package, string data = "")
	{
		if (!Supports(package))
		{
			return false;
		}

		await SendAndTrackAsync(package, data);
		return true;
	}

	private ValueTask SendAndTrackAsync(string package, string? data)
	{
		if (Is(package, CorePackages.Ping))
		{
			Interlocked.Exchange(ref _pingSent, 1);
		}

		return send(package, data ?? string.Empty);
	}

	/// <summary>
	/// Sends <c>Core.Goodbye</c>, "immediately before terminating a connection". The reason is
	/// shown to the user.
	/// </summary>
	public ValueTask GoodbyeAsync(string reason) => send(CorePackages.Goodbye, GmcpJson.String(reason));

	/// <summary>
	/// Forgets what the client said: its identity and its module list. For a copyover, after which
	/// "the server and client behave as if the client has just connected".
	/// </summary>
	public void Reset()
	{
		lock (_lock)
		{
			_modules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		}

		ClientName = null;
		ClientVersion = null;
		ReportedRoundTripMilliseconds = null;
	}

	private async ValueTask HandleHelloAsync(string data)
	{
		// Mudlet, Blightmud and the schema page write "client" and "version"; the Core page's example
		// writes "Client" and "Version". Both are read.
		var values = GmcpJson.ReadProperties(data, "client", "version");

		if (values is null)
		{
			return;
		}

		ClientName = values[0];
		ClientVersion = values[1];

		if (OnHelloAsync is not null)
		{
			await OnHelloAsync(ClientName, ClientVersion);
		}
	}

	private async ValueTask ChangeSupportsAsync(string data, bool replace, bool remove)
	{
		var entries = GmcpJson.ReadStrings(data);

		if (entries is null)
		{
			return;
		}

		IReadOnlyDictionary<string, int> changed;

		lock (_lock)
		{
			// Copy on write, so a reader holding the old dictionary never sees it change.
			var modules = replace
				? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
				: new Dictionary<string, int>(_modules, StringComparer.OrdinalIgnoreCase);

			foreach (var entry in entries)
			{
				if (!GmcpModule.TryParse(entry, out var module))
				{
					continue;
				}

				if (remove)
				{
					// "No version numbers included", though Blightmud includes one; the name is what counts.
					modules.Remove(module.Name);
				}
				else
				{
					// "the new version number takes precedence over the previously sent one, even if
					// the newly sent number is lower."
					modules[module.Name] = module.Version;
				}
			}

			_modules = modules;
			changed = modules;
		}

		if (OnSupportsChangedAsync is not null)
		{
			await OnSupportsChangedAsync(changed);
		}
	}

	private static bool Is(string package, string expected) =>
		string.Equals(package, expected, StringComparison.OrdinalIgnoreCase);
}
