using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Handlers;

/// <summary>
/// The client half of MSDP: asks a server for variables with <c>LIST</c>, <c>SEND</c>,
/// <c>REPORT</c>, <c>UNREPORT</c> and <c>RESET</c>, and keeps the latest value of every variable
/// the server sends.
/// </summary>
/// <remarks>
/// <para>
/// https://tintin.mudhalla.net/protocols/msdp/ — a server answers every request with the variables
/// it names and their values, <c>MSDP_VAR "HEALTH" MSDP_VAL "100"</c>, and a reported variable is
/// sent again whenever it changes. So the client's state is one table, updated variable by
/// variable: a value replaces the one held before it.
/// </para>
/// <para>
/// Wire it to a connection with <c>.AddPlugin&lt;MSDPProtocol&gt;().OnMSDPMessage(handler.HandleAsync)</c>.
/// Requests go over native MSDP, or over GMCP as MSDP over GMCP when the server agreed to GMCP and
/// not to MSDP.
/// </para>
/// </remarks>
/// <param name="logger">
/// Optional. Records messages that were ignored and why.
/// </param>
public class MSDPClientHandler(ILogger? logger = null)
{
	private readonly ILogger _logger = logger ?? NullLogger.Instance;

	private readonly ConcurrentDictionary<string, JsonNode?> _variables = new(StringComparer.Ordinal);

	/// <summary>
	/// The latest value the server sent for each variable: text as a JSON string, an MSDP array as a
	/// JSON array, and an MSDP table as a JSON object. This includes the answers to <c>LIST</c>,
	/// which arrive as variables named after the list, such as <c>REPORTABLE_VARIABLES</c>.
	/// </summary>
	public IReadOnlyDictionary<string, JsonNode?> Variables => _variables;

	/// <summary>
	/// Called for every variable the server sends, after <see cref="Variables"/> holds the new value.
	/// Receives the variable's name and its value.
	/// </summary>
	public Func<string, JsonNode?, ValueTask>? OnVariableAsync { get; set; }

	/// <summary>
	/// Records one MSDP message from the server. Shaped to be passed straight to
	/// <c>OnMSDPMessage</c>.
	/// </summary>
	/// <param name="telnet">The connection the message arrived on.</param>
	/// <param name="serverJson">The message, as the JSON <c>OnMSDPMessage</c> delivers.</param>
	public ValueTask HandleAsync(TelnetInterpreter telnet, string serverJson) => HandleAsync(serverJson);

	/// <summary>
	/// Records one MSDP message from the server.
	/// </summary>
	/// <param name="serverJson">
	/// The message, as the JSON <c>OnMSDPMessage</c> delivers. Its properties are the variables the
	/// server sent.
	/// </param>
	public async ValueTask HandleAsync(string serverJson)
	{
		JsonNode? parsed;

		try
		{
			parsed = JsonNode.Parse(serverJson);
		}
		catch (JsonException ex)
		{
			_logger.LogWarning(ex, "Ignoring an MSDP message that is not valid JSON.");
			return;
		}

		if (parsed is not JsonObject message)
		{
			_logger.LogWarning("Ignoring an MSDP message that carries no variables: {Message}", serverJson);
			return;
		}

		foreach (var variable in message)
		{
			// Copied out, so the stored value does not keep the whole message alive or share a parent.
			var value = variable.Value?.DeepClone();
			_variables[variable.Key] = value;

			if (OnVariableAsync is not null)
			{
				await OnVariableAsync(variable.Key, value);
			}
		}
	}

	/// <summary>
	/// Asks the server for a list, such as <c>COMMANDS</c>, <c>LISTS</c> or <c>REPORTABLE_VARIABLES</c>.
	/// The answer arrives as a variable of the same name.
	/// </summary>
	public ValueTask ListAsync(TelnetInterpreter telnet, string list) =>
		RequestAsync(telnet, "LIST", [list]);

	/// <summary>
	/// Asks the server to send the current value of each variable once.
	/// </summary>
	public ValueTask SendAsync(TelnetInterpreter telnet, params string[] variables) =>
		RequestAsync(telnet, "SEND", variables);

	/// <summary>
	/// Asks the server to send each variable now and again whenever it changes.
	/// </summary>
	public ValueTask ReportAsync(TelnetInterpreter telnet, params string[] variables) =>
		RequestAsync(telnet, "REPORT", variables);

	/// <summary>
	/// Asks the server to stop reporting each variable.
	/// </summary>
	public ValueTask UnReportAsync(TelnetInterpreter telnet, params string[] variables) =>
		RequestAsync(telnet, "UNREPORT", variables);

	/// <summary>
	/// Asks the server to reset a group of variables, most often <c>REPORTED_VARIABLES</c>.
	/// </summary>
	public ValueTask ResetAsync(TelnetInterpreter telnet, string list) =>
		RequestAsync(telnet, "RESET", [list]);

	/// <summary>
	/// Sets one of the server's configurable variables, such as <c>CLIENT_NAME</c>.
	/// </summary>
	public ValueTask SetAsync(TelnetInterpreter telnet, string variable, string value) =>
		RequestAsync(telnet, variable, [value]);

	/// <summary>
	/// Sends <c>MSDP_VAR &lt;command&gt; MSDP_VAL &lt;argument&gt;</c>, with one <c>MSDP_VAL</c>
	/// per argument.
	/// </summary>
	/// <remarks>
	/// Several arguments are strung together as repeated values, the form the specification shows for
	/// command-like variables: <c>MSDP_VAR "REPORT" MSDP_VAL "HEALTH" MSDP_VAL "HEALTH_MAX"</c>.
	/// Over GMCP the same request is a JSON string, or an array when there are several.
	/// </remarks>
	private ValueTask RequestAsync(TelnetInterpreter telnet, string command, IReadOnlyList<string> arguments)
	{
		if (arguments.Count == 0)
		{
			return default;
		}

		if (telnet.MSDPGoesOverGMCP)
		{
			JsonNode argument = arguments.Count == 1
				? JsonValue.Create(arguments[0])
				: new JsonArray([.. ToNodes(arguments)]);

			return telnet.SendMSDPOverGMCPAsync(new JsonObject { [command] = argument });
		}

		var encoding = telnet.CurrentEncoding;
		var payload = new List<byte> { (byte)Trigger.MSDP_VAR };
		payload.AddRange(encoding.GetBytes(command));

		foreach (var argument in arguments)
		{
			payload.Add((byte)Trigger.MSDP_VAL);
			payload.AddRange(encoding.GetBytes(argument));
		}

		return telnet.SendMSDPPayloadAsync(payload.ToArray());
	}

	private static IEnumerable<JsonNode?> ToNodes(IEnumerable<string> values)
	{
		foreach (var value in values)
		{
			yield return JsonValue.Create(value);
		}
	}
}
