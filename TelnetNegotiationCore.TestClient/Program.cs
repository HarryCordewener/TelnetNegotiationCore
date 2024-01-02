using Serilog;
using Serilog.Formatting.Compact;

namespace TelnetNegotiationCore.TestClient
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
			var server = new MockClient("127.0.0.1", 4201, log.ForContext<MockClient>());
		}
	}
}