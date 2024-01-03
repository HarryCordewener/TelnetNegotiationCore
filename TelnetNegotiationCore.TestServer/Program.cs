using Serilog;
using Serilog.Formatting.Compact;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.TestServer
{
	public class Program
	{
		static async Task Main()
		{
			var log = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.Console()
				.WriteTo.File(new CompactJsonFormatter(), "LogResult.log")
				.MinimumLevel.Debug()
				.CreateLogger();

			Log.Logger = log;
			var server = new MockServer("127.0.0.1", 4202, log.ForContext<MockServer>());
			await server.StartListenerAsync();
		}
	}
}