using Serilog;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TelnetNegotiationCore
{
	public class MockServer
  {
		readonly TcpListener server = null;
    readonly ILogger _Logger;

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

    public Task WriteBack(string writeback)
    {
      // The Regex removes control characters.
      // Regex.Replace(writeback, @"\p{Cc}+", String.Empty);
      _Logger.Information("Writeback: {writeBack}", writeback);
      return Task.CompletedTask;
    }

    public Task SignalNAWS(int height, int width)
    {
      _Logger.Information("Client Height and Width updated: {height}x{width}", height, width);
      return Task.CompletedTask;
    }

    public void HandleDevice(object obj)
    {
      TcpClient client = (TcpClient)obj;
      var stream = client.GetStream();
      var input = new StreamReader(stream);
			var output = new StreamWriter(stream)
			{
				AutoFlush = true
			};
			var telnet = new TelnetInterpretor(_Logger.ForContext<TelnetInterpretor>());
      telnet.RegisterStream(input, output);
      telnet.RegisterWriteBack(WriteBack);
      telnet.RegisterNAWSCallback(SignalNAWS);
      telnet.RegisterCharsetOrder(new[] { "utf-8", "iso-8859-1" });
      telnet.ProcessAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }
  }
}
