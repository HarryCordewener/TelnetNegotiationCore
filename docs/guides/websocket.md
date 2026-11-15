# WebSockets

TNC speaks the subprotocols of the [mudstandards.org WebSocket proposal](https://mudstandards.org/websocket/),
and lets you add your own. TNC never touches HTTP: your web server accepts the socket, and TNC runs on it.

| Subprotocol | Frames | TNC support |
|---|---|---|
| `telnet.mudstandards.org` (alias `telnet`) | BINARY: the full telnet stream | `TelnetInterpreter`, every plugin unchanged |
| `telnetb64.mudstandards.org` | TEXT: the telnet stream, Base64 | `TelnetInterpreter`. The proposal advises against it. |
| `gmcp.mudstandards.org` | BINARY: UTF-8 text and ANSI. TEXT: GMCP | Server: `GmcpWebSocketHandler`. Client: `TelnetInterpreter` |
| `terminal.mudstandards.org` | BINARY: UTF-8 text and ANSI | Server: `TerminalWebSocketHandler`. Client: `TelnetInterpreter` |
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

    await using var telnet = await factory.CreateBuilder()
        .AddDefaultMUDProtocols()
        .StartAsync(socket, ctx.RequestAborted);
    await telnet.Completion;   // faults only if the socket fails; the interpreter is disposed either way
});
```

A socket with no subprotocol is taken as telnet, because most existing wss-to-telnet bridges never name one.
A socket that negotiated anything else (`gmcp.mudstandards.org`, say) throws `ArgumentException`, so non-telnet
frames never reach the telnet parser.

A client connects with `TelnetWebSocketClient.ConnectAsync`, which offers `telnet.mudstandards.org`, `telnet`,
`gmcp.mudstandards.org` and `terminal.mudstandards.org`, and the server picks. A server that names none is taken as
telnet, as above. A client interpreter runs on whichever was picked, so the same code reaches a telnet bridge and an
Evennia game (see [A client on gmcp or terminal](#a-client-on-gmcp-or-terminal)):

```csharp
using var socket = await TelnetWebSocketClient.ConnectAsync(new Uri("wss://game.example/ws"),
    options => options.KeepAliveInterval = TimeSpan.FromSeconds(30), ct);

await using var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    /* ... */
    .StartAsync(socket, ct);
await telnet.Completion;   // the server closed the connection
```

The `configure` lambda sets anything else on `ClientWebSocketOptions` (headers, a proxy, `KeepAliveTimeout` on
.NET 9 or later). The socket is yours to dispose.

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
- A client uses a registry the same way: `registry.ConnectAsync(uri)` offers its names in order, and
  `registry.RunAsync(socket)` runs whichever one the server picked. `options.AddSubprotocols(registry)` does the
  offering alone, for a `ClientWebSocket` you connect yourself.
- `AddTelnetSession` registers the telnet names to run an `ITelnetSession`, the same object a TCP connection
  runs; see [Connections and sessions](sessions.md).

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
  `new GmcpServerSession(handler.Send)` on a server, `new GmcpClientSession(handler.Send)` on a client.
- **Text as sent.** `LineReceived` suits a server reading commands. A client wants the server's output whole,
  prompts without a line ending included: set `TextReceived`, which gets each BINARY message before any line
  splitting.

```csharp
var registry = new WebSocketSubprotocolRegistry()
    .AddTelnet(CreateClientBuilder)                       // offered first
    .Add(MudWebSocketSubprotocols.Gmcp, () => new GmcpWebSocketHandler
    {
        TextReceived = (h, text) => screen.WriteAsync(text),
        GmcpReceived = (h, package, data) => ui.GmcpAsync(package, data),
    });

