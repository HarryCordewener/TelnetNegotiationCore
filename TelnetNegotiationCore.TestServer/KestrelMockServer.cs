﻿using Serilog;
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

namespace TelnetNegotiationCore.TestServer
{
	public class KestrelMockServer : ConnectionHandler
	{
		readonly ILogger _Logger;
		private static readonly string[] config = ["ABC", "DEF"];
		private static readonly string[] value = ["Moo", "Meow"];

		public KestrelMockServer(ILogger logger = null): base()
		{
			Console.OutputEncoding = Encoding.UTF8;
			_Logger = logger ?? Log.Logger.ForContext<KestrelMockServer>();
		}

		private async Task WriteToOutputStreamAsync(byte[] arg, PipeWriter writer)
		{
			try
			{
				await writer.WriteAsync(new ReadOnlyMemory<byte>(arg), CancellationToken.None);
			}
			catch (ObjectDisposedException ode)
			{
				_Logger.Information("Stream has been closed", ode);
			}
		}

		public Task SignalGMCPAsync((string module, string writeback) val) =>
			Task.Run(() => _Logger.Debug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback));

		public Task SignalMSSPAsync(MSSPConfig val) =>
			Task.Run(() => _Logger.Debug("New MSSP: {@MSSPConfig}", val));

		public Task SignalNAWSAsync(int height, int width) =>
			Task.Run(() => _Logger.Debug("Client Height and Width updated: {Height}x{Width}", height, width));

		public static async Task WriteBackAsync(byte[] writeback, Encoding encoding, PipeWriter writer)
		{
			var str = encoding.GetString(writeback);
			if (str.StartsWith("echo"))
			{
				await writer.WriteAsync(encoding.GetBytes($"We heard: {str}" + Environment.NewLine));
			}
			Console.WriteLine(encoding.GetString(writeback));
		}

		public async override Task OnConnectedAsync(ConnectionContext connection)
		{
			_Logger.Information(connection.ConnectionId + " connected");

			var telnet = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server)
			{
				CallbackOnSubmitAsync = (w,e) => WriteBackAsync(w,e, connection.Transport.Output),
				SignalOnGMCPAsync = SignalGMCPAsync,
				SignalOnMSSPAsync = SignalMSSPAsync,
				SignalOnNAWSAsync = SignalNAWSAsync,
				CallbackNegotiationAsync = (x) => WriteToOutputStreamAsync(x, connection.Transport.Output),
				CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
			}
				.RegisterMSSPConfig(() => new MSSPConfig
				{
					Name = "My Telnet Negotiated Server",
					UTF_8 = true,
					Gameplay = config,
					Extended = new Dictionary<string, dynamic>
				{
						{ "Foo",  "Bar"},
						{ "Baz", value }
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

			_Logger.Information(connection.ConnectionId + " disconnected");
		}
	}
}
