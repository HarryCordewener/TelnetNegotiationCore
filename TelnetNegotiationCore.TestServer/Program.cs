using System;

namespace TelnetNegotiationCore.TestServer
{
	class Program
	{
		static void Main(string[] args)
		{
			MockServer server = new MockServer("127.0.0.1", 4201);
		}
	}
}
