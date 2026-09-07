using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Handlers;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.TestServer
{
	public class KestrelMockServer(ILogger<KestrelMockServer> logger, ITelnetInterpreterFactory telnetFactory) : ConnectionHandler
	{
		public ValueTask SignalGMCPAsync((string module, string writeback) val)
		{
			logger.LogDebug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback);
			return ValueTask.CompletedTask;
		}

		public ValueTask SignalMSSPAsync(MSSPConfig val)
		{
			logger.LogDebug("New MSSP: {@MSSPConfig}", val);
			return ValueTask.CompletedTask;
		}

		public ValueTask SignalNAWSAsync(int height, int width)
		{
			logger.LogDebug("Client Height and Width updated: {Height}x{Width}", height, width);
			return ValueTask.CompletedTask;
		}

		private static async ValueTask SignalMSDPAsync(MSDPServerHandler handler, TelnetInterpreter telnet, string config) =>
			await handler.HandleAsync(telnet, config);

		public static async ValueTask WriteBackAsync(byte[] writeback, Encoding encoding, TelnetInterpreter telnet)
		{
			var str = encoding.GetString(writeback);
			if (str.StartsWith("echo"))
			{
				await telnet.SendAsync(encoding.GetBytes($"We heard: {str}"));
			}
			Console.WriteLine(encoding.GetString(writeback));
		}

		/// <summary>
		/// The value behind the ROOM variable, shaped like the table in the MSDP specification. A real
		/// game would read this off the player's current room; the point here is that SEND and REPORT
		/// answer with a value, and that a value with named members is sent as a table.
		/// </summary>
		/// <remarks>
		/// A type of the game's own, carried by the serializer contract in <see cref="MsdpJsonContext"/>
		/// that the source generator writes at compile time — so this works in a server published with
		/// Native AOT, where reflecting over <see cref="Room"/> would not.
		/// </remarks>
		private static Room CurrentRoom() => new()
		{
			Vnum = 6008,
			Name = "The forest clearing",
			Area = "Haon Dor",
			Terrain = "forest",
			Exits = new Dictionary<string, string> { { "n", "6011" }, { "e", "6007" } }
		};

		private async ValueTask MSDPUpdateBehavior(string resetVariable)
		{
			logger.LogDebug("MSDP Reset Request: {@Reset}", resetVariable);
			await ValueTask.CompletedTask;
		}

		public override async Task OnConnectedAsync(ConnectionContext connection)
		{
			using (logger.BeginScope(new Dictionary<string, object> { { "ConnectionId", connection.ConnectionId } }))
			{
				logger.LogInformation("{ConnectionId} connected", connection.ConnectionId);

				var msdpHandler = new MSDPServerHandler(new MSDPServerModel(MSDPUpdateBehavior)
				{
					Commands = () => ["help", "stats", "info"],
					Configurable_Variables = () => ["CLIENT_NAME", "CLIENT_VERSION", "PLUGIN_ID"],
					SerializerOptions = MsdpJsonContext.Default.Options,
					Reportable_Variables = new() { ["ROOM"] = () => CurrentRoom() },
					Sendable_Variables = new() { ["ROOM"] = () => CurrentRoom() },
				});

				var (telnet, readTask) = await telnetFactory.CreateBuilder()
				.OnSubmit(WriteBackAsync)
				.AddPlugin<NAWSProtocol>()
					.OnNAWS(SignalNAWSAsync)
				.AddPlugin<GMCPProtocol>()
					.OnGMCPMessage(SignalGMCPAsync)
				.AddPlugin<MSDPProtocol>()
					.OnMSDPMessage((t, config) => SignalMSDPAsync(msdpHandler, t, config))
				.AddPlugin<MSSPProtocol>()
					.OnMSSP(SignalMSSPAsync)
					.WithMSSPConfig(() => new MSSPConfig
					{
						Name = "My Telnet Negotiated Server",
						UTF_8 = true,
						Gameplay = ["ABC", "DEF"],
						Extended = new Dictionary<string, object>
						{
							{ "Foo",  "Bar"},
							{ "Baz", (string[])["Moo", "Meow"] }
						}
					})
				.AddPlugin<TerminalTypeProtocol>()
				.AddPlugin<CharsetProtocol>()
					.WithCharsetOrder(Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1"))
				.AddPlugin<EORProtocol>()
				.AddPlugin<SuppressGoAheadProtocol>()
				.BuildAndStartAsync(connection.Transport);

				await readTask;

				logger.LogInformation("{ConnectionId} disconnected", connection.ConnectionId);
			}
		}
	}
}
