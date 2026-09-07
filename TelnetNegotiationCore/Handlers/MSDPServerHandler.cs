using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TelnetNegotiationCore.Functional;
using TelnetNegotiationCore.Interpreters;

namespace TelnetNegotiationCore.Handlers;

/// <summary>
/// The server half of MSDP: answers a client's <c>LIST</c>, <c>SEND</c>, <c>REPORT</c>,
/// <c>UNREPORT</c> and <c>RESET</c> requests out of a <see cref="MSDPServerModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// https://tintin.mudhalla.net/protocols/msdp/ — every request and every answer is the same wire
/// shape, <c>IAC SB MSDP MSDP_VAR &lt;name&gt; MSDP_VAL &lt;value&gt; IAC SE</c>, with the command
/// name carried as the variable. So a client asking
/// <c>MSDP_VAR "SEND" MSDP_VAL "HINT"</c> is answered with
/// <c>MSDP_VAR "HINT" MSDP_VAL "THE GAME"</c>: the variable it named, and that variable's current
/// value.
/// </para>
/// <para>
/// Wire it to a connection with <c>.AddPlugin&lt;MSDPProtocol&gt;().OnMSDPMessage(handler.HandleAsync)</c>.
/// </para>
/// </remarks>
/// <param name="model">
/// A model that resolves lists, variables and their values.
/// </param>
/// <param name="logger">
/// Optional. Records what was ignored and why — a request naming a list or a variable this server
/// does not offer is dropped, and silence is otherwise hard to explain.
/// </param>
public class MSDPServerHandler(MSDPServerModel model, ILogger? logger = null)
{
    private const string ReportedVariables = "REPORTED_VARIABLES";

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Current Data.
    /// </summary>
    public MSDPServerModel Data { get; private init; } = model;

