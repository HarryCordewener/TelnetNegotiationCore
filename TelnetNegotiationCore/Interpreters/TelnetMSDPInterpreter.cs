using System;
using System.Text.Json.Nodes;
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

	/// <summary>
	/// The GMCP package that carries MSDP over GMCP. Case sensitive: "the package name is
	/// considered case sensitive and MSDP must be fully capitalized".
	/// </summary>
	private const string MSDPOverGMCPPackage = "MSDP";

	/// <summary>
	/// Sends MSDP variables and their values to the remote party, over whichever option the peer
	/// accepted: native MSDP, or MSDP over GMCP when GMCP is the only one of the two it agreed to.
	/// </summary>
	/// <remarks>
	/// <para>
	/// MSDP over GMCP exists for "clients that only support GMCP": such a client sends
	/// <c>IAC SB GMCP 'MSDP {"LIST" : "COMMANDS"}' IAC SE</c> and is answered the same way,
	/// <c>IAC SB GMCP 'MSDP {"COMMANDS" : [...]}' IAC SE</c>. Answering it with
	/// <c>IAC SB MSDP</c> instead writes a subnegotiation for an option the peer refused, which
	/// it drops.
	/// </para>
	/// <para>
	/// When the peer accepted both, native MSDP is used: the specification expects such a client
	/// "to be able to process both MSDP and GMCP data interchangably". Native MSDP is also the
	/// answer when neither option has finished negotiating, which is what this sent before MSDP
	/// over GMCP was answered at all.
	/// </para>
	/// </remarks>
	/// <param name="variables">
	/// The variables, as a JSON object: each property is a variable, and its value is text, an
	/// array or a table.
	/// </param>
	public ValueTask SendMSDPVariablesAsync(JsonObject variables) =>
		MSDPGoesOverGMCP
			? SendMSDPOverGMCPAsync(variables)
			: SendMSDPPayloadAsync(Functional.MSDPLibrary.ReportVariables(variables, CurrentEncoding));

	/// <summary>
	/// True when MSDP has to travel as MSDP over GMCP: the peer agreed to GMCP and not to MSDP.
	/// </summary>
	internal bool MSDPGoesOverGMCP =>
		PluginManager?.GetPlugin<Protocols.GMCPProtocol>() is { IsEnabled: true, IsNegotiated: true }
		&& PluginManager.GetPlugin<Protocols.MSDPProtocol>() is not { IsEnabled: true, IsNegotiated: true };

	/// <summary>
	/// Sends variables as <c>IAC SB GMCP 'MSDP {...}' IAC SE</c>.
	/// </summary>
	internal ValueTask SendMSDPOverGMCPAsync(JsonObject variables) =>
		SendGMCPCommand(MSDPOverGMCPPackage, variables.ToJsonString());
}