using var socket = await registry.ConnectAsync(new Uri("wss://game.example/ws"), cancellationToken: ct);
await registry.RunAsync(socket, ct);   // telnet or gmcp, whichever the server chose
```

## A client on gmcp or terminal

Evennia speaks no telnet over a WebSocket: it offers `json.mudstandards.org`, `gmcp.mudstandards.org`,
`terminal.mudstandards.org` and its own `v1.evennia.com`. Offered only the telnet names it names none and sends
`v1.evennia.com` JSON in TEXT frames, which the telnet transport closes with 1003.

`TelnetTransport.WebSocketClient` (which `StartAsync(socket)` uses for a client interpreter) runs the interpreter on
any subprotocol that has a `WebSocketClientBridge`. `WebSocketClientBridges.Default` holds two, `GmcpClientBridge`
and `TerminalClientBridge`, and `TelnetWebSocketClient.ConnectAsync` offers them after the telnet names. A bridge
turns the server's frames into a telnet stream and decides what each thing the interpreter writes becomes:

- **Text** in BINARY frames is the stream as is, with any `0xFF` doubled so it stays data. What the client sends goes
  out as one BINARY message per write.
- **GMCP**, on `gmcp`, arrives as `IAC SB GMCP … IAC SE`, and goes out as a TEXT frame. The connection opens as if
  the server had sent `IAC WILL GMCP`: picking the subprotocol is its agreement, so `OnGMCPNegotiated` fires and a
  client that waits for it before saying `Core.Hello` says it then.
- **Every other telnet command is dropped**, the answer to that `WILL GMCP` and the client's own opening `WILL NAWS`
  included. A server with no telnet layer reads those bytes as text, in front of the next line the player sends,
  which is usually the login: the game shows its connect screen again and logs nobody in.
- **Nothing else is negotiated**: no NAWS, TTYPE, CHARSET, echo control or prompt marking. The text is UTF-8, so
  decode as UTF-8 rather than waiting for CHARSET. A prompt such as `Name: ` has no line ending and no GA or EOR, so
  let `PacketPatchProtocol` find it from the pause after it: `AddDefaultMUDProtocols(onPrompt: …)` adds it only when
  `onPrompt` is given. A password is echoed by the server's own rules.

```csharp
builder.AddDefaultMUDProtocols(onPrompt: () => screen.ShowPromptAsync(connection.Interpreter.LastPromptBytes));
```

A TEXT frame on `terminal` closes the socket with 1003. A server interpreter refuses both subprotocols; serve them with
`GmcpWebSocketHandler` and `TerminalWebSocketHandler`.

### Your own subprotocol

`OptionTextFrameBridge` is GMCP's shape for any option: text in BINARY frames, one telnet option in TEXT frames,
`WILL` fed on open. Override `Decode`/`Encode` when the TEXT format is not the bare payload. For anything else,
subclass `WebSocketClientBridge`; by default it behaves as `terminal`, and you override what your subprotocol changes:

| Override | Called with | Default |
|---|---|---|
| `OnOpenAsync` | a `TelnetInput` | nothing; feed the `WILL`s the subprotocol implies |
| `OnBinaryMessageAsync` / `OnTextMessageAsync` | a whole message and the input | text / close 1003 |
| `OnTextAsync` | text the interpreter wrote | one BINARY message |
| `OnSubnegotiationAsync` | an option and its unescaped payload | dropped |
| `OnCommandAsync` | `WILL`/`WONT`/`DO`/`DONT` and an option, or GA, EOR, NOP… | dropped |

`TelnetInput` feeds the interpreter: `TextAsync` (escaped), `WillAsync`, `DoAsync`, `SubnegotiationAsync`,
`RawAsync`. Register the bridge and offer it:

```csharp
var bridges = WebSocketClientBridges.Default
    .Add("msdp.v1.example.org", () => new OptionTextFrameBridge((byte)Trigger.MSDP));

using var socket = await TelnetWebSocketClient.ConnectAsync(uri, null, bridges, ct);
await using var connection = await builder.StartAsync(TelnetTransport.WebSocketClient(socket, bridges), ct);
```

## MSSP

Advertise the endpoint with the two variables the proposal suggests:

```csharp
new MSSPConfig
{
    WebSocket = "wss://game.example/ws",
    WebSocketSubprotocols = "telnet.mudstandards.org,gmcp.mudstandards.org",
}
```

## Running it in production

- **Await the run.** The socket lives as long as the request does, so the endpoint awaits `RunAsync` or
  `Completion`; returning early disposes the socket under the connection.
- **Detect vanished players.** A client whose network drops sends no close frame. Set
  `WebSocketOptions.KeepAliveTimeout` (with `KeepAliveInterval`) so the server pings and aborts a socket that
  stops answering; TNC keeps a receive pending, which the pong needs. With `OnInputProcessed`, each chunk can hold
  the next receive for up to a second, so keep the timeout well above that.
- **Bounded close.** A close this side starts waits `MudWebSocketConnection.CloseTimeout` (5 seconds) for the
  peer's answer, then aborts the socket, so a silent peer cannot hold the connection open. Pass your own
  `MudWebSocketConnection` to `TelnetTransport.WebSocket` or `RunAsync` to change it.
- **Origins.** Browsers apply no CORS to WebSockets, so any page can open one to your endpoint. That is harmless
  when login happens in-band, as telnet's does; if the endpoint trusts cookies, list the allowed pages in
  `WebSocketOptions.AllowedOrigins`. The `Origin` header is not authentication.
- **Compression.** Leave `permessage-deflate` off on the telnet subprotocols. MCCP already compresses the stream,
  and compressing a password in the same context as text another player chose is what CRIME and BREACH exploit.
  If you do enable it for another subprotocol, set `DisableServerContextTakeover`.

## Notes

- **Client address.** Behind a reverse proxy, the client's address comes from the HTTP layer
  (`X-Forwarded-For`, `HttpContext.Connection.RemoteIpAddress`), not from MNES.
- The [TestServer](../../TelnetNegotiationCore.TestServer/Program.cs) serves every built-in subprotocol at
  `ws://localhost:4203/ws`.
