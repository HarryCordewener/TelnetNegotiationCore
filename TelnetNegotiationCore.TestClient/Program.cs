using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace TelnetNegotiationCore.TestClient
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

			var builder = Host.CreateDefaultBuilder(args)
					.ConfigureServices(services =>
					{
						services.AddTransient<MockPipelineClient>();
					});

			var host = builder.ConfigureLogging(logging =>
			{
				logging.ClearProviders();
				logging.AddSerilog();
				logging.SetMinimumLevel(LogLevel.Debug);
			}).Build();

			await host.Services.GetRequiredService<MockPipelineClient>().StartAsync("127.0.0.1", 4201);
		}
	}
}