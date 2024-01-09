using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
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
				.MinimumLevel.Debug()
				.CreateLogger();

			Log.Logger = log;

			CreateWebHostBuilder(args).Build().Run();
			await Task.CompletedTask;
		}

		public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
				WebHost.CreateDefaultBuilder(args)
					.UseStartup<Startup>()
					.UseKestrel(options => options.ListenLocalhost(4202, builder => builder.UseConnectionHandler<KestrelMockServer>()));
	}
}