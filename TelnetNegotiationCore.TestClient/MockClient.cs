using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TelnetNegotiationCore.Interpreters;

namespace TelnetNegotiationCore.TestClient
{
	public class MockClient
	{
		readonly TcpClient? client = null;
		readonly ILogger _Logger;

		public MockClient(string ip, int port, ILogger? logger = null)
		{
			Console.OutputEncoding = Encoding.UTF8;
			_Logger = logger ?? Log.Logger.ForContext<MockClient>();
			IPAddress localAddress = IPAddress.Parse(ip);
			client = new TcpClient(localAddress.ToString(), port);
			Handle(client);
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

		public void Handle(TcpClient client)
		{
			int port = -1;
			TelnetInterpreter? telnet = null;

			try
			{
				port = ((IPEndPoint)client.Client.RemoteEndPoint).Port;

				using var stream = client.GetStream();
				using var input = new StreamReader(stream);
				using var output = new StreamWriter(stream) { AutoFlush = true };

				telnet = new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client, _Logger.ForContext<TelnetInterpreter>())
				{
					CallbackOnSubmit = WriteBack,
					CallbackNegotiation = (x) => WriteToOutputStream(x, output),
					NAWSCallback = SignalNAWS,
					CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
				}.Validate()
				 .Build()
				 .GetAwaiter()
				 .GetResult();

				_Logger.Information("Connection: {connectionState}", "Connected");

				for (int currentByte = 0; currentByte != -1; currentByte = input.BaseStream.ReadByte())
				{
					telnet.InterpretAsync((byte)currentByte).GetAwaiter().GetResult();
				}

				_Logger.Information("Connection: {connectionState}", "Connection Closed");
			}
			catch (Exception ex)
			{
				_Logger.Error(ex, "Connection: {connectionStatus}", "Connection was unexpectedly closed.");
			}
		}
	}
}
