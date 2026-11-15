# Connections and sessions

`BuildAndStartAsync` hands back an interpreter and a read task. A server usually needs more than that: count the
traffic, look at what each chunk of input settled, end the connection from its side, and run the same
per-connection code whether the player came in on TCP or a WebSocket. This page covers the parts of the builder
that do that, so you do not have to write your own read loop to get them.

## Hooks on the read and write paths

Three builder methods apply in every read loop this library runs: `StartAsync`, every `BuildAndStartAsync`, and
the WebSocket handlers.

| Method | Called |
| --- | --- |
| `.OnReceived(bytes => …)` | With each chunk of telnet bytes, before the interpreter sees it. The chunk is only valid during the call. |
| `.OnInputProcessed(telnet => …)` | After each chunk has been handled, its callbacks included (`WaitForProcessingAsync`). The next read waits for it. |
| `.WrapOutput(write => bytes => …)` | Around every write the interpreter makes. Several wrappers compose; the last one added is outermost. |

```csharp
var builder = factory.CreateBuilder()
    .AddDefaultMUDProtocols()
    .OnReceived(bytes => { traffic.Received(bytes.Length); return ValueTask.CompletedTask; })
    .OnInputProcessed(telnet => session.SampleNegotiationAsync(telnet))
    .WrapOutput(write => async bytes =>
    {
        traffic.Writing(bytes.Length);
        await write(bytes);
        traffic.Written(bytes.Length);
    });
```

On `telnetb64.mudstandards.org`, `OnReceived` sees the decoded bytes. A loop you write yourself around
`InterpretByteArrayAsync` does not run the input hooks; `WrapOutput` applies however the output is wired.

## `TelnetConnection`

`StartAsync` takes the same transports as `BuildAndStartAsync` and returns a `TelnetConnection`:

```csharp
await using var connection = await builder.StartAsync(context.Transport, ct);

connection.Interpreter      // the interpreter; the connection disposes it
connection.Completion       // the read loop
connection.Closed           // cancelled once the connection has ended, either side
await connection.CloseAsync();   // end it from this side, whatever the transport
connection.ClosedByServer   // whether CloseAsync ended it while the peer was still there
```

It deconstructs as the old pair, so `var (telnet, read) = await builder.StartAsync(...)` still reads the same.

`StartAsync` starts reading before it returns, so a callback can run before your code has the connection back. If
callbacks need the connection or its interpreter from a field, set the field in `.OnStarting`. It runs with the
new connection after the opening negotiation is written and before the read loop starts, and nothing is read
until it returns:

```csharp
var connection = await builder
    .OnStarting(started =>
    {
        _telnet = started.Interpreter;
        _connection = started;
        return ValueTask.CompletedTask;
    })
    .StartAsync(transport, ct);
```

It runs wherever a `TelnetConnection` is made, which includes `TelnetConnection.RunAsync` and sessions on a WebSocket
endpoint. `BuildAndStartAsync` makes no connection and does not call it. If it throws, `StartAsync` throws the
same exception, the interpreter is disposed, and nothing has been read; the transport is still yours to close.

- `CloseAsync` closes through the transport: it completes a pipe's output, disposes a stream, closes a
  `TcpClient`, or sends a WebSocket a 1000 close. A write after it does nothing.
- `DisposeAsync` stops the read loop and disposes the interpreter. It does not close the transport, and a read
  loop that failed does not fail it; `Completion` still reports the failure.
- `Completion` completes normally when the peer leaves, on `CloseAsync` or `DisposeAsync`, and when the start token
  is cancelled. It faults only when the transport fails. (The `BuildAndStartAsync` read task is unchanged: the
  start token cancels it.)

### Transports

`TelnetTransport` makes them, and each `close` argument replaces the default way of ending the connection:

| Transport | Default close |
| --- | --- |
| `TelnetTransport.Pipe(pipe, close?)` | Completes `pipe.Output`. Pass `() => { context.Abort(); return default; }` to drop a Kestrel connection at once. |
| `TelnetTransport.Stream(stream, close?)` | Disposes the stream. |
| `TelnetTransport.Tcp(client)` | Closes the client. |
| `TelnetTransport.WebSocket(socket)` | A 1000 close. The socket must speak a telnet subprotocol or none. |

To run telnet over anything else, implement `ITelnetTransport`: `ReadAsync`, `WriteAsync` and `CloseAsync`.

## Sessions: one connection's code, any transport

An `ITelnetSession` is one object per connection. It configures the builder, then runs alongside the read loop:

```csharp
sealed class PlayerSession(ITelnetInterpreterFactory factory, string address) : ITelnetSession
{
    public TelnetInterpreterBuilder CreateBuilder() =>
        factory.CreateBuilder()
            .AddDefaultMUDProtocols()
            .OnSubmit((bytes, encoding, _) => InputAsync(encoding.GetString(bytes)));

    public async Task RunAsync(TelnetConnection connection, CancellationToken ct)
    {
        // The interpreter is already reading, so the opening negotiation can finish.
        await connection.Interpreter.WaitForInitialNegotiationAsync(TimeSpan.FromMilliseconds(400), ct);
        await RegisterAsync(address, ct);
        await connection.Completion;
    }
}
```

The connection is closed and disposed when `RunAsync` returns. The same class runs over each transport:

```csharp
// Kestrel, raw TCP
public override Task OnConnectedAsync(ConnectionContext context) =>
    TelnetConnection.RunAsync(
        new PlayerSession(factory, context.RemoteEndPoint?.ToString() ?? "unknown"),
        TelnetTransport.Pipe(context.Transport, () => { context.Abort(); return default; }),
        context.ConnectionClosed);

// A WebSocket endpoint that only speaks telnet
app.Map("/telnet", async (HttpContext ctx) =>
{
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync(
        ctx.WebSockets.WebSocketRequestedProtocols.FirstOrDefault(MudWebSocketSubprotocols.IsTelnet));
    await TelnetConnection.RunAsync(
        new PlayerSession(factory, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        TelnetTransport.WebSocket(socket),
        ctx.RequestAborted);
});
```

Where the endpoint serves several subprotocols, register the session on the registry instead:

```csharp
registry.AddTelnetSession(() => new PlayerSession(factory, address), acceptNoSubprotocol: true);
// or, for a session that needs no state of its own:
registry.AddTelnetSession(() => factory.CreateBuilder().AddDefaultMUDProtocols(),
    async (connection, ct) => await connection.Completion);
```

The registry's factory takes no arguments, so a session that needs the request (its address, say) builds the
registry per request, or calls `TelnetConnection.RunAsync` itself as above. `TelnetWebSocketHandler.ForSession`
makes the handler for any other name, `telnetb64.mudstandards.org` for one.

Connection metadata (the client's address behind a proxy, whether it came over TLS) stays with your application:
it comes from the HTTP or socket layer, which this library never touches.
