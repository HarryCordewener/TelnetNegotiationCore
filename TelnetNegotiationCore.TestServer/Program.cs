using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Serilog;
using Serilog.Formatting.Compact;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.TestServer
{
	public class Program
	{
		static async Task Main(string[] args)
		{
			var log = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.Console()
				.WriteTo.File(new CompactJsonFormatter(), "LogResult.log")
				.MinimumLevel.Debug()
				.CreateLogger();

			Log.Logger = log;

			CreateWebHostBuilder(args).Build().Run();
			await Task.CompletedTask;
		}

		public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
				WebHost.CreateDefaultBuilder(args)
					.UseKestrel(options =>  options.ListenLocalhost(4202, builder => builder.UseConnectionHandler<KestrelMockServer>()))
					.UseStartup<Startup>();
	}
}