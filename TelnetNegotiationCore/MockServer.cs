using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TelnetNegotiationCore
{
	public class MockServer
  {
		readonly TcpListener server = null;

    public MockServer(string ip, int port)
    {
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
          Console.WriteLine("Waiting for a connection...");
          TcpClient client = server.AcceptTcpClient();
          Console.WriteLine("Connected!");
          Thread t = new Thread(new ParameterizedThreadStart(HandleDevice));
          t.Start(client);
        }
      }
      catch (SocketException e)
      {
        Console.WriteLine("SocketException: {0}", e);
        server.Stop();
      }
    }
    public void HandleDevice(Object obj)
    {
      TcpClient client = (TcpClient)obj;
      var stream = client.GetStream();
      var input = new StreamReader(stream);
      var output = new StreamWriter(stream);
      output.AutoFlush = true;
      var telnet = new TelnetInterpretor();
      telnet.RegisterStream(input, output);
      telnet.Process();
    }
}
}
