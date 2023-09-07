using Serilog;
using Serilog.Formatting.Compact;

namespace TelnetNegotiationCore.TestServer
{
	public class Program
	{
		static void Main()
		{
			var log = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.Console()
				.WriteTo.File(new CompactJsonFormatter(), "logresult.log")
				.MinimumLevel.Debug()
				.CreateLogger();

			Log.Logger = log;
			var server = new MockServer("127.0.0.1", 4202, log.ForContext<MockServer>());
			server.StartListener();
		}
	}
}