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
using TelnetNegotiationCore.Interpretors;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.TestServer
{
	public class MockServer
	{
		readonly TcpListener server = null;
		readonly ILogger _Logger;

		private readonly ConcurrentDictionary<int, TelnetInterpretor> Clients = new ConcurrentDictionary<int, TelnetInterpretor>();

		public MockServer(string ip, int port, ILogger logger = null)
		{
			_Logger = logger ?? Log.Logger.ForContext<MockServer>();
			IPAddress localAddr = IPAddress.Parse(ip);
			server = new TcpListener(localAddr, port);
			server.Start();
			StartListener();
		}

		public void StartListener()
		{
			try
			{
				while (true)
				{
					_Logger.Information("Waiting for a connection...");
					TcpClient client = server.AcceptTcpClient();
					Thread t = new Thread(new ParameterizedThreadStart(HandleDevice));
					t.Start(client);
				}
			}
			catch (SocketException e)
			{
				_Logger.Error(e, "SocketException occurred in thread");
				server.Stop();
			}
		}

		public Task WriteBack(byte[] writeback, Encoding encoding)
		{
			// The Regex removes control characters.
			// Regex.Replace(writeback, @"\p{Cc}+", String.Empty);
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
			TelnetInterpretor telnet = null;
			TcpClient client = null;

			try
			{
				client = (TcpClient)obj;
				port = ((IPEndPoint)client.Client.RemoteEndPoint).Port;

				using (var stream = client.GetStream())
				using (var input = new StreamReader(stream))
				using (var output = new StreamWriter(stream) { AutoFlush = true })
				{
					telnet = new TelnetInterpretor(TelnetInterpretor.TelnetMode.Server, _Logger.ForContext<TelnetInterpretor>())
						.RegisterStream(input, output)
						.RegisterCallback(WriteBack)
						.RegisterNAWSCallback(SignalNAWS)
						.RegisterCharsetOrder(new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") })
						.RegisterMSSPConfig(new MSSPConfig
						{
							Name = () => "My Telnet Negotiated Server",
							UTF_8 = () => true,
							Gameplay = () => new[] { "ABC", "DEF" },
							Extended = new Dictionary<string, Func<dynamic>>
						{
						{ "Foo", () => "Bar"},
						{ "Baz", () => new [] {"Moo", "Meow" }}
						}
						});

					Clients.TryAdd(port, telnet);
					telnet.ProcessAsync().ConfigureAwait(false).GetAwaiter().GetResult();
				}
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
