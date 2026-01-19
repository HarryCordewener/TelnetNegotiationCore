using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;
using System.Threading.Channels;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// Implements http://www.faqs.org/rfcs/rfc1073.html
/// </summary>
/// <remarks>
/// TODO: Implement Client Side
/// </remarks>
public partial class TelnetInterpreter
{
	/// <summary>
	/// Bounded channel for MSDP message assembly (max 8KB per message).
	/// </summary>
	private Channel<byte> _msdpByteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(8192)
	{
		FullMode = BoundedChannelFullMode.DropWrite  // Drop bytes if message too large (DOS protection)
	});

	private void CaptureMSDPValue(OneOf<byte, Trigger> b) => _msdpByteChannel.Writer.TryWrite(b.AsT0);

	private async ValueTask ReadMSDPValues()
	{
		// Read all bytes from channel
		var msdpBytes = new List<byte>(256);
		while (_msdpByteChannel.Reader.TryRead(out var bt))
		{
			msdpBytes.Add(bt);
			if (msdpBytes.Count >= 8192)
			{
				_logger.LogWarning("MSDP message too large (>8KB), truncating");
				break;
			}
		}

		if (msdpBytes.Count > 0)
		{
			// Call MSDP plugin if available
			var msdpPlugin = PluginManager?.GetPlugin<Protocols.MSDPProtocol>();
			if (msdpPlugin != null && msdpPlugin.IsEnabled)
			{
				await msdpPlugin.OnMSDPMessageAsync(this, JsonSerializer.Serialize(Functional.MSDPLibrary.MSDPScan(msdpBytes.Skip(1), CurrentEncoding)));
			}
		}
	}
}
