using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TelnetNegotiationCore.TestServer;
using TelnetNegotiationCore.WebSockets;

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
{
	// Telnet over TCP.
	options.ListenLocalhost(4202, listenOptions =>
		listenOptions.UseConnectionHandler<KestrelMockServer>());
	// HTTP, for the WebSocket endpoint at ws://localhost:4203/ws.
	options.ListenLocalhost(4203);
});

var app = builder.Build();

// The mudstandards.org WebSocket subprotocols, most preferred first. The telnet ones run the same interpreter the
// TCP listener does; gmcp and terminal have no telnet layer and just echo.
var mock = ActivatorUtilities.CreateInstance<KestrelMockServer>(app.Services);
var wsLogger = app.Services.GetRequiredService<ILogger<KestrelMockServer>>();
var subprotocols = new WebSocketSubprotocolRegistry()
	.AddTelnet(mock.CreateBuilder, acceptNoSubprotocol: true)
	.Add(MudWebSocketSubprotocols.Gmcp, () => new GmcpWebSocketHandler
	{
		LineReceived = (h, line) => h.SendLineAsync($"We heard: {line}"),
		GmcpReceived = (h, package, data) =>
		{
			wsLogger.LogDebug("GMCP over WebSocket: {Package}: {Data}", package, data);
			return ValueTask.CompletedTask;
		},
	})
	.Add(MudWebSocketSubprotocols.Terminal, () => new TerminalWebSocketHandler
	{
		LineReceived = (h, line) => h.SendLineAsync($"We heard: {line}"),
	})
	.AddTelnetBase64(mock.CreateBuilder);

app.UseWebSockets();
app.Map("/ws", async (HttpContext context) =>
{
	if (!context.WebSockets.IsWebSocketRequest)
	{
		context.Response.StatusCode = StatusCodes.Status400BadRequest;
		return;
	}

	var offered = context.WebSockets.WebSocketRequestedProtocols;
	var chosen = subprotocols.Select(offered);
	if (chosen is null && (offered.Count > 0 || !subprotocols.HasFallback))
	{
		context.Response.StatusCode = StatusCodes.Status400BadRequest;
		return;
	}

	using var socket = await context.WebSockets.AcceptWebSocketAsync(chosen);
	wsLogger.LogInformation("WebSocket connected with subprotocol {Subprotocol}", chosen ?? "(none)");
	await subprotocols.RunAsync(socket, context.RequestAborted);
	wsLogger.LogInformation("WebSocket disconnected");
});

await app.RunAsync();