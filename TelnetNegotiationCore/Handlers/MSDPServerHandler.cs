using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TelnetNegotiationCore.Functional;
using TelnetNegotiationCore.Interpreters;

namespace TelnetNegotiationCore.Handlers
{
	/*
	 * https://tintin.mudhalla.net/protocols/msdp/
	  Reportable MSDP Variables
		These variables are mere suggestions for MUDs wanting to implement MSDP. By using these reportable variables it'll be easier to create MSDP scripts that need little or no modification to work across MUDs. If you create your own set of variables in addition to these, it's suggested to create an extended online specification that describes your variables and their behavior. The SPECIFICATION variable can be used to link people to the web page.

		General
			"ACCOUNT_NAME"         Name of the player account.
			"CHARACTER_NAME"       Name of the player character.
			"SERVER_ID"            Name of the MUD, or an otherwise unique ID.
			"SERVER_TIME"          The time on the server using either military or civilian time.
			"SPECIFICATION"        URL to the MUD's online MSDP specification, if any.

		Character
			"AFFECTS"              Current affects in array format.
			"ALIGNMENT"            Current alignment.
			"EXPERIENCE"           Current total experience points. Use 0-100 for percentages.
			"EXPERIENCE_MAX"       Current maximum experience points. Use 100 for percentages.
			"EXPERIENCE_TNL"       Current total experience points Till Next Level. Use 0-100 for percentages.
			"EXPERIENCE_TNL_MAX"   Current maximum experience points Till Next Level. Use 100 for percentages.
			"HEALTH"               Current health points.
			"HEALTH_MAX"           Current maximum health points.
			"LEVEL"                Current level.
			"MANA"                 Current mana points.
			"MANA_MAX"             Current maximum mana points.
			"MONEY"                Current amount of money.
			"MOVEMENT"             Current movement points.
			"MOVEMENT_MAX"         Current maximum movement points.

		Combat
			"OPPONENT_LEVEL"       Level of opponent.
			"OPPONENT_HEALTH"      Current health points of opponent. Use 0-100 for percentages.
			"OPPONENT_HEALTH_MAX"  Current maximum health points of opponent. Use 100 for percentages.
			"OPPONENT_NAME"        Name of opponent.
			"OPPONENT_STRENGTH"    Relative strength of opponent, like the consider mud command.
		Mapping
			Indentation indicates the variable is nested within the parent variable using a table.
			"ROOM"
				"VNUM"               A number uniquely identifying the room.
				"NAME"               The name of the room.
				"AREA"               The area the room is in.
				"COORDS"
					"X"                The X coordinate of the room.
					"Y"                The Y coordinate of the room.
					"Z"                The Z coordinate of the room.
				"TERRAIN"            The terrain type of the room. Forest, Ocean, etc.
				"EXITS"              Nested abbreviated exit directions (n, e, w, etc) and corresponding destination VNUMs.

		World
			"WORLD_TIME"           The in game time on the MUD using either military or civilian time.

		Configurable MSDP Variables
		Configurable variables are variables on the server that can be altered by the client. Implementing configurable variable support is optional.
		General
			"CLIENT_NAME"          Name of the MUD client.
			"CLIENT_VERSION"       Version of the MUD client.
			"PLUGIN_ID"            Unique ID of the MSDP plugin/script.
	*/

	/// <summary>
	/// A simple handler for MSDP that creates a workflow for responding with MSDP information.
	/// </summary>
	/// <remarks>
	/// We need a way to Observe changes to the functions to properly support the REPORT action. 
	/// This is not currently implemented.
	/// </remarks>
	public class MSDPServerHandler(Dictionary<string, Func<string>> resolvers)
	{
		public async Task HandleAsync(TelnetInterpreter telnet, string clientJson)
		{
			var json = JsonSerializer.Deserialize<dynamic>(clientJson);

			if (json.LIST != null)
			{
				await HandleListRequestAsync(telnet, (string)json.LIST);
			}
			else if (json.REPORT != null)
			{
				await HandleReportRequestAsync(telnet, (string)json.REPORT);
			}
			else if (json.RESET != null)
			{
				await HandleResetRequestAsync((string)json.RESET);
			}
			else if (json.SEND != null)
			{
				await HandleSendRequestAsync(telnet, (string)json.SEND);
			}
			else if (json.UNREPORT != null)
			{
				await HandleUnReportRequestAsync((string)json.UNREPORT);
			}

			await Task.CompletedTask;
		}

