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
	public ValueTask SendMSDPCommand(byte[] variable, byte[] value)
	{
		// MSDP_VAR <variable> MSDP_VAL <value>
		var payload = new byte[1 + variable.Length + 1 + value.Length];
		payload[0] = (byte)Trigger.MSDP_VAR;
		variable.AsSpan().CopyTo(payload.AsSpan(1));
		payload[1 + variable.Length] = (byte)Trigger.MSDP_VAL;
		value.AsSpan().CopyTo(payload.AsSpan(1 + variable.Length + 1));

		return SendMSDPPayloadAsync(payload);
	}

	/// <summary>
	/// Sends an MSDP payload to the remote party, framed as a subnegotiation.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The payload is the variable/value sequence
	/// <c>MSDP_VAR &lt;name&gt; MSDP_VAL &lt;value&gt;</c> …, as
	/// <see cref="Functional.MSDPLibrary.ReportVariables"/> produces it; this wraps it in
	/// <c>IAC SB MSDP</c> … <c>IAC SE</c>. MSDP bytes written to the connection without that framing
	/// are not MSDP at all — they are ordinary output with control bytes in it, which a client
	/// renders as garbage.
	/// </para>
	/// <para>
	/// RFC 854 requires a literal <c>IAC</c> (0xFF) inside the payload to be doubled, or the peer's
	/// state machine reads it as the start of a command and the frame desyncs. MSDP says a variable
	/// or value "cannot contain the MSDP_VAR, MSDP_VAL, IAC, or NUL byte", so this should never fire
	/// on well-behaved data — but a non-ASCII <see cref="TelnetInterpreter.CurrentEncoding"/> can
	/// still encode a single character to 0xFF (ISO-8859-1 'ÿ'). MSDP's own structural bytes are 1
	/// through 6, so escaping the whole payload cannot disturb them.
	/// </para>
	/// </remarks>
	/// <param name="payload">The MSDP variable/value sequence, without framing.</param>
	public async ValueTask SendMSDPPayloadAsync(byte[] payload)
	{
		var safePayload = TelnetSafeBytes(payload);

		// IAC SB MSDP <payload> IAC SE
		var output = new byte[3 + safePayload.Length + 2];
		output[0] = (byte)Trigger.IAC;
		output[1] = (byte)Trigger.SB;
		output[2] = (byte)Trigger.MSDP;
		safePayload.AsSpan().CopyTo(output.AsSpan(3));
		output[output.Length - 2] = (byte)Trigger.IAC;
		output[output.Length - 1] = (byte)Trigger.SE;
		await WriteToNetworkAsync(output);
	}
}
