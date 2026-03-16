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
			Log.Logger = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.Console()
				.MinimumLevel.Debug()
				.CreateLogger();

			var builder = Host.CreateApplicationBuilder(args);

			builder.Logging.ClearProviders();
			builder.Logging.AddSerilog();
			builder.Logging.SetMinimumLevel(LogLevel.Debug);

			builder.Services.AddTelnetClient();
			builder.Services.AddTransient<MockPipelineClient>();

			var host = builder.Build();

			await host.Services.GetRequiredService<MockPipelineClient>().StartAsync("127.0.0.1", 4201);
		}
	}
}