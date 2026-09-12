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

