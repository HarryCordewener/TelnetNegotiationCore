using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// Holds the latest data for each package until the game's next tick, then sends each package
/// once, and only if its data differs from what was last sent.
/// </summary>
/// <remarks>
/// <para>
/// A game that changes hit points five times between ticks would otherwise send five
/// <c>Char.Vitals</c>. KaVir's snippet, MTH and CoffeeMud all batch this way: record on change,
/// send on the tick, skip what did not change.
/// </para>
/// <para>
/// To send only what the client listed in <c>Core.Supports</c>, pass
/// <c>async (package, data) =&gt; await session.SendIfSupportedAsync(package, data)</c> as the
/// sender.
/// </para>
/// </remarks>
/// <param name="send">How to send a GMCP message on this connection.</param>
public sealed class GmcpBatch(GmcpSend send)
{
	private readonly object _lock = new();
	private readonly List<string> _order = [];
	private readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _lastSent = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Records the package's latest data, replacing any set since the last flush. Sends nothing.
	/// </summary>
	/// <param name="package">The package name, spelled as the client expects it.</param>
	/// <param name="data">The data as JSON text, or empty for none.</param>
	public void Set(string package, string data)
	{
		lock (_lock)
		{
			if (!_pending.ContainsKey(package))
			{
				_order.Add(package);
			}

			_pending[package] = data ?? string.Empty;
		}
	}

	/// <summary>
	/// Records a typed message's latest data. See <see cref="Set(string, string)"/>.
	/// </summary>
	public void Set(IGmcpMessage message) => Set(message.Package, message.ToJson());

	/// <summary>
	/// Sends each package set since the last flush, in the order first set, leaving out any whose
	/// data is the same as the data last sent for it.
	/// </summary>
	public async ValueTask FlushAsync()
	{
		List<KeyValuePair<string, string>> changed;

		lock (_lock)
		{
			if (_order.Count == 0)
			{
				return;
			}

			changed = new(_order.Count);

			foreach (var package in _order)
			{
				var data = _pending[package];

				if (_lastSent.TryGetValue(package, out var last) && last == data)
				{
					continue;
				}

				_lastSent[package] = data;
				changed.Add(new(package, data));
			}

			_order.Clear();
			_pending.Clear();
		}

		foreach (var message in changed)
		{
			await send(message.Key, message.Value);
		}
	}

	/// <summary>
	/// Forgets what was sent, so the next flush sends every package set, changed or not. For a
	/// copyover, or after the client sends a new <c>Core.Supports</c>.
	/// </summary>
	public void Forget()
	{
		lock (_lock)
		{
			_lastSent.Clear();
		}
	}
}
