# WebSockets

TNC speaks the subprotocols of the [mudstandards.org WebSocket proposal](https://mudstandards.org/websocket/),
and lets you add your own. TNC never touches HTTP: your web server accepts the socket, and TNC runs on it.

| Subprotocol | Frames | TNC support |
|---|---|---|
| `telnet.mudstandards.org` (alias `telnet`) | BINARY: the full telnet stream | `TelnetInterpreter`, every plugin unchanged |
| `telnetb64.mudstandards.org` | TEXT: the telnet stream, Base64 | `TelnetInterpreter`. The proposal advises against it. |
| `gmcp.mudstandards.org` | BINARY: UTF-8 text and ANSI. TEXT: GMCP | `GmcpWebSocketHandler`, no telnet |
| `terminal.mudstandards.org` | BINARY: UTF-8 text and ANSI | `TerminalWebSocketHandler`, no telnet |
| `divstream.mudstandards.org` | BINARY: HTML `div`s | none built in; write a handler |
| your own, e.g. `markup.v1.example.org` | your choice | a `WebSocketMessageHandler` subclass |

Everything is in `TelnetNegotiationCore.WebSockets`, and works on every target framework, netstandard2.0 included.

## Telnet over a WebSocket, the short way

If the endpoint only speaks telnet, hand the accepted socket to the builder. It is the same call as for a pipe,
stream or `TcpClient`, and the same plugins work:

```csharp
app.UseWebSockets();
app.Map("/ws", async (HttpContext ctx, ITelnetInterpreterFactory factory) =>
{
    var offered = ctx.WebSockets.WebSocketRequestedProtocols;
    var chosen = offered.FirstOrDefault(MudWebSocketSubprotocols.IsTelnet);
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync(chosen);

    var (telnet, read) = await factory.CreateBuilder()
        .AddDefaultMUDProtocols()
        .BuildAndStartAsync(socket, ctx.RequestAborted);
    await using (telnet)
    {
        await read;   // faults if the socket fails; the interpreter is disposed either way
    }
});
```

A socket with no subprotocol is taken as telnet, because most existing wss-to-telnet bridges never name one.
A socket that negotiated anything else (`gmcp.mudstandards.org`, say) throws `ArgumentException`, so non-telnet
frames never reach the telnet parser.

A client does the same with `ClientWebSocket`:

```csharp
var ws = new ClientWebSocket();
ws.Options.AddSubProtocol(MudWebSocketSubprotocols.Telnet);
await ws.ConnectAsync(new Uri("wss://game.example/ws"), ct);

var (telnet, read) = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    /* ... */
    .BuildAndStartAsync(ws, ct);
```

## Several subprotocols on one endpoint

`WebSocketSubprotocolRegistry` holds the subprotocols you accept, in your order of preference, and the handler for
each. Pick with `Select` before accepting, then `RunAsync`:

```csharp
var subprotocols = new WebSocketSubprotocolRegistry()
    .Add("markup.v1.example.org", () => new MarkupHandler(game))       // preferred when offered
    .AddTelnet(() => factory.CreateBuilder().AddDefaultMUDProtocols(), acceptNoSubprotocol: true)
    .Add(MudWebSocketSubprotocols.Gmcp, () => new GmcpWebSocketHandler
    {
        LineReceived = (h, line) => game.InputAsync(h, line),
        GmcpReceived = (h, package, data) => game.GmcpAsync(h, package, data),
    });

app.UseWebSockets();
app.Map("/ws", async (HttpContext ctx) =>
{
    var offered = ctx.WebSockets.WebSocketRequestedProtocols;
    var chosen = subprotocols.Select(offered);
    if (!ctx.WebSockets.IsWebSocketRequest || (chosen is null && (offered.Count > 0 || !subprotocols.HasFallback)))
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync(chosen);
    await subprotocols.RunAsync(socket, ctx.RequestAborted);
});
```

- `Select` returns the most preferred name the client offered, spelled the way the client spelled it, or `null`.
  Names compare case-insensitively.
- `AddTelnet` registers `telnet.mudstandards.org` and `telnet`. Its `onConnected` callback hands you each
  connection's `TelnetInterpreter`. The handler disposes the interpreter when the socket closes.
- `WithFallback` (or `acceptNoSubprotocol: true`) picks the handler for a client that offers nothing. Without one,
  `RunAsync` closes such a socket with 1002.
- A client offers the registry's names with `options.AddSubprotocols(registry)` and runs the same `RunAsync`.

## Writing a subprotocol handler

Subclass `WebSocketMessageHandler`. It reassembles whole messages, enforces `MaxMessageSize` (1 MiB by default,
1009 when exceeded), and closes with 1003 on a message type you did not override. One handler is made per
connection, so it can hold that connection's state.

```csharp
sealed class MarkupHandler(Game game) : WebSocketMessageHandler
{
    private Player? _player;

    protected override async ValueTask OnOpenAsync(CancellationToken ct) =>
        _player = await game.ConnectAsync(json => Connection.SendTextAsync(json));

    protected override ValueTask OnTextMessageAsync(string text, CancellationToken ct) =>
        _player!.InputAsync(text);

    protected override ValueTask OnClosedAsync(WebSocketCloseStatus? status, string? description) =>
        game.DisconnectAsync(_player);
}
```

`Connection` sends: `SendTextAsync`, `SendBinaryAsync`, `CloseAsync`. Sends are serialized, so any thread may call
them. A send after the socket closed does nothing.

For frame-level control (streaming, no reassembly) implement `IWebSocketSubprotocolHandler` directly; the telnet
handler does, because telnet is a byte stream and frame boundaries carry no meaning in it.

Name your own subprotocol under a domain you control and put a version in it (`markup.v1.example.org`), as RFC 6455
asks. An incompatible change is then a new name, and old clients keep working.

## gmcp and terminal

Neither has a telnet layer, so there is no NAWS, TTYPE, CHARSET, echo control or prompt marking.

- **Input** is split into lines on `\n`, with a trailing `\r` dropped. A message without a newline is one line,
  so a client that sends one command per frame works too.
- **GMCP**, on `gmcp.mudstandards.org`, is `Package.Name {json}` in a TEXT frame: the GMCP payload as telnet
  carries it, which is what Evennia and Telix send. The proposal itself does not spell the format out.
  `GmcpWebSocketHandler.Send` is a `GmcpSend`, so the GMCP package's sessions work unchanged:
  `new GmcpServerSession(handler.Send)`.

## MSSP

Advertise the endpoint with the two variables the proposal suggests:

```csharp
new MSSPConfig
{
    WebSocket = "wss://game.example/ws",
    WebSocketSubprotocols = "telnet.mudstandards.org,gmcp.mudstandards.org",
}
```

## Notes

- **Compression.** MCCP works over `telnet.mudstandards.org`, but if the socket negotiated `permessage-deflate`,
  do not also offer MCCP: compressing twice gains nothing.
- **Client address.** Behind a reverse proxy, the client's address comes from the HTTP layer
  (`X-Forwarded-For`, `HttpContext.Connection.RemoteIpAddress`), not from MNES.
- The [TestServer](../../TelnetNegotiationCore.TestServer/Program.cs) serves every built-in subprotocol at
  `ws://localhost:4203/ws`.
