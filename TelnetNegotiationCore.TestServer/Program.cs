using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TelnetNegotiationCore.TestServer;

Log.Logger = new LoggerConfiguration()
	.Enrich.FromLogContext()
	.WriteTo.Console()
	.MinimumLevel.Debug()
	.CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging(logging =>
{
	logging.ClearProviders();
	logging.AddSerilog();
	logging.SetMinimumLevel(LogLevel.Debug);
});

builder.Services.AddTelnetServer();

builder.WebHost.UseKestrel(options =>
	options.ListenLocalhost(4202, listenOptions =>
		listenOptions.UseConnectionHandler<KestrelMockServer>()));

var app = builder.Build();

await app.RunAsync();