using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

public partial class TelnetInterpreter
{
	/// <summary>
	/// Sends a GMCP command to the remote party using UTF-8, independently of terminal CHARSET.
	/// </summary>
	/// <param name="package">The GMCP package name (e.g., "Core.Hello", "Char.Vitals").</param>
	/// <param name="command">The JSON data to send as a string, or an empty string for a message with no data.</param>
	/// <returns>A ValueTask representing the asynchronous operation.</returns>
	/// <example>
	/// await telnet.SendGMCPCommand("Char.Vitals", "{\"hp\":1000,\"maxhp\":1500}");
	/// </example>
	public ValueTask SendGMCPCommand(string package, string command)
	{
		if (package is null) throw new ArgumentNullException(nameof(package));
		if (command is null) throw new ArgumentNullException(nameof(command));

		// Encoded straight into the frame. UTF-8 never produces 0xFF, so neither part can hold an IAC
		// to escape, and the frame is one allocation instead of five.
		var utf8 = System.Text.Encoding.UTF8;
		var packageLength = utf8.GetByteCount(package);
		var commandLength = utf8.GetByteCount(command);
		var separator = commandLength == 0 ? 0 : 1;

		var output = new byte[3 + packageLength + separator + commandLength + 2];
		output[0] = (byte)Trigger.IAC;
		output[1] = (byte)Trigger.SB;
		output[2] = (byte)Trigger.GMCP;
		utf8.GetBytes(package, 0, package.Length, output, 3);
		if (separator == 1)
		{
			output[3 + packageLength] = (byte)' ';
		}

		utf8.GetBytes(command, 0, command.Length, output, 3 + packageLength + separator);
		output[output.Length - 2] = (byte)Trigger.IAC;
		output[output.Length - 1] = (byte)Trigger.SE;
		return WriteToNetworkAsync(output);
	}

	/// <summary>
	/// Sends a GMCP command to the remote party.
	/// </summary>
	/// <param name="package">The GMCP package name (e.g., "Core.Hello", "Char.Vitals").</param>
	/// <param name="command">The JSON data to send as a byte array.</param>
	/// <returns>A ValueTask representing the asynchronous operation.</returns>
	public ValueTask SendGMCPCommand(string package, byte[] command) =>
		SendGMCPCommand(System.Text.Encoding.UTF8.GetBytes(package), command);

	/// <summary>
	/// Sends a GMCP command to the remote party.
	/// </summary>
	/// <remarks>
	/// RFC 854 requires a literal <c>IAC</c> (0xFF) inside data to be doubled, or the peer's state
	/// machine reads it as the start of a command and desyncs mid-payload. GMCP's data section is
	/// normally UTF-8 JSON, which cannot itself produce a raw 0xFF byte, but caller-supplied
	/// bytes can still contain it, as <c>Protocols.MSSPProtocol.AppendEscaped</c> guards against for MSSP.
	/// Escaped through the shared <see cref="TelnetSafeBytes"/> rather than a second IAC-doubling loop.
	/// </remarks>
	/// <param name="package">The GMCP package name as a byte array.</param>
	/// <param name="command">The JSON data to send as a byte array.</param>
	/// <returns>A ValueTask representing the asynchronous operation.</returns>
	public async ValueTask SendGMCPCommand(byte[] package, byte[] command)
	{
		var packageLength = Helpers.SubnegotiationEscaping.EscapedLength(package);
		var commandLength = Helpers.SubnegotiationEscaping.EscapedLength(command);

		// "When sending a command without a data section the space should be omitted": Core.Ping,
		// Core.KeepAlive and their kind are the package name alone.
		var separator = commandLength == 0 ? 0 : 1;

		// One exact-size buffer, escaped straight into: IAC SB GMCP <package> [' ' <command>] IAC SE
		var output = new byte[3 + packageLength + separator + commandLength + 2];
		output[0] = (byte)Trigger.IAC;
		output[1] = (byte)Trigger.SB;
		output[2] = (byte)Trigger.GMCP;
		Helpers.SubnegotiationEscaping.CopyEscaped(package, output.AsSpan(3));
		if (separator == 1)
		{
			output[3 + packageLength] = (byte)' ';
		}
		Helpers.SubnegotiationEscaping.CopyEscaped(command, output.AsSpan(3 + packageLength + separator));
		output[output.Length - 2] = (byte)Trigger.IAC;
		output[output.Length - 1] = (byte)Trigger.SE;
		await WriteToNetworkAsync(output);
	}
}
