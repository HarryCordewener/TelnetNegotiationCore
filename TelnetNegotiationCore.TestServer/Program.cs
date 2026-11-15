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

// The mudstandards.org WebSocket subprotocols, most preferred first. The telnet ones run the same session the TCP
// listener does; gmcp and terminal have no telnet layer and just echo.
var mock = ActivatorUtilities.CreateInstance<KestrelMockServer>(app.Services);
var wsLogger = app.Services.GetRequiredService<ILogger<KestrelMockServer>>();
WebSocketSubprotocolRegistry Subprotocols(string remote) => new WebSocketSubprotocolRegistry()
	.AddTelnetSession(() => mock.CreateSession(remote), acceptNoSubprotocol: true)
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
	.Add(MudWebSocketSubprotocols.TelnetBase64, () => TelnetWebSocketHandler.ForSession(() => mock.CreateSession(remote), base64: true));

// One registry for choosing, before the socket is accepted; the per-request one carries the client's address.
var subprotocols = Subprotocols("(none)");

app.UseWebSockets(new WebSocketOptions
{
	// Ping every 30 seconds, and drop a client that has not answered within 15: a player whose network vanished
	// sends no close frame, and without a timeout the server would hold the connection until TCP gives up.
	KeepAliveInterval = TimeSpan.FromSeconds(30),
	KeepAliveTimeout = TimeSpan.FromSeconds(15),
	// A browser does not apply CORS to WebSockets: list the pages allowed to open one. Empty allows every origin,
	// which a game whose login happens in-band (as telnet's does) can accept; one that authenticates with cookies
	// cannot.
});
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

	// No permessage-deflate: MCCP already compresses the telnet stream, and compressing a password together with text
	// another player chose is what CRIME and BREACH exploit.
	using var socket = await context.WebSockets.AcceptWebSocketAsync(chosen);
	wsLogger.LogInformation("WebSocket connected with subprotocol {Subprotocol}", chosen ?? "(none)");
	// Awaited: the socket lives only as long as this request does.
	await Subprotocols(context.Connection.RemoteIpAddress?.ToString() ?? "unknown").RunAsync(socket, context.RequestAborted);
	wsLogger.LogInformation("WebSocket disconnected");
});

await app.RunAsync();