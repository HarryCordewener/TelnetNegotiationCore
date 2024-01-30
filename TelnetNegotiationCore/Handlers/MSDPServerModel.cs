using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.Handlers
{
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
		/// <summary>
		/// What lists we can report on.
		/// </summary>
		public Dictionary<string, Func<HashSet<string>>> Lists { get; private init; }

		public Func<HashSet<string>> Commands { get; set; } = () => [];

		public Func<HashSet<string>> Configurable_Variables { get; set; } = () => [];

		public Func<HashSet<string>> Reportable_Variables { get; set; } = () => [];

		public Dictionary<string, Func<string, Task>> Reported_Variables => [];

		public Func<HashSet<string>> Sendable_Variables { get; set; } = () => [];

		public Func<string, Task> ResetCallbackAsync { get; }

		/// <summary>
		/// Creates the MSDP Server Model. 
		/// Define each public variable to implement MSDP.
		/// </summary>
		/// <param name="setCallback">Function to call when a client wishes to set a server variable.</param>
		public MSDPServerModel(Func<string,Task> resetCallback)
		{
			Lists = new()
			{
				{ "COMMANDS", Commands},
				{ "CONFIGURABLE_VARIABLES", Configurable_Variables},
				{ "REPORTABLE_VARIABLES", Reportable_Variables},
				{ "REPORTED_VARIABLES", () => Reported_Variables.Select( x=> x.Key).ToHashSet() },
				{ "SENDABLE_VARIABLES", Sendable_Variables}
			};
			
			ResetCallbackAsync = resetCallback;
		}

		public async Task ResetAsync(string configurableVariable) =>
				await ResetCallbackAsync(configurableVariable);

		public void Report(string reportableVariable, Func<string, Task> function) =>
			Reported_Variables.Add(reportableVariable, function);

		public void UnReport(string reportableVariable) =>
			Reported_Variables.Remove(reportableVariable);

		public async Task NotifyChangeAsync(string reportableVariable, string newValue) =>
			await (Reported_Variables.TryGetValue(reportableVariable, out var function)
				? function(newValue)
				: Task.CompletedTask);
	}
}
