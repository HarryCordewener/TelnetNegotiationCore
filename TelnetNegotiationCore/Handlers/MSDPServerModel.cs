using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Handlers;

/// <summary>
/// https://tintin.mudhalla.net/protocols/msdp/
/// Reportable MSDP Variables
/// These variables are mere suggestions for MUDs wanting to implement MSDP.By using these reportable variables it'll be easier to create MSDP scripts that need little or no modification to work across MUDs. If you create your own set of variables in addition to these, it's suggested to create an extended online specification that describes your variables and their behavior.The SPECIFICATION variable can be used to link people to the web page.
/// 
/// General
/// 		"ACCOUNT_NAME"         Name of the player account.
/// 		"CHARACTER_NAME"       Name of the player character.
/// 		"SERVER_ID"            Name of the MUD, or an otherwise unique ID.
/// 		"SERVER_TIME"          The time on the server using either military or civilian time.
/// 		"SPECIFICATION"        URL to the MUD's online MSDP specification, if any.
/// 
/// 	Character
/// 		"AFFECTS"              Current affects in array format.
/// 		"ALIGNMENT"            Current alignment.
/// 		"EXPERIENCE"           Current total experience points. Use 0-100 for percentages.
/// 		"EXPERIENCE_MAX"       Current maximum experience points. Use 100 for percentages.
/// 		"EXPERIENCE_TNL"       Current total experience points Till Next Level.Use 0-100 for percentages.
/// 		"EXPERIENCE_TNL_MAX"   Current maximum experience points Till Next Level.Use 100 for percentages.
/// 		"HEALTH"               Current health points.
/// 		"HEALTH_MAX"           Current maximum health points.
/// 		"LEVEL"                Current level.
/// 		"MANA"                 Current mana points.
/// 		"MANA_MAX"             Current maximum mana points.
/// 		"MONEY"                Current amount of money.
/// 		"MOVEMENT"             Current movement points.
/// 		"MOVEMENT_MAX"         Current maximum movement points.
/// 
/// 	Combat
/// 		"OPPONENT_LEVEL"       Level of opponent.
/// 		"OPPONENT_HEALTH"      Current health points of opponent.Use 0-100 for percentages.
/// 		"OPPONENT_HEALTH_MAX"  Current maximum health points of opponent. Use 100 for percentages.
/// 		"OPPONENT_NAME"        Name of opponent.
/// 		"OPPONENT_STRENGTH"    Relative strength of opponent, like the consider mud command.
/// 	Mapping
/// 		Indentation indicates the variable is nested within the parent variable using a table.
/// 		"ROOM"
/// 			"VNUM"               A number uniquely identifying the room.
/// 			"NAME"               The name of the room.
/// 			"AREA"               The area the room is in.
/// 			"COORDS"
/// 				"X"                The X coordinate of the room.
/// 				"Y"                The Y coordinate of the room.
/// 				"Z"                The Z coordinate of the room.
/// 			"TERRAIN"            The terrain type of the room. Forest, Ocean, etc.
/// 			"EXITS"              Nested abbreviated exit directions (n, e, w, etc) and corresponding destination VNUMs.
/// 
/// 	World
/// 		"WORLD_TIME"           The in game time on the MUD using either military or civilian time.
/// 
/// 	Configurable MSDP Variables
/// 	Configurable variables are variables on the server that can be altered by the client. Implementing configurable variable support is optional.
/// 	General
/// 		"CLIENT_NAME"          Name of the MUD client.
/// 		"CLIENT_VERSION"       Version of the MUD client.
/// 		"PLUGIN_ID"            Unique ID of the MSDP plugin/script.
/// </summary>
public class MSDPServerModel
{
    private readonly ConcurrentDictionary<string, Func<ValueTask>> _reportedVariables = new(StringComparer.Ordinal);

    /// <summary>
    /// The lists a client can ask for by name with <c>LIST</c>, and what each one contains.
    /// </summary>
    /// <remarks>
    /// Each entry reads the property below it when the client asks, rather than capturing whatever
    /// was set while the constructor ran — a model is configured through an object initialiser,
    /// which runs afterwards.
    /// </remarks>
    public Dictionary<string, Func<HashSet<string>>> Lists { get; private init; }

    /// <summary>
    /// The commands this server understands, for <c>LIST COMMANDS</c>.
    /// </summary>
    public Func<HashSet<string>> Commands { get; set; } = () => [];

    /// <summary>
    /// The variables a client may set, for <c>LIST CONFIGURABLE_VARIABLES</c>. A client setting one
    /// of these reaches <see cref="SetCallbackAsync"/>; a variable that is not in this list is not
    /// configurable and is ignored.
    /// </summary>
    public Func<HashSet<string>> Configurable_Variables { get; set; } = () => [];

