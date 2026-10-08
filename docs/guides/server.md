# Writing a server

A documented example exists in the [TestServer project](../../TelnetNegotiationCore.TestServer/KestrelMockServer.cs).
This uses a Kestrel server to make the TCP handling easier.

> **Thread safety**: All outgoing writes are serialized internally. You do **not** need external locking around concurrent writes on a telnet connection.

```csharp
public override async Task OnConnectedAsync(ConnectionContext connection)
{
    _logger.LogInformation("{ConnectionId} connected", connection.ConnectionId);

    // Reportable and sendable variables map a name to a function reading its current value, so the
    // list a client is offered and the value it is then sent come from one place. MSDP has three
    // shapes: a dictionary or JsonObject is a table, a collection is an array, and everything else
    // is text. A type of your own is carried by its serializer contract - see SerializerOptions
    // below - which is source-generated, so a server doing this still publishes with Native AOT.
    var msdpHandler = new MSDPServerHandler(new MSDPServerModel(MSDPUpdateBehavior)
    {
        Commands = () => ["help", "stats", "info"],
        Configurable_Variables = () => ["CLIENT_NAME", "CLIENT_VERSION", "PLUGIN_ID"],
        SerializerOptions = MsdpJsonContext.Default.Options,
        Reportable_Variables = new() { ["ROOM"] = () => CurrentRoom() },
        Sendable_Variables = new() { ["ROOM"] = () => CurrentRoom() },
        SetCallbackAsync = (variable, value) => SetClientVariableAsync(variable, value),
    }, _logger);

    // When a reported variable changes, tell the client - the handler re-reads and re-sends it.
    // await msdpHandler.Data.NotifyChangeAsync("ROOM");

    var (telnet, readTask) = await new TelnetInterpreterBuilder()
        .UseMode(TelnetInterpreter.TelnetMode.Server)
        .UseLogger(_logger)
        .OnSubmit(WriteBackAsync)
        .AddPlugin<NAWSProtocol>()
            .OnNAWS(SignalNAWSAsync)
        .AddPlugin<GMCPProtocol>()
            .OnGMCPMessage(SignalGMCPAsync)
        .AddPlugin<MSDPProtocol>()
            .OnMSDPMessage((t, config) => SignalMSDPAsync(msdpHandler, t, config))
        .AddPlugin<MSSPProtocol>()
            .OnMSSP(SignalMSSPAsync)
            .WithMSSPConfig(() => new MSSPConfig
            {
                Name = "My Telnet Negotiated Server",
                UTF_8 = true,
                Gameplay = ["ABC", "DEF"],
            })
        .AddPlugin<TerminalTypeProtocol>()
        .AddPlugin<CharsetProtocol>()
            .WithCharsetOrder(Encoding.UTF8, Encoding.GetEncoding("iso-8859-1"))
        .AddPlugin<EORProtocol>()
        .AddPlugin<SuppressGoAheadProtocol>()
        .AddPlugin<MXPProtocol>()
        .BuildAndStartAsync(connection.Transport);

    await readTask;
    _logger.LogInformation("{ConnectionId} disconnected", connection.ConnectionId);
}
```

## Copyover

A copyover replaces the server process while keeping the sockets, so the new process does not know
what each client agreed to. GMCP, MSDP, MCCP3, MTTS and MNES all handle this the same way: withdraw
every offer before the copyover, then make them again afterwards as if the client had just connected.

```csharp
// Before exec: ends compression, then sends WONT/DONT for every option offered at connection start.
await telnet.UnannounceSupportAsync();

// In the new process, once the interpreter for the inherited socket is built:
await telnet.AnnounceSupportAsync();
```

`InitialOffers` lists the `WILL` and `DO` offers this side made, which is what
`UnannounceSupportAsync` withdraws. The client's answers arrive through the usual negotiation
handlers, so each protocol's `IsNegotiated` drops as the client acknowledges. This is what MTH calls
`unannounce_support` and `announce_support`.
