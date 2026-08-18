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
	/// <param name="variable">The MSDP command, as bytes.</param>
	/// <param name="value">The command's argument, as bytes.</param>
	public async ValueTask SendMSDPCommand(byte[] variable, byte[] value)
	{
		// IAC SB MSDP MSDP_VAR <variable> MSDP_VAL <value> IAC SE
		var output = new byte[4 + variable.Length + 1 + value.Length + 2];
		output[0] = (byte)Trigger.IAC;
		output[1] = (byte)Trigger.SB;
		output[2] = (byte)Trigger.MSDP;
		output[3] = (byte)Trigger.MSDP_VAR;
		variable.AsSpan().CopyTo(output.AsSpan(4));
		output[4 + variable.Length] = (byte)Trigger.MSDP_VAL;
		value.AsSpan().CopyTo(output.AsSpan(4 + variable.Length + 1));
		output[output.Length - 2] = (byte)Trigger.IAC;
		output[output.Length - 1] = (byte)Trigger.SE;
		await WriteToNetworkAsync(output);
	}
}
