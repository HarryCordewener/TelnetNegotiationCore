using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Serilog;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.TestServer;

public class Program
{
	public static async Task Main(string[] args)
	{
		var log = new LoggerConfiguration()
			.Enrich.FromLogContext()
			.WriteTo.Console()
			.MinimumLevel.Debug()
			.CreateLogger();

		Log.Logger = log;

		await CreateWebHostBuilder(args).Build().RunAsync();
	}

	private static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
		WebHost.CreateDefaultBuilder(args)
			.UseStartup<Startup>()
			.UseKestrel(options => options.ListenLocalhost(4202, builder => builder.UseConnectionHandler<KestrelMockServer>()));
}