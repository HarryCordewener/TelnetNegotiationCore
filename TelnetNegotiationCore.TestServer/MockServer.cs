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

		public void StartListener()
		{
			try
			{
				while (true)
				{
					_Logger.Information("Waiting for a connection...");
					TcpClient client = server.AcceptTcpClient();
					var t = new Thread(new ParameterizedThreadStart(HandleDevice));
					t.Start(client);
				}
			}
			catch (SocketException e)
			{
				_Logger.Error(e, "SocketException occurred in thread");
				server.Stop();
			}
		}

		private async Task WriteToOutputStream(byte[] arg, StreamWriter writer) => await writer.BaseStream.WriteAsync(arg);

		public Task WriteBack(byte[] writeback, Encoding encoding)
		{
			string str = encoding.GetString(writeback);
			_Logger.Information("Writeback: {writeBack}", str);
			return Task.CompletedTask;
		}

		public Task SignalNAWS(int height, int width)
		{
			_Logger.Information("Client Height and Width updated: {height}x{width}", height, width);
			return Task.CompletedTask;
		}

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
					CallbackOnSubmit = WriteBack,
					CallbackNegotiation = (x) => WriteToOutputStream(x, output),
					NAWSCallback = SignalNAWS,
					CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
				}
					.RegisterMSSPConfig(() => new MSSPConfig
					{
						Name =  "My Telnet Negotiated Server",
						UTF_8 =  true,
						Gameplay =  new[] { "ABC", "DEF" },
						Extended = new Dictionary<string, dynamic>
					{
						{ "Foo",  "Bar"},
						{ "Baz",  new [] {"Moo", "Meow" }}
					}
					}).Validate().Build().GetAwaiter().GetResult();

				Clients.TryAdd(port, telnet);
				
				_Logger.Information("Connection: {connectionState}", "Connected");

				for(int currentByte = 0; currentByte != -1; currentByte = input.BaseStream.ReadByte())
				{
					telnet.InterpretAsync((byte)currentByte).GetAwaiter().GetResult();
				}

				_Logger.Information("Connection: {connectionState}", "Connection Closed");
			}
			catch (Exception ex)
			{
				_Logger.Error(ex, "Connection: {connectionStatus}", "Connection was unexpectedly closed.");
			}
			finally
			{
				Clients.TryRemove(port, out telnet);
			}
		}
	}
}
