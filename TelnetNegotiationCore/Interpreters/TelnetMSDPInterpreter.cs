using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	/// <summary>
	/// Sends an MSDP variable/value pair to the remote party.
	/// </summary>
	/// <remarks>
	/// This is the client half of MSDP's request vocabulary. <c>SEND</c>, <c>REPORT</c>,
	/// <c>UNREPORT</c>, <c>LIST</c> and <c>RESET</c> are not separate wire forms - each is this same
	/// shape, <c>IAC SB MSDP MSDP_VAR &lt;command&gt; MSDP_VAL &lt;argument&gt; IAC SE</c>, with the
	/// command name itself carried as the variable. <see cref="Handlers.MSDPServerHandler"/> is what
	/// answers these on the server side; nothing until now built the client side that asks.
	/// </remarks>
	/// <param name="variable">The MSDP command, e.g. "SEND", "REPORT", "LIST".</param>
	/// <param name="value">The command's argument, e.g. "PLAYERS", "REPORTABLE_VARIABLES".</param>
	/// <example>
	/// await telnet.SendMSDPCommand("SEND", "PLAYERS");
	/// </example>
	public ValueTask SendMSDPCommand(string variable, string value) =>
		SendMSDPCommand(CurrentEncoding.GetBytes(variable), CurrentEncoding.GetBytes(value));

	/// <summary>
	/// Sends an MSDP variable/value pair to the remote party, from raw bytes.
	/// </summary>
	/// <remarks>
	/// RFC 854 requires a literal <c>IAC</c> (0xFF) inside data to be doubled, or the peer's state
	/// machine reads it as the start of a command and desyncs. MSDP itself says a variable or value
	/// "cannot contain the MSDP_VAR, MSDP_VAL, IAC, or NUL byte", so this should never fire on a
	/// well-behaved argument - but a non-ASCII <see cref="TelnetInterpreter.CurrentEncoding"/> can
	/// still encode a single character to 0xFF (ISO-8859-1 'ÿ'), same as
	/// <c>Protocols.MSSPProtocol.AppendEscaped</c> exists to guard against for MSSP. Escaping through
	/// the shared <see cref="TelnetSafeBytes"/> - the same helper <c>SendAsync</c> and
	/// <c>SendPromptAsync</c> already use - rather than a second IAC-doubling loop.
	/// </remarks>
	/// <param name="variable">The MSDP command, as bytes.</param>
	/// <param name="value">The command's argument, as bytes.</param>
	public async ValueTask SendMSDPCommand(byte[] variable, byte[] value)
	{
		var safeVariable = TelnetSafeBytes(variable);
		var safeValue = TelnetSafeBytes(value);

		// IAC SB MSDP MSDP_VAR <variable> MSDP_VAL <value> IAC SE
		var output = new byte[4 + safeVariable.Length + 1 + safeValue.Length + 2];
		output[0] = (byte)Trigger.IAC;
		output[1] = (byte)Trigger.SB;
		output[2] = (byte)Trigger.MSDP;
		output[3] = (byte)Trigger.MSDP_VAR;
		safeVariable.AsSpan().CopyTo(output.AsSpan(4));
		output[4 + safeVariable.Length] = (byte)Trigger.MSDP_VAL;
		safeValue.AsSpan().CopyTo(output.AsSpan(4 + safeVariable.Length + 1));
		output[output.Length - 2] = (byte)Trigger.IAC;
		output[output.Length - 1] = (byte)Trigger.SE;
		await WriteToNetworkAsync(output);
	}
}
