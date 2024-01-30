using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using Microsoft.AspNetCore.Connections;
using System.IO.Pipelines;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using TelnetNegotiationCore.Handlers;

namespace TelnetNegotiationCore.TestServer
{
	public class KestrelMockServer : ConnectionHandler
	{
		private readonly ILogger _Logger;

		public KestrelMockServer(ILogger<KestrelMockServer> logger) : base()
		{
			Console.OutputEncoding = Encoding.UTF8;
			_Logger = logger;
		}

		private async Task WriteToOutputStreamAsync(byte[] arg, PipeWriter writer)
		{
			try
			{
				await writer.WriteAsync(new ReadOnlyMemory<byte>(arg), CancellationToken.None);
			}
			catch (ObjectDisposedException ode)
			{
				_Logger.LogError(ode, "Stream has been closed");
			}
		}

		public Task SignalGMCPAsync((string module, string writeback) val)
		{
			_Logger.LogDebug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback);
			return Task.CompletedTask;
		}

		public Task SignalMSSPAsync(MSSPConfig val)
		{
			_Logger.LogDebug("New MSSP: {@MSSPConfig}", val);
			return Task.CompletedTask;
		}

		public Task SignalNAWSAsync(int height, int width)
		{
			_Logger.LogDebug("Client Height and Width updated: {Height}x{Width}", height, width);
			return Task.CompletedTask;
		}

		private static async Task SignalMSDPAsync(MSDPServerHandler handler, TelnetInterpreter telnet, string config) =>
			await handler.HandleAsync(telnet, config);

		public static async Task WriteBackAsync(byte[] writeback, Encoding encoding, TelnetInterpreter telnet)
		{
			var str = encoding.GetString(writeback);
			if (str.StartsWith("echo"))
			{
				await telnet.SendAsync(encoding.GetBytes($"We heard: {str}" + Environment.NewLine));
			}
			Console.WriteLine(encoding.GetString(writeback));
		}

		private async Task MSDPUpdateBehavior(string resetVariable)
		{
			_Logger.LogDebug("MSDP Reset Request: {@Reset}", resetVariable);
			await Task.CompletedTask;
		}

		public async override Task OnConnectedAsync(ConnectionContext connection)
		{
			using (_Logger.BeginScope(new Dictionary<string, object> { { "ConnectionId", connection.ConnectionId } }))
			{
				_Logger.LogInformation("{ConnectionId} connected", connection.ConnectionId);

				var MSDPHandler = new MSDPServerHandler(new MSDPServerModel(MSDPUpdateBehavior)
				{
					Commands = () => ["help", "stats", "info"],
					Configurable_Variables = () => ["CLIENT_NAME", "CLIENT_VERSION", "PLUGIN_ID"],
					Reportable_Variables = () => ["ROOM"],
					Sendable_Variables = () => ["ROOM"],
				});

				var telnet = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, _Logger)
				{
					CallbackOnSubmitAsync = WriteBackAsync,
					SignalOnGMCPAsync = SignalGMCPAsync,
					SignalOnMSSPAsync = SignalMSSPAsync,
					SignalOnNAWSAsync = SignalNAWSAsync,
					SignalOnMSDPAsync = (telnet, config) => SignalMSDPAsync(MSDPHandler, telnet, config),
					CallbackNegotiationAsync = (x) => WriteToOutputStreamAsync(x, connection.Transport.Output),
					CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
				}
					.RegisterMSSPConfig(() => new MSSPConfig
					{
						Name = "My Telnet Negotiated Server",
						UTF_8 = true,
						Gameplay = ["ABC", "DEF"],
						Extended = new Dictionary<string, dynamic>
					{
						{ "Foo",  "Bar"},
						{ "Baz", (string[])["Moo", "Meow"] }
					}
					})
					.BuildAsync();

				while (true)
				{
					var result = await connection.Transport.Input.ReadAsync();
					var buffer = result.Buffer;

					foreach (var segment in buffer)
					{
						await telnet.InterpretByteArrayAsync(segment.Span.ToImmutableArray());
					}

					if (result.IsCompleted)
					{
						break;
					}

					connection.Transport.Input.AdvanceTo(buffer.End);
				}
				_Logger.LogInformation("{ConnectionId} disconnected", connection.ConnectionId);
			}
		}
	}
}
