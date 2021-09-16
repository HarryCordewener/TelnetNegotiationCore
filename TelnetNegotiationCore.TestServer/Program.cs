using Serilog;
using Serilog.Formatting.Compact;

namespace TelnetNegotiationCore.TestServer
{
	public class Program
	{
		static void Main(string[] args)
		{
			var log = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.Console()
				.WriteTo.File(new CompactJsonFormatter(), "log.txt")
				.MinimumLevel.Verbose()
				.CreateLogger();

			Log.Logger = log;

			MockServer server = new MockServer("127.0.0.1", 4201, log.ForContext<MockServer>());
		}
	}
}