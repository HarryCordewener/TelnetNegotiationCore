using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// Implements http://www.faqs.org/rfcs/rfc1073.html
/// </summary>
/// <remarks>
/// Reporting this side's window size lives on <see cref="Protocols.NAWSProtocol"/>
/// (<c>SendWindowSizeAsync</c>); what remains here is the received size and the server's request
/// for it.
/// </remarks>
public partial class TelnetInterpreter
{
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
	/// Whether this side has already asked the peer to report its window size.
	/// </summary>
	private bool _WillingToDoNAWS = false;

	// Cached negotiation byte array to avoid repeated allocations
	private static readonly byte[] s_doNAWS = [(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.NAWS];

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
}
