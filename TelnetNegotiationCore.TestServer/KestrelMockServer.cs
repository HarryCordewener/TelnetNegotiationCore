using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Handlers;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.TestServer
{
	public class KestrelMockServer : ConnectionHandler
	{
		private readonly ILogger _logger;

		public KestrelMockServer(ILogger<KestrelMockServer> logger)
		{
			Console.OutputEncoding = Encoding.UTF8;
			_logger = logger;
		}

		private async ValueTask WriteToOutputStreamAsync(byte[] arg, PipeWriter writer)
		{
			try
			{
				await writer.WriteAsync(new ReadOnlyMemory<byte>(arg), CancellationToken.None);
			}
			catch (ObjectDisposedException ode)
			{
				_logger.LogError(ode, "Stream has been closed");
			}
		}

		public ValueTask SignalGMCPAsync((string module, string writeback) val)
		{
			_logger.LogDebug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback);
			return ValueTask.CompletedTask;
		}

		public ValueTask SignalMSSPAsync(MSSPConfig val)
		{
			_logger.LogDebug("New MSSP: {@MSSPConfig}", val);
			return ValueTask.CompletedTask;
		}

		public ValueTask SignalNAWSAsync(int height, int width)
		{
			_logger.LogDebug("Client Height and Width updated: {Height}x{Width}", height, width);
			return ValueTask.CompletedTask;
		}

		private static async ValueTask SignalMSDPAsync(MSDPServerHandler handler, TelnetInterpreter telnet, string config) =>
			await handler.HandleAsync(telnet, config);

		public static async ValueTask WriteBackAsync(byte[] writeback, Encoding encoding, TelnetInterpreter telnet)
		{
			var str = encoding.GetString(writeback);
			if (str.StartsWith("echo"))
			{
				await telnet.SendAsync(encoding.GetBytes($"We heard: {str}" + Environment.NewLine));
			}
			Console.WriteLine(encoding.GetString(writeback));
		}

		private async ValueTask MSDPUpdateBehavior(string resetVariable)
		{
			_logger.LogDebug("MSDP Reset Request: {@Reset}", resetVariable);
			await ValueTask.CompletedTask;
		}

		public override async Task OnConnectedAsync(ConnectionContext connection)
		{
			using (_logger.BeginScope(new Dictionary<string, object> { { "ConnectionId", connection.ConnectionId } }))
			{
				_logger.LogInformation("{ConnectionId} connected", connection.ConnectionId);

				var msdpHandler = new MSDPServerHandler(new MSDPServerModel(MSDPUpdateBehavior)
				{
					Commands = () => ["help", "stats", "info"],
					Configurable_Variables = () => ["CLIENT_NAME", "CLIENT_VERSION", "PLUGIN_ID"],
					Reportable_Variables = () => ["ROOM"],
					Sendable_Variables = () => ["ROOM"],
				});

				var telnet = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(_logger)
				.OnSubmit(WriteBackAsync)
				.OnNegotiation(x => WriteToOutputStreamAsync(x, connection.Transport.Output))
				.AddDefaultMUDProtocols()
				.BuildAsync();

			// Subscribe to protocol callbacks
			var gmcp = telnet.PluginManager!.GetPlugin<GMCPProtocol>();
			if (gmcp != null)
				gmcp.OnGMCPReceived = SignalGMCPAsync;

			var mssp = telnet.PluginManager!.GetPlugin<MSSPProtocol>();
			if (mssp != null)
			{
				mssp.OnMSSPRequest = SignalMSSPAsync;
				mssp.SetMSSPConfig(() => new MSSPConfig
				{
					Name = "My Telnet Negotiated Server",
					UTF_8 = true,
					Gameplay = ["ABC", "DEF"],
					Extended = new Dictionary<string, dynamic>
					{
						{ "Foo",  "Bar"},
						{ "Baz", (string[])["Moo", "Meow"] }
					}
				});
			}

			var naws = telnet.PluginManager!.GetPlugin<NAWSProtocol>();
			if (naws != null)
				naws.OnNAWSNegotiated = SignalNAWSAsync;

			var msdp = telnet.PluginManager!.GetPlugin<MSDPProtocol>();
			if (msdp != null)
				msdp.OnMSDPReceived = (t, config) => SignalMSDPAsync(msdpHandler, t, config);

			var charset = telnet.PluginManager!.GetPlugin<CharsetProtocol>();
			if (charset != null)
				charset.CharsetOrder = [Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1")];

			while (true)
			{
					var result = await connection.Transport.Input.ReadAsync();
					var buffer = result.Buffer;

					foreach (var segment in buffer)
					{
						await telnet.InterpretByteArrayAsync(segment);
					}

					if (result.IsCompleted)
					{
						break;
					}

					connection.Transport.Input.AdvanceTo(buffer.End);
				}
				_logger.LogInformation("{ConnectionId} disconnected", connection.ConnectionId);
			}
		}
	}
}