		/// <summary>
		/// Handles a request to LIST a single item. For safety, also supports a list of lists.
		/// Should at least support:
		///		"COMMANDS"               Request an array of commands supported by the server.
		///		"LISTS"                  Request an array of lists supported by the server.
		///		"CONFIGURABLE_VARIABLES" Request an array of variables the client can configure.
		///		"REPORTABLE_VARIABLES"   Request an array of variables the server will report.
		///		"REPORTED_VARIABLES"     Request an array of variables currently being reported.
		///		"SENDABLE_VARIABLES"     Request an array of variables the server will send.
		/// </summary>
		/// <param name="telnet">Telnet Interpreter to callback with.</param>
		/// <param name="item">The item to report</param>
		private async Task HandleListRequestAsync(TelnetInterpreter telnet, string item)
		{
			// TODO: Do something to check if ITEM is an Array or a single String here.
			var found = resolvers.TryGetValue($"LIST.{item}", out var list);
			var jsonString = $"{{{item}:{(found ? list : "null")}}}";
			await telnet.CallbackNegotiationAsync(MSDPLibrary.Report(jsonString, telnet.CurrentEncoding));
		}

		private async Task HandleReportRequestAsync(TelnetInterpreter telnet, string item)
		{
			// TODO: Do something to check if ITEM is an Array or a single String here.
			// TODO: Implement a Reporting system.
			await HandleSendRequestAsync(telnet, item);
		}

		/// <summary>
		/// The RESET command works like the LIST command, and can be used to reset groups of variables to their initial state.
		/// Most commonly RESET will be called with REPORTABLE_VARIABLES or REPORTED_VARIABLES as the argument, 
		/// though any LIST option can be used.
		/// </summary>
		/// <param name="item">Item to reset</param>
		private async Task HandleResetRequestAsync(string item)
		{
			// TODO: Do something to check if ITEM is an Array or a single String here.
			// TODO: Reset it?
			// TODO: Enable resetting other items we can LIST?
			var found = resolvers.TryGetValue($"REPORTABLE_VARIABLES.{item}", out var list);
			await Task.CompletedTask;
		}

		/// <summary>
		/// The SEND command can be used by either side, but should typically be used by the client. 
		/// After the client has received a list of variables, or otherwise knows which variables exist, 
		/// it can request the server to send those variables and their values with the SEND command. 
		/// The value of the SEND command should be a list of variables the client wants returned.
		/// </summary>
		/// <param name="telnet">Telnet interpreter to send back negotiation with</param>
		/// <param name="item">The item to send</param>
		private async Task HandleSendRequestAsync(TelnetInterpreter telnet, string item)
		{
			// TODO: Do something to check if ITEM is an Array or a single String here.
			var found = resolvers.TryGetValue($"REPORTABLE_VARIABLES.{item}", out var list);
			var jsonString = $"{{{item}:{(found ? list : "null")}}}";
			await telnet.CallbackNegotiationAsync(MSDPLibrary.Report(jsonString, telnet.CurrentEncoding));
		}

		/// <summary>
		/// The UNREPORT command is used to remove the report status of variables after the use of the REPORT command.
		/// </summary>
		/// <param name="item">The item to stop reporting on</param>
		private async Task HandleUnReportRequestAsync(string item)
		{
			var found = resolvers.TryGetValue($"REPORTED_VARIABLES.{item}", out var list);
			// TODO: Remove them from the list of variables being reported.
			// TODO: Do something to check if ITEM is an Array or a single String here.
			await Task.CompletedTask;
		}
	}
}