    /// <summary>
    /// Answers one MSDP message from a client.
    /// </summary>
    /// <param name="telnet">The connection the message arrived on, and the one answered.</param>
    /// <param name="clientJson">
    /// The message, as the JSON <c>OnMSDPMessage</c> delivers. Its properties are the variables the
    /// client sent: the five commands, or a configurable variable the client is setting.
    /// </param>
    public async ValueTask HandleAsync(TelnetInterpreter telnet, string clientJson)
    {
        JsonNode? parsed;

        try
        {
            parsed = JsonNode.Parse(clientJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring an MSDP message that is not valid JSON.");
            return;
        }

        if (parsed is not JsonObject message)
        {
            _logger.LogWarning(
                "Ignoring an MSDP message that carries no variables: {Message}", clientJson);
            return;
        }

        // A subnegotiation may carry several variables - the specification's configurable-variable
        // example sets two at once - so every one of them is answered, not just the first.
        foreach (var variable in message)
        {
            switch (variable.Key)
            {
                case "LIST":
                    await HandleListRequestAsync(telnet, variable.Value);
                    break;
                case "SEND":
                    await HandleSendRequestAsync(telnet, variable.Value);
                    break;
                case "REPORT":
                    await HandleReportRequestAsync(telnet, variable.Value);
                    break;
                case "UNREPORT":
                    HandleUnReportRequest(variable.Value);
                    break;
                case "RESET":
                    await HandleResetRequestAsync(variable.Value);
                    break;
                default:
                    await HandleConfigurationAsync(variable.Key, variable.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// Answers a request to LIST a group of variables, with the group named:
    /// <c>MSDP_VAR "COMMANDS" MSDP_VAL MSDP_ARRAY_OPEN MSDP_VAL "LIST" … MSDP_ARRAY_CLOSE</c>.
    /// Should at least support:
    ///		"COMMANDS"               Request an array of commands supported by the server.
    ///		"LISTS"                  Request an array of lists supported by the server.
    ///		"CONFIGURABLE_VARIABLES" Request an array of variables the client can configure.
    ///		"REPORTABLE_VARIABLES"   Request an array of variables the server will report.
    ///		"REPORTED_VARIABLES"     Request an array of variables currently being reported.
    ///		"SENDABLE_VARIABLES"     Request an array of variables the server will send.
    /// </summary>
    /// <param name="telnet">Telnet Interpreter to answer on.</param>
    /// <param name="requested">The list, or lists, the client asked for.</param>
    private async ValueTask HandleListRequestAsync(TelnetInterpreter telnet, JsonNode? requested)
    {
        foreach (var group in Arguments(requested))
        {
            if (!Data.Lists.TryGetValue(group, out var contents))
            {
                _logger.LogDebug("Ignoring a LIST for {Group}, which this server does not offer.", group);
                continue;
            }

            await SendVariablesAsync(telnet, [new(group, contents())]);
        }
    }

    /// <summary>
    /// The SEND command can be used by either side, but should typically be used by the client.
    /// After the client has received a list of variables, or otherwise knows which variables exist,
    /// it can request the server to send those variables and their values with the SEND command.
    /// The value of the SEND command should be a list of variables the client wants returned.
    /// </summary>
    /// <remarks>
    /// Everything asked for at once is answered in one subnegotiation, as the specification's own
    /// example does. A variable this server does not offer is left out rather than answered with
    /// something invented: MSDP has no spelling for "no such variable", and a made-up value would be
    /// read as a real one.
    /// </remarks>
    /// <param name="telnet">Telnet interpreter to answer on.</param>
    /// <param name="requested">The variable, or variables, the client asked for.</param>
    private async ValueTask HandleSendRequestAsync(TelnetInterpreter telnet, JsonNode? requested) =>
        await SendVariablesAsync(telnet, CurrentValues(Data.Sendable_Variables, Arguments(requested), "SEND"));

    /// <summary>
    /// Answers a REPORT by sending the variables now, and registers them so that every later
    /// <see cref="MSDPServerModel.NotifyChangeAsync"/> sends them again — "the server should send the
    /// requested variables to the client, and re-send each individual variable whenever it changes".
    /// </summary>
    /// <param name="telnet">Telnet interpreter to answer on.</param>
    /// <param name="requested">The variable, or variables, the client wants reported.</param>
    private async ValueTask HandleReportRequestAsync(TelnetInterpreter telnet, JsonNode? requested)
    {
        var reportable = new List<KeyValuePair<string, object?>>();

        foreach (var variable in Arguments(requested))
        {
            if (!Data.Reportable_Variables.TryGetValue(variable, out var value))
            {
                _logger.LogDebug(
                    "Ignoring a REPORT for {Variable}, which this server does not report on.", variable);
                continue;
            }

            reportable.Add(new(variable, value()));

            // The registration closes over the name, not over this value: it re-reads the variable
            // every time it fires, so a change sends what the variable holds then.
            var name = variable;
            Data.Report(name, () => SendVariablesAsync(
                telnet, [new(name, Data.Reportable_Variables.TryGetValue(name, out var current) ? current() : null)]));
        }

        await SendVariablesAsync(telnet, reportable);
    }

    /// <summary>
    /// The UNREPORT command is used to remove the report status of variables after the use of the REPORT command.
    /// </summary>
    /// <param name="requested">The variable, or variables, to stop reporting on.</param>
    private void HandleUnReportRequest(JsonNode? requested)
    {
        foreach (var variable in Arguments(requested))
        {
            Data.UnReport(variable);
        }
    }

    /// <summary>
    /// The RESET command works like the LIST command, and can be used to reset groups of variables to their initial state.
    /// Most commonly RESET will be called with REPORTABLE_VARIABLES or REPORTED_VARIABLES as the argument,
    /// though any LIST option can be used.
    /// </summary>
    /// <remarks>
    /// The initial state of REPORTED_VARIABLES is that nothing is being reported, so that group is
    /// cleared here as well as handed to the consumer; every other group's initial state is the
    /// game's to decide.
    /// </remarks>
    /// <param name="requested">The group, or groups, to reset.</param>
    private async ValueTask HandleResetRequestAsync(JsonNode? requested)
    {
        foreach (var group in Arguments(requested))
        {
            if (group == ReportedVariables)
            {
                Data.UnReportAll();
            }

            await Data.ResetAsync(group);
        }
    }

    /// <summary>
    /// A variable that is not one of the five commands is the client setting one of the variables
    /// this server advertised as configurable.
    /// </summary>
    private async ValueTask HandleConfigurationAsync(string variable, JsonNode? value)
    {
        if (!Data.Configurable_Variables().Contains(variable))
        {
            _logger.LogDebug(
                "Ignoring {Variable}, which is neither an MSDP command nor a variable this server offers to configure.",
                variable);
            return;
        }

        if (Data.SetCallbackAsync is null)
        {
            _logger.LogWarning(
                "{Variable} is advertised as configurable but the model has no SetCallbackAsync, so the client's value is dropped.",
                variable);
            return;
        }

        foreach (var setting in Arguments(value))
        {
            await Data.SetCallbackAsync(variable, setting);
        }
    }

    /// <summary>
    /// Writes one subnegotiation carrying the given variables and their values. Sends nothing when
    /// there is nothing to say.
    /// </summary>
    private async ValueTask SendVariablesAsync(
        TelnetInterpreter telnet, IReadOnlyList<KeyValuePair<string, object?>> variables)
    {
        if (variables.Count == 0)
        {
            return;
        }

        var payload = new JsonObject();

        foreach (var variable in variables)
        {
            if (TryConvert(variable.Key, variable.Value, out var value))
            {
                payload[variable.Key] = value;
            }
        }

        if (payload.Count == 0)
        {
            return;
        }

        await telnet.SendMSDPPayloadAsync(MSDPLibrary.ReportVariables(payload, telnet.CurrentEncoding));
    }

    /// <summary>
    /// Turns a variable's value into the JSON that becomes its MSDP value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MSDP has three shapes — a table, an array, and text — so that is all this has to decide:
    /// anything with named members is a table, anything you can enumerate is an array, and everything
    /// else is its text. None of that reflects over a type, which is what keeps a server that uses
    /// this handler compilable ahead of time.
    /// </para>
    /// <para>
    /// A type of your own is a fourth case, and needs its contract: either return a
    /// <see cref="JsonNode"/> you built with one (<c>JsonSerializer.SerializeToNode(room,
    /// MyContext.Default.Room)</c>), or set <see cref="MSDPServerModel.SerializerOptions"/> to a
    /// source-generated context and return the object itself. Without either there is no
    /// trim-safe way to read its properties, so the variable is dropped with an error rather than
    /// sent as its type name.
    /// </para>
    /// </remarks>
    private bool TryConvert(string variable, object? value, out JsonNode? node)
    {
        switch (value)
        {
            case null:
                node = null;
                return true;
            // A node already belongs to whatever built it, so it is copied rather than re-parented.
            case JsonNode json:
                node = json.DeepClone();
                return true;
            case string text:
                node = JsonValue.Create(text);
                return true;
            // MSDP spells a boolean 1 or 0, which the JSON writer already knows.
            case bool flag:
                node = JsonValue.Create(flag);
                return true;
            case IDictionary table:
                node = ToTable(variable, table);
                return true;
            case IEnumerable items:
                node = ToArray(variable, items);
                return true;
            case IConvertible convertible:
                node = JsonValue.Create(convertible.ToString(CultureInfo.InvariantCulture));
                return true;
            default:
                return TryConvertWithContract(variable, value, out node);
        }
    }

    private JsonObject ToTable(string variable, IDictionary table)
    {
        var result = new JsonObject();

        foreach (DictionaryEntry entry in table)
        {
            var key = entry.Key as string ?? entry.Key?.ToString();

            if (key is null)
            {
                _logger.LogDebug("Dropping an entry of {Variable} whose key is not text.", variable);
                continue;
            }

            if (TryConvert(variable, entry.Value, out var value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private JsonArray ToArray(string variable, IEnumerable items)
    {
        var result = new JsonArray();

        foreach (var item in items)
        {
            if (TryConvert(variable, item, out var value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    /// <summary>
    /// The last resort for a type of the consumer's own: their serializer contract, if they gave one.
    /// </summary>
    private bool TryConvertWithContract(string variable, object value, out JsonNode? node)
    {
        node = null;

        if (Data.SerializerOptions is null)
        {
            _logger.LogError(
                "Cannot send {Variable}: {Type} is not text, a collection or a JsonNode, and MSDPServerModel.SerializerOptions is not set to a serializer context that describes it.",
                variable, value.GetType());
            return false;
        }

        try
        {
            // GetTypeInfo asks the resolver the consumer configured. With a source-generated context
            // that is a lookup, not reflection; with a reflection-based one it is their choice, made
            // in their own code.
            node = JsonSerializer.SerializeToNode(value, Data.SerializerOptions.GetTypeInfo(value.GetType()));
            return true;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogError(ex,
                "Cannot send {Variable}: the configured serializer context has no contract for {Type}.",
                variable, value.GetType());
            return false;
        }
    }

    /// <summary>
    /// Reads the current value of each named variable, dropping the ones this server does not offer.
    /// </summary>
    private List<KeyValuePair<string, object?>> CurrentValues(
        Dictionary<string, Func<object?>> variables, IEnumerable<string> names, string command)
    {
        var values = new List<KeyValuePair<string, object?>>();

        foreach (var name in names)
        {
            if (!variables.TryGetValue(name, out var value))
            {
                _logger.LogDebug(
                    "Ignoring a {Command} for {Variable}, which this server does not offer.", command, name);
                continue;
            }

            values.Add(new(name, value()));
        }

        return values;
    }

    /// <summary>
    /// The names a command was given. MSDP writes one argument as a value and several either as an
    /// array or as repeated values, so both arrive here — as a string, or as a list of them.
    /// </summary>
    private IEnumerable<string> Arguments(JsonNode? argument)
    {
        switch (argument)
        {
            case JsonValue value:
                yield return value.ToString();
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonValue element)
                    {
                        yield return element.ToString();
                    }
                    else
                    {
                        _logger.LogDebug("Ignoring an MSDP argument that is not a variable name: {Argument}", item);
                    }
                }

                break;
            default:
                _logger.LogDebug("Ignoring an MSDP argument that names nothing: {Argument}", argument);
                break;
        }
    }
}
