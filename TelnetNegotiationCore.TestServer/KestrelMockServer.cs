using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Handlers;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Connections;
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

		/// <summary>
		/// The builder for one connection, shared by the TCP listener and the WebSocket endpoint so both get the same
		/// plugins. The negotiation callback is left unset: each transport wires its own.
		/// </summary>
		public TelnetInterpreterBuilder CreateBuilder()
		{
			var msdpHandler = new MSDPServerHandler(new MSDPServerModel(MSDPUpdateBehavior)
			{
				Commands = () => ["help", "stats", "info"],
				Configurable_Variables = () => ["CLIENT_NAME", "CLIENT_VERSION", "PLUGIN_ID"],
				SerializerOptions = MsdpJsonContext.Default.Options,
				Reportable_Variables = new() { ["ROOM"] = () => CurrentRoom() },
				Sendable_Variables = new() { ["ROOM"] = () => CurrentRoom() },
			});

			return telnetFactory.CreateBuilder()
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
					WebSocket = "ws://localhost:4203/ws",
					WebSocketSubprotocols = "telnet.mudstandards.org,telnet,telnetb64.mudstandards.org,gmcp.mudstandards.org,terminal.mudstandards.org",
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
			.AddPlugin<SuppressGoAheadProtocol>();
		}

		/// <summary>
		/// One connection's session, whatever carries it: the TCP listener and the WebSocket endpoint both run it.
		/// </summary>
		public ITelnetSession CreateSession(string remote) => new MockSession(this, logger, remote);

		public override Task OnConnectedAsync(ConnectionContext connection) =>
			TelnetConnection.RunAsync(
				CreateSession(connection.RemoteEndPoint?.ToString() ?? connection.ConnectionId),
				// Abort rather than complete the pipe: the server is done with the player, so drop at once.
				TelnetTransport.Pipe(connection.Transport, () =>
				{
					connection.Abort();
					return ValueTask.CompletedTask;
				}),
				connection.ConnectionClosed);

		private sealed class MockSession(KestrelMockServer server, ILogger logger, string remote) : ITelnetSession
		{
			private long _received;
			private long _sent;

			public TelnetInterpreterBuilder CreateBuilder() =>
				server.CreateBuilder()
					.OnReceived(bytes =>
					{
						Interlocked.Add(ref _received, bytes.Length);
						return ValueTask.CompletedTask;
					})
					.WrapOutput(write => async bytes =>
					{
						await write(bytes);
						Interlocked.Add(ref _sent, bytes.Length);
					});

			public async Task RunAsync(TelnetConnection connection, CancellationToken cancellationToken)
			{
				using (logger.BeginScope(new Dictionary<string, object> { { "Remote", remote } }))
				{
					logger.LogInformation("{Remote} connected", remote);

					// The interpreter is already reading, so the client's answers arrive while this waits.
					var opening = await connection.Interpreter.WaitForInitialNegotiationAsync(
						TimeSpan.FromMilliseconds(500), cancellationToken);
					logger.LogInformation("{Remote} opening negotiation ended: {End}", remote, opening);

					try
					{
						await connection.Completion;
					}
					finally
					{
						logger.LogInformation("{Remote} disconnected after {Received} bytes in, {Sent} out",
							remote, Interlocked.Read(ref _received), Interlocked.Read(ref _sent));
					}
				}
			}
		}
	}
}
