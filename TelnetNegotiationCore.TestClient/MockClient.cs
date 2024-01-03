using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.TestClient
{
	public class MockClient
	{
		readonly ILogger _Logger;
		readonly TcpClient? client = null;
		TelnetInterpreter? telnet = null;

		public MockClient(string address, int port, ILogger? logger = null)
		{
			Console.InputEncoding = Console.OutputEncoding = Encoding.UTF8;

			_Logger = logger ?? Log.Logger.ForContext<MockClient>();
			client = new TcpClient(address, port);
		}

		public async Task StartAsync()
		{
			var t = new Thread(new ParameterizedThreadStart(Handle));
			t.Start(client);

			while (true)
			{
				var read = Console.ReadLine() + Environment.NewLine; 
				
				if(telnet != null)
				{
					await telnet.SendPromptAsync(telnet?.CurrentEncoding.GetBytes(read));
				}
			}
		}

		private async Task WriteToOutputStreamAsync(byte[] arg, StreamWriter writer)
		{
			try 
			{ 
				await writer.BaseStream.WriteAsync(arg, CancellationToken.None);
			}
			catch(ObjectDisposedException ode)
			{
				_Logger.Information("Stream has been closed", ode);
			}
		}

		public static Task WriteBackAsync(byte[] writeback, Encoding encoding) =>
			Task.Run(() => Console.WriteLine(encoding.GetString(writeback)));

		public Task SignalGMCPAsync((string module, string writeback) val, Encoding encoding) =>
			Task.Run(() => _Logger.Debug("GMCP Signal: {Module}: {WriteBack}", val.module, val.writeback));

		public Task SignalMSSPAsync(MSSPConfig val) =>
			Task.Run(() => _Logger.Debug("New MSSP: {@MSSP}", val));

		public Task SignalPromptAsync() =>
			Task.Run(() => _Logger.Debug("Prompt"));

		public Task SignalNAWSAsync(int height, int width) => 
			Task.Run(() => _Logger.Debug("Client Height and Width updated: {Height}x{Width}", height, width));

		public void Handle(object? obj)
		{
			var client = (TcpClient)obj!;
			int port = -1;

			try
			{
				port = ((IPEndPoint)client.Client.RemoteEndPoint!).Port;

				using var stream = client.GetStream();
				using var input = new StreamReader(stream);
				using var output = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };

				telnet = new TelnetInterpreter(TelnetInterpreter.TelnetMode.Client, _Logger.ForContext<TelnetInterpreter>())
				{
					CallbackOnSubmitAsync = WriteBackAsync,
					CallbackNegotiationAsync = (x) => WriteToOutputStreamAsync(x, output),
					SignalOnGMCPAsync = SignalGMCPAsync,
					SignalOnMSSPAsync = SignalMSSPAsync,
					SignalOnNAWSAsync = SignalNAWSAsync,
					SignalOnPromptingAsync = SignalPromptAsync,
					CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") }
				}.BuildAsync()
				 .GetAwaiter()
				 .GetResult();

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
		}
	}
}
