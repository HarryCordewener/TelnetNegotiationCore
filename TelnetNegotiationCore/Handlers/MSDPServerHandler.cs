﻿using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TelnetNegotiationCore.Functional;
using TelnetNegotiationCore.Interpreters;

namespace TelnetNegotiationCore.Handlers
{
	/// <summary>
	/// A simple handler for MSDP that creates a workflow for responding with MSDP information.
	/// </summary>
	/// <remarks>
	/// We need a way to Observe changes to the functions to properly support the REPORT action. 
	/// This is not currently implemented.
	/// </remarks>
	/// <param name="model">
	/// A dictionary that resolves Variables, commands, and lists. 
	/// </param>
	public class MSDPServerHandler(MSDPServerModel model)
	{
		public MSDPServerModel Data { get; private init; } = model;

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
		private async Task HandleListRequestAsync(TelnetInterpreter telnet, string item) =>
			await ExecuteOnAsync(item, async (val) =>
				await (Data.Lists.TryGetValue(val, out Func<HashSet<string>> value)
					? telnet.CallbackNegotiationAsync(
						MSDPLibrary.Report(JsonSerializer.Serialize(value()),
						telnet.CurrentEncoding))
					: Task.CompletedTask));

		private async Task HandleReportRequestAsync(TelnetInterpreter telnet, string item) =>
			await ExecuteOnAsync(item, async (val) =>
			{
				await HandleSendRequestAsync(telnet, val);
				Data.Report(val, async (newVal) => await HandleSendRequestAsync(telnet, newVal));
			});

		/// <summary>
		/// The RESET command works like the LIST command, and can be used to reset groups of variables to their initial state.
		/// Most commonly RESET will be called with REPORTABLE_VARIABLES or REPORTED_VARIABLES as the argument, 
		/// though any LIST option can be used.
		/// </summary>
		/// <param name="item">Item to reset</param>
		private async Task HandleResetRequestAsync(string item) =>
			await ExecuteOnAsync(item, async (var) =>
			{
				var found = Data.Reportable_Variables().TryGetValue(var, out var list);
				await Task.CompletedTask;
			});

		/// <summary>
		/// The SEND command can be used by either side, but should typically be used by the client. 
		/// After the client has received a list of variables, or otherwise knows which variables exist, 
		/// it can request the server to send those variables and their values with the SEND command. 
		/// The value of the SEND command should be a list of variables the client wants returned.
		/// </summary>
		/// <param name="telnet">Telnet interpreter to send back negotiation with</param>
		/// <param name="item">The item to send</param>
		private async Task HandleSendRequestAsync(TelnetInterpreter telnet, string item) =>
			await ExecuteOnAsync(item, async (var) =>
			{
				var found = Data.Sendable_Variables().TryGetValue(var, out var val);
				var jsonString = $"{{{var}:{(found ? val : "null")}}}";
				await telnet.CallbackNegotiationAsync(MSDPLibrary.Report(jsonString, telnet.CurrentEncoding));
			});

		/// <summary>
		/// The UNREPORT command is used to remove the report status of variables after the use of the REPORT command.
		/// </summary>
		/// <param name="item">The item to stop reporting on</param>
		private async Task HandleUnReportRequestAsync(string item) =>
			await ExecuteOnAsync(item, async (var) =>
			{
				Data.UnReport(var);
				await Task.CompletedTask;
			});

		private async Task ExecuteOnAsync(string item, Func<string, Task> function)
		{
			string[] items;
			if (item.StartsWith('['))
			{
				items = JsonSerializer.Deserialize<string[]>(item);
			}
			else
			{
				items = [item];
			}
			foreach (var val in items)
			{
				await function(val);
			}
		}
	}
}
