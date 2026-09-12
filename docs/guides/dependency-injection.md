# Dependency injection

For an application already built on `Microsoft.Extensions.DependencyInjection` — a Kestrel server, a
worker service — the builder does not have to be constructed by hand.

Register telnet services with `AddTelnetServer()` or `AddTelnetClient()` for seamless integration with `WebApplication.CreateBuilder()` or `Host.CreateApplicationBuilder()`. The factory automatically resolves the logger from the DI container. Configure shared plugins in the registration call:

```csharp
// Program.cs — Server with WebApplication
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTelnetServer(telnet => telnet
    .AddPlugin<NAWSProtocol>()
    .AddPlugin<GMCPProtocol>()
    .AddPlugin<MSSPProtocol>()
        .WithMSSPConfig(() => new MSSPConfig { Name = "My Server", UTF_8 = true })
    .AddPlugin<TerminalTypeProtocol>()
    .AddPlugin<CharsetProtocol>()
        .WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
    .AddPlugin<EORProtocol>()
    .AddPlugin<SuppressGoAheadProtocol>()
    .AddPlugin<MXPProtocol>());

builder.WebHost.UseKestrel(options =>
    options.ListenLocalhost(4202, listenOptions =>
        listenOptions.UseConnectionHandler<MyConnectionHandler>()));

var app = builder.Build();
await app.RunAsync();
```

```csharp
// ConnectionHandler — inject ITelnetInterpreterFactory
public class MyConnectionHandler(
    ILogger<MyConnectionHandler> logger,
    ITelnetInterpreterFactory telnetFactory) : ConnectionHandler
{
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var (telnet, readTask) = await telnetFactory.CreateBuilder()
            .OnSubmit(WriteBackAsync)
            .BuildAndStartAsync(connection.Transport);

        await readTask;
    }
}
```

```csharp
// Program.cs — Client with Host.CreateApplicationBuilder
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTelnetClient(telnet => telnet
    .AddDefaultMUDProtocols(onPrompt: HandlePromptAsync));
builder.Services.AddTransient<MyTelnetClient>();

var host = builder.Build();
await host.Services.GetRequiredService<MyTelnetClient>().ConnectAsync();
```

Passing `onPrompt` here is what turns on Packet Patch, alongside EOR and Suppress Go-Ahead — see
[Detecting prompts](prompts.md). Leaving it out is valid for a client that only cares about
complete lines: EOR and Suppress Go-Ahead still take a boundary's partial line off the line buffer
either way (onto `TelnetInterpreter.LastPromptBytes`), they just have nothing to call back into
without it, and Packet Patch is not registered at all.

