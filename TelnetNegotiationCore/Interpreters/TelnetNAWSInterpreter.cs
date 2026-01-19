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

	public async ValueTask SendNAWS(short width, short height)
	{
		if(!_WillingToDoNAWS) await ValueTask.CompletedTask;
		
		await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NAWS, 
			.. BitConverter.GetBytes(width), .. BitConverter.GetBytes(height), 
			(byte)Trigger.IAC, (byte)Trigger.SE]);
	}

	/// <summary>
	/// Request NAWS from a client
	/// </summary>
	public async ValueTask RequestNAWSAsync(StateMachine<State, Trigger>.Transition? _ = null)
	{
		if (!_WillingToDoNAWS)
		{
			_logger.LogDebug("Connection: {ConnectionState}", "Requesting NAWS details from Client");

			await CallbackNegotiationAsync([(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS]);
			_WillingToDoNAWS = true;
		}
	}

	private async ValueTask CompleteNAWSAsync(StateMachine<State, Trigger>.Transition _)
	{
		byte[] width = [_nawsByteState[0], _nawsByteState[1]];
		byte[] height = [_nawsByteState[2], _nawsByteState[3]];

		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(width);
			Array.Reverse(height);
		}

		ClientWidth = BitConverter.ToInt16(width);
		ClientHeight = BitConverter.ToInt16(height);

		_logger.LogDebug("Negotiated for: {clientWidth} width and {clientHeight} height", ClientWidth, ClientHeight);
		
		// Call NAWS plugin if available
		var nawsPlugin = PluginManager?.GetPlugin<Protocols.NAWSProtocol>();
		if (nawsPlugin != null && nawsPlugin.IsEnabled)
		{
			await nawsPlugin.OnNAWSNegotiatedAsync(ClientHeight, ClientWidth);
		}
	}
}
