# Writing a client

A documented example exists in the [TestClient project](../../TelnetNegotiationCore.TestClient/MockPipelineClient.cs).

```csharp
var (telnet, readTask) = await new TelnetInterpreterBuilder()
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
    .BuildAndStartAsync(tcpClient);

// readTask completes when the server closes the connection
await readTask;
```

See [Saying who your application is](client-identity.md) for `WithClientIdentity`, and
[Detecting prompts](prompts.md) for the three ways a client can notice one.
