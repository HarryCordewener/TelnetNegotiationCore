using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	/// <summary>
	/// Sends a GMCP command to the remote party.
	/// </summary>
	/// <param name="package">The GMCP package name (e.g., "Core.Hello", "Char.Vitals").</param>
	/// <param name="command">The JSON data to send as a string.</param>
	/// <returns>A ValueTask representing the asynchronous operation.</returns>
	/// <example>
	/// await telnet.SendGMCPCommand("Char.Vitals", "{\"hp\":1000,\"maxhp\":1500}");
	/// </example>
	public ValueTask SendGMCPCommand(string package, string command) =>
		SendGMCPCommand(CurrentEncoding.GetBytes(package), CurrentEncoding.GetBytes(command));

	/// <summary>
	/// Sends a GMCP command to the remote party.
	/// </summary>
	/// <param name="package">The GMCP package name (e.g., "Core.Hello", "Char.Vitals").</param>
	/// <param name="command">The JSON data to send as a byte array.</param>
	/// <returns>A ValueTask representing the asynchronous operation.</returns>
	public ValueTask SendGMCPCommand(string package, byte[] command) =>
		SendGMCPCommand(CurrentEncoding.GetBytes(package), command);

	/// <summary>
	/// Sends a GMCP command to the remote party.
	/// </summary>
	/// <remarks>
	/// RFC 854 requires a literal <c>IAC</c> (0xFF) inside data to be doubled, or the peer's state
	/// machine reads it as the start of a command and desyncs mid-payload. GMCP's data section is
	/// normally UTF-8 JSON, which cannot itself produce a raw 0xFF byte - but a non-ASCII
	/// <see cref="TelnetInterpreter.CurrentEncoding"/> can still turn one character into 0xFF
	/// (ISO-8859-1 'ÿ'), same as <c>Protocols.MSSPProtocol.AppendEscaped</c> guards against for MSSP.
	/// Escaped through the shared <see cref="TelnetSafeBytes"/> rather than a second IAC-doubling loop.
	/// </remarks>
	/// <param name="package">The GMCP package name as a byte array.</param>
	/// <param name="command">The JSON data to send as a byte array.</param>
	/// <returns>A ValueTask representing the asynchronous operation.</returns>
	public async ValueTask SendGMCPCommand(byte[] package, byte[] command)
	{
		var safePackage = TelnetSafeBytes(package);
		var safeCommand = TelnetSafeBytes(command);

		// Pre-allocate exact-size buffer: IAC SB GMCP <package> ' ' <command> IAC SE
		var output = new byte[3 + safePackage.Length + 1 + safeCommand.Length + 2];
		output[0] = (byte)Trigger.IAC;
		output[1] = (byte)Trigger.SB;
		output[2] = (byte)Trigger.GMCP;
		safePackage.AsSpan().CopyTo(output.AsSpan(3));
		output[3 + safePackage.Length] = (byte)' ';
		safeCommand.AsSpan().CopyTo(output.AsSpan(3 + safePackage.Length + 1));
		output[output.Length - 2] = (byte)Trigger.IAC;
		output[output.Length - 1] = (byte)Trigger.SE;
		await WriteToNetworkAsync(output);
	}
}