    /// <summary>
    /// The variables this server will report on change, and how to read each one's current value.
    /// </summary>
    /// <remarks>
    /// A <c>REPORT</c> is answered from here immediately and again on every
    /// <see cref="NotifyChangeAsync"/>, and the keys are what <c>LIST REPORTABLE_VARIABLES</c>
    /// answers with — so the list a client is offered and the values it is then sent cannot
    /// disagree.
    /// </remarks>
    public Dictionary<string, Func<object?>> Reportable_Variables { get; set; } = [];

    /// <summary>
    /// The variables this server will send on request, and how to read each one's current value.
    /// A value may be any object: a string, a number, a collection (an MSDP array) or an object
    /// (an MSDP table).
    /// </summary>
    public Dictionary<string, Func<object?>> Sendable_Variables { get; set; } = [];

    /// <summary>
    /// The variables currently being reported, for <c>LIST REPORTED_VARIABLES</c>.
    /// </summary>
    public IReadOnlyCollection<string> Reported_Variables => _reportedVariables.Keys.ToArray();

    /// <summary>
    /// How to read a variable whose value is a type of your own — a source-generated
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>'s
    /// <c>Default.Options</c>, typically.
    /// </summary>
    /// <remarks>
    /// Only needed for that case. Text, numbers, booleans, dictionaries, collections and
    /// <see cref="System.Text.Json.Nodes.JsonNode"/>s are understood without it, because MSDP has
    /// only three shapes — table, array and text — and none of them needs a type read to recognise.
    /// Leaving this null keeps a server free of reflection, and so compilable ahead of time; a
    /// variable whose value needs a contract that is not here is dropped with an error rather than
    /// sent wrong.
    /// </remarks>
    public JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>
    /// Called when a client asks for a group of variables to be reset to its initial state; only the
    /// game knows what that state is.
    /// </summary>
    public Func<string, ValueTask> ResetCallbackAsync { get; }

    /// <summary>
    /// Called when a client sets one of the <see cref="Configurable_Variables"/>, with the variable
    /// and the value it sent. Optional: implementing configurable variables is optional in MSDP.
    /// </summary>
    public Func<string, string, ValueTask>? SetCallbackAsync { get; set; }

    /// <summary>
    /// Creates the MSDP Server Model. 
    /// Define each public variable to implement MSDP.
    /// </summary>
    /// <param name="resetCallback">Function to call when a client wishes to reset a group of variables.</param>
    public MSDPServerModel(Func<string, ValueTask> resetCallback)
    {
        var lists = new Dictionary<string, Func<HashSet<string>>>
        {
            { "COMMANDS", () => Commands() },
            { "CONFIGURABLE_VARIABLES", () => Configurable_Variables() },
            { "REPORTABLE_VARIABLES", () => [.. Reportable_Variables.Keys] },
            { "REPORTED_VARIABLES", () => [.. Reported_Variables] },
            { "SENDABLE_VARIABLES", () => [.. Sendable_Variables.Keys] }
        };

        // "LISTS - Request an array of lists supported by the server", which is these and itself.
        lists.Add("LISTS", () => [.. lists.Keys]);

        Lists = lists;

        ResetCallbackAsync = resetCallback;
    }

    /// <summary>
    /// Asks the consumer to reset a group of variables to its initial state.
    /// </summary>
    public ValueTask ResetAsync(string group) => ResetCallbackAsync(group);

    /// <summary>
    /// Starts reporting a variable. Called by <see cref="MSDPServerHandler"/> when a client asks;
    /// <paramref name="onChange"/> is what sends the variable's current value to that client.
    /// </summary>
    public void Report(string reportableVariable, Func<ValueTask> onChange) =>
        _reportedVariables[reportableVariable] = onChange;

    /// <summary>
    /// Stops reporting a variable.
    /// </summary>
    public void UnReport(string reportableVariable) =>
        _reportedVariables.TryRemove(reportableVariable, out _);

    /// <summary>
    /// Stops reporting every variable — the initial state of <c>REPORTED_VARIABLES</c>.
    /// </summary>
    public void UnReportAll() => _reportedVariables.Clear();

    /// <summary>
    /// Tells the client that a reported variable has changed, sending its current value. Call this
    /// from the game whenever a value behind <see cref="Reportable_Variables"/> changes.
    /// </summary>
    /// <remarks>
    /// The value is read from <see cref="Reportable_Variables"/> at this point rather than passed in,
    /// so what the client is told and what the server would answer a <c>SEND</c> with cannot differ.
    /// A variable nobody asked to have reported is not sent.
    /// </remarks>
    public ValueTask NotifyChangeAsync(string reportableVariable) =>
        _reportedVariables.TryGetValue(reportableVariable, out var onChange)
            ? onChange()
            : default;
}
