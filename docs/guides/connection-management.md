# Managing the read loop yourself

`BuildAndStartAsync` wires the connection and runs the read loop for you, and is what
[Getting started](getting-started.md) uses. When you need the loop itself — a transport this library
does not know about, or your own framing around it — build the interpreter and drive it.

## Wiring only the write side

`UsePipe` or `UseStream` wires `OnNegotiation` to the transport and leaves the reading to you:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(HandleSubmitAsync)
    .UsePipe(connection.Transport)   // wires OnNegotiation → pipe.Output
    .AddPlugin<NAWSProtocol>()
    .BuildAsync();

await TelnetInterpreterBuilder.ReadFromPipeAsync(telnet, connection.Transport.Input, cancellationToken);
```

## Managing both sides

Or supply `OnNegotiation` yourself and own the whole thing:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(HandleSubmitAsync)
    .OnNegotiation(data => WriteToNetworkAsync(data))
    .AddPlugin<NAWSProtocol>()
    .BuildAsync();

while (true)
{
    var result = await connection.Transport.Input.ReadAsync();
    foreach (var segment in result.Buffer)
        await telnet.InterpretByteArrayAsync(segment);

    // Advance before the completion check: the final read owns pipe memory too, and a loop that
    // breaks first never hands it back.
    connection.Transport.Input.AdvanceTo(result.Buffer.End);
    if (result.IsCompleted) break;
}
```

Every byte handed to `InterpretByteArrayAsync` goes through the same state machine the built-in loop
uses, so negotiation, [compression](../protocols/mccp.md) and the
[line buffer's limits](../concepts/limits.md) all behave exactly as they do under
`BuildAndStartAsync`.
