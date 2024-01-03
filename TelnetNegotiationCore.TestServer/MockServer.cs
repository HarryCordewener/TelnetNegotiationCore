using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.TestServer
{
	public class MockServer
	{
		readonly TcpListener server = null;
		readonly ILogger _Logger;

		private readonly ConcurrentDictionary<int, TelnetInterpreter> Clients = new();

		public MockServer(string ip, int port, ILogger logger = null)
		{
			Console.OutputEncoding = Encoding.UTF8;
			_Logger = logger ?? Log.Logger.ForContext<MockServer>();
			IPAddress localAddr = IPAddress.Parse(ip);
			server = new TcpListener(localAddr, port);
			server.Start();
		}

		public async Task StartListenerAsync()
		{
			while (true)
			{
				try
				{
					_Logger.Information("Waiting for a connection...");
					TcpClient client = await server.AcceptTcpClientAsync();
					var t = new Thread(new ParameterizedThreadStart(HandleDevice));
					t.Start(client);
				}
				catch (SocketException e)
				{
					_Logger.Error(e, "SocketException occurred in thread");
					server.Stop();
				}
			}
		}

		private static async Task WriteToOutputStreamAsync(byte[] arg, StreamWriter writer) => await writer.BaseStream.WriteAsync(arg);

		public Task SignalGMCPAsync((string module, string writeback) val, Encoding encoding) =>
			Task.Run(() => _Logger.Debug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback));

		public Task SignalMSSPAsync(MSSPConfig val) =>
			Task.Run(() => _Logger.Debug("New MSSP: {@MSSPConfig}", val));

		public Task SignalNAWSAsync(int height, int width) =>
			Task.Run(() => _Logger.Debug("Client Height and Width updated: {Height}x{Width}", height, width));

		public static Task WriteBackAsync(byte[] writeback, Encoding encoding) =>
			Task.Run(() => Console.WriteLine(encoding.GetString(writeback)));

		public void HandleDevice(object obj)
		{
			int port = -1;
			TelnetInterpreter telnet = null;
			TcpClient client = null;

			try
			{
				client = (TcpClient)obj;
				port = ((IPEndPoint)client.Client.RemoteEndPoint).Port;

				using var stream = client.GetStream();
				using var input = new StreamReader(stream);
				using var output = new StreamWriter(stream) { AutoFlush = true };

				telnet = new TelnetInterpreter(TelnetInterpreter.TelnetMode.Server, _Logger.ForContext<TelnetInterpreter>())
				{
					CallbackOnSubmitAsync = WriteBackAsync,
					SignalOnGMCPAsync = SignalGMCPAsync,
					SignalOnMSSPAsync = SignalMSSPAsync,
					SignalOnNAWSAsync = SignalNAWSAsync,
					CallbackNegotiationAsync = (x) => WriteToOutputStreamAsync(x, output),
					CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
				}
					.RegisterMSSPConfig(() => new MSSPConfig
					{
						Name = "My Telnet Negotiated Server",
						UTF_8 = true,
						Gameplay = new[] { "ABC", "DEF" },
						Extended = new Dictionary<string, dynamic>
					{
						{ "Foo",  "Bar"},
						{ "Baz",  new [] {"Moo", "Meow" }}
					}
					})
					.BuildAsync()
					.GetAwaiter()
					.GetResult();

				Clients.TryAdd(port, telnet);

				_Logger.Information("Connection: {ConnectionState}", "Connected");

				for (int currentByte = 0; currentByte != -1; currentByte = input.BaseStream.ReadByte())
				{
					telnet.InterpretAsync((byte)currentByte).GetAwaiter().GetResult();
				}

				_Logger.Information("Connection: {ConnectionState}", "Connection Closed");
			}
			catch (Exception ex)
			{
				_Logger.Error(ex, "Connection: {ConnectionState}", "Connection was unexpectedly closed.");
			}
			finally
			{
				Clients.TryRemove(port, out telnet);
			}
		}
	}
}
