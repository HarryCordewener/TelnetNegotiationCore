# Writing a client

A documented example exists in the [TestClient project](../../TelnetNegotiationCore.TestClient/MockPipelineClient.cs).

```csharp
await using var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .UseLogger(logger)
    .OnSubmit(WriteBackAsync)
    .WithClientIdentity(new ClientIdentity("MY-CLIENT")
    {
        Version = "1.0.0",
        TerminalType = "XTERM",
        Mtts = MttsCapabilities.Ansi | MttsCapabilities.Colors256
    })
    .AddPlugin<NAWSProtocol>()
    .AddPlugin<GMCPProtocol>()
        .OnGMCPMessage(SignalGMCPAsync)
    .AddPlugin<MSSPProtocol>()
        .OnMSSP(SignalMSSPAsync)
    .AddPlugin<TerminalTypeProtocol>()
    .AddPlugin<CharsetProtocol>()
        .WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
    .AddPlugin<EORProtocol>()
        .OnPrompt(SignalPromptAsync)
    .AddPlugin<SuppressGoAheadProtocol>()
    .StartAsync(tcpClient);   // CloseAsync on it closes the TcpClient

// Completion completes when the server closes the connection
await telnet.Completion;
```

See [Saying who your application is](client-identity.md) for `WithClientIdentity`, and
[Detecting prompts](prompts.md) for the three ways a client can notice one.

## Over a WebSocket

A game that takes players on `wss://` speaks telnet inside the socket (`telnet.mudstandards.org`). Connect with
`TelnetWebSocketClient`, then start the same builder on the socket:

```csharp
using var socket = await TelnetWebSocketClient.ConnectAsync(new Uri("wss://game.example/"),
    options => options.KeepAliveInterval = TimeSpan.FromSeconds(30));

await using var telnet = await CreateBuilder().StartAsync(socket);   // CloseAsync sends a 1000 close
await telnet.Completion;
```

It offers `telnet.mudstandards.org` and `telnet`, and takes a server that names no subprotocol as telnet, as
wss-to-telnet bridges do. To accept `gmcp.mudstandards.org` or another subprotocol as well, connect through a
registry; see [WebSockets](websocket.md#gmcp-and-terminal). The TestClient does both: pass it a `ws://` or
`wss://` address.
