# Getting started

`TelnetInterpreterBuilder` is the whole configuration surface: a mode, a logger, the callbacks your
application cares about, and one `AddPlugin<T>()` per telnet option you want to speak. Every option
is opt-in — a plugin you do not register is an option this connection answers `WONT` / `DONT` to.

```bash
dotnet add package TelnetNegotiationCore
```

## Quick start

`StartAsync` wires the connection and starts the read loop automatically. Pass an `IDuplexPipe` (e.g. Kestrel's `connection.Transport`), a `TcpClient`, a raw `Stream` or a telnet [WebSocket](websocket.md). It returns a `TelnetConnection`, which owns the interpreter: dispose the connection and the interpreter goes with it.

```csharp
await using var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(HandleSubmitAsync)
    .AddPlugin<NAWSProtocol>()
        .OnNAWS(HandleWindowSizeAsync)
    .AddPlugin<GMCPProtocol>()
        .OnGMCPMessage(HandleGMCPAsync)
    .AddPlugin<MSSPProtocol>()
        .OnMSSP(HandleMSSPAsync)
        .WithMSSPConfig(() => new MSSPConfig { Name = "My Server", UTF_8 = true })
    .AddPlugin<CharsetProtocol>()
        .WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
    .AddPlugin<EORProtocol>()
    .AddPlugin<SuppressGoAheadProtocol>()
    .AddPlugin<MXPProtocol>()
    .StartAsync(connection.Transport);   // IDuplexPipe (Kestrel), TcpClient, Stream or WebSocket

await telnet.Completion; // completes when the connection closes
```

`AddDefaultMUDProtocols()` registers NAWS, GMCP, MSDP, MSSP, Terminal Type, Charset, EOR, Suppress
Go-Ahead, and MXP in one call. Packet Patch — the silence-inferred prompt fallback, see
[Detecting prompts](prompts.md) below — joins them too, but only when a `onPrompt` callback is
given: unlike the other two prompt sources it drains the line buffer on a guess rather than an
explicit marker from the peer, so it is not added for a consumer who never asked for prompts at all.

```csharp
await using var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(HandleSubmitAsync)
    .AddDefaultMUDProtocols(
        onNAWS: HandleWindowSizeAsync,
        onGMCPMessage: HandleGMCPAsync,
        msspConfig: () => new MSSPConfig { Name = "My Server", UTF_8 = true }
    )
    .StartAsync(connection.Transport);

await telnet.Completion;
```

`BuildAndStartAsync` takes the same arguments and returns the older `(interpreter, readTask)` pair, which leaves
the interpreter yours to dispose. `telnet.Interpreter` is the interpreter here, for sending.

## Next

- [Dependency injection](dependency-injection.md) — `AddTelnetServer()` / `AddTelnetClient()` and Kestrel.
- [Writing a client](client.md) and [writing a server](server.md) — the two worked examples.
- [Builder reference](../reference/builder.md) — every callback and setting in one list.
- [Protocols](../protocols/index.md) — one page per telnet option.
