﻿using Serilog;
using System.Net.Sockets;
using TelnetNegotiationCore.Interpreters;
using System.IO.Pipelines;
using Pipelines.Sockets.Unofficial;
using System.Text;
using TelnetNegotiationCore.Models;
using System.Collections.Immutable;

namespace TelnetNegotiationCore.TestClient
{
	public class MockPipelineClient
	{
		readonly ILogger _Logger;

		public MockPipelineClient()
		{
			_Logger = Log.Logger.ForContext<MockPipelineClient>();
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

		public static Task WriteBackAsync(byte[] writeback, Encoding encoding, TelnetInterpreter t) =>
			Task.Run(() => Console.WriteLine(encoding.GetString(writeback.AsSpan())));

		public Task SignalGMCPAsync((string module, string writeback) val)
		{
			_Logger.Debug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback);
			return Task.CompletedTask;
		}

		public Task SignalMSSPAsync(MSSPConfig val)
		{
			_Logger.Debug("New MSSP: {@MSSPConfig}", val);
			return Task.CompletedTask;
		}

		public Task SignalPromptAsync() =>
			Task.Run(() => _Logger.Debug("Prompt"));

		public async Task StartAsync(string address, int port)
		{
			var client = new TcpClient(address, port);
			var stream = client.GetStream();
			var pipe = StreamConnection.GetDuplex(stream, new PipeOptions());

			var telnet = await new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client, _Logger.ForContext<TelnetInterpreter>())
			{
				CallbackOnSubmitAsync = WriteBackAsync,
				CallbackNegotiationAsync = (x) => WriteToOutputStreamAsync(x, pipe.Output),
				SignalOnGMCPAsync = SignalGMCPAsync,
				SignalOnMSSPAsync = SignalMSSPAsync,
				SignalOnPromptingAsync = SignalPromptAsync,
				CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
			}.BuildAsync();

			var backgroundTask = Task.Run(() => ReadFromPipeline(telnet, pipe.Input));

			while (true)
			{
				string read = Console.ReadLine() ?? string.Empty;

				if (telnet != null)
				{
					await telnet.SendPromptAsync(telnet?.CurrentEncoding.GetBytes(read));
				}
			}
		}

		/// <summary>
		/// Read data coming from the server and interpret it.
		/// </summary>
		/// <param name="reader">The Pipeline Reader</param>
		/// <returns>A ValueTask</returns>
		static async ValueTask ReadFromPipeline(TelnetInterpreter telnet, PipeReader reader)
		{
			while (true)
			{
				// await some data being available
				ReadResult read = await reader.ReadAtLeastAsync(1);

				foreach(var segment in read.Buffer)
				{
					await telnet.InterpretByteArrayAsync(segment.Span.ToImmutableArray());
				}

				// tell the pipe that we used everything
				reader.AdvanceTo(read.Buffer.End);
			}
		}
	}
}
