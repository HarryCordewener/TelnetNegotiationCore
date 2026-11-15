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

			// No arguments: telnet over TCP to the TestServer. A ws:// or wss:// address: telnet over a WebSocket,
			// e.g. ws://localhost:4203/ws for the TestServer's endpoint.
			var client = host.Services.GetRequiredService<MockPipelineClient>();
			if (args.Length > 0 && Uri.TryCreate(args[0], UriKind.Absolute, out var uri) && uri.Scheme is "ws" or "wss")
				await client.StartAsync(uri);
			else
				await client.StartAsync("127.0.0.1", 4202);
		}
	}
}