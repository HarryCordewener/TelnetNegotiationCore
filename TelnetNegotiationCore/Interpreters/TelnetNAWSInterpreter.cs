using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// Implements http://www.faqs.org/rfcs/rfc1073.html
/// </summary>
/// <remarks>
/// TODO: Implement Client Side
/// </remarks>
public partial class TelnetInterpreter
{
#pragma warning disable CS0414 // Field is assigned but never used in this partial - used in NAWSProtocol
	/// <summary>
	/// Internal NAWS Byte State
	/// </summary>
	private byte[] _nawsByteState = [];

	/// <summary>
	/// Internal NAWS Byte Index Value
	/// </summary>
	private int _nawsIndex = 0;
#pragma warning restore CS0414

	/// <summary>
	/// Currently known Client Height
	/// </summary>
	/// <remarks>
	/// Defaults to 24
	/// </remarks>
	public int ClientHeight { get; private set; } = 24;

	/// <summary>
	/// Currently known Client Width.
	/// </summary>
	/// <remarks>
	/// Defaults to 78
	/// </remarks>
	public int ClientWidth { get; private set; } = 78;

	/// <summary>
	/// NAWS Callback function to alert server of Width & Height negotiation
	/// </summary>
	private bool _WillingToDoNAWS = false;

	// Cached negotiation byte array to avoid repeated allocations
	private static readonly byte[] s_doNAWS = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS];

	public async ValueTask SendNAWS(short width, short height)
	{
		// Only report window size once NAWS is actually enabled with the peer. When the plugin is
		// in use it owns that state (for a client, set when the server sends DO NAWS); otherwise
		// fall back to the legacy interpreter flag. Sending an SB NAWS unsolicited desyncs a strict
		// server's telnet parser and can make it swallow the following line (RFC 1073).
		var nawsPlugin = PluginManager?.GetPlugin<Protocols.NAWSProtocol>();
		var enabled = nawsPlugin is { IsEnabled: true }
			? nawsPlugin.WindowSizeReportingEnabled
			: _WillingToDoNAWS;
		if (!enabled)
		{
			return;
		}

		// RFC 1073: "IAC SB NAWS WIDTH[1] WIDTH[0] HEIGHT[1] HEIGHT[0] IAC SE", high byte first
		// (network byte order). Shifting explicitly is endian-independent, so this is one code path
		// on every target framework.
		//
		// RFC 1073: "As required by the Telnet protocol, any occurrence of 255 in the subnegotiation
		// must be doubled to distinguish it from the IAC character (which has a value of 255)."
		// A dimension byte of 255 is an ordinary terminal size, not a corner case - a 255-column
		// window, or any height/width whose high or low byte happens to be 255. Sent raw, the peer
		// reads that byte as the IAC that ends the subnegotiation and the rest of the stream desyncs.
		var dimensions = TelnetSafeBytesInternal(new byte[]
		{
			(byte)(width >> 8), (byte)width,
			(byte)(height >> 8), (byte)height
		});

		await WriteToNetworkAsync((byte[])
		[
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS,
			.. dimensions,
			(byte)Trigger.IAC, (byte)Trigger.SE
		]);
	}

	/// <summary>
	/// Request NAWS from a client
	/// </summary>
	public async ValueTask RequestNAWSAsync(StateMachine<State, Trigger>.Transition? _ = null)
	{
		if (!_WillingToDoNAWS)
		{
			_logger.LogDebug("Connection: {ConnectionState}", "Requesting NAWS details from Client");

			await WriteToNetworkAsync(s_doNAWS);
			_WillingToDoNAWS = true;
		}
	}

	private async ValueTask CompleteNAWSAsync(StateMachine<State, Trigger>.Transition _)
	{
		// See NAWSProtocol.CompleteNAWSAsync: RFC 1073 is high byte first and allows up to 65535,
		// so the pair must be read unsigned rather than through BitConverter.ToInt16.
		ClientWidth = (_nawsByteState[0] << 8) | _nawsByteState[1];
		ClientHeight = (_nawsByteState[2] << 8) | _nawsByteState[3];

		_logger.LogDebug("Negotiated for: {clientWidth} width and {clientHeight} height", ClientWidth, ClientHeight);
		
		// Call NAWS plugin if available
		var nawsPlugin = PluginManager?.GetPlugin<Protocols.NAWSProtocol>();
		if (nawsPlugin != null && nawsPlugin.IsEnabled)
		{
			await nawsPlugin.OnNAWSNegotiatedAsync(ClientHeight, ClientWidth);
		}
	}
}
