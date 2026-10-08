# TelnetNegotiationCore.Gmcp

The standard GMCP packages for MUD clients and servers: the `Core` package (`Core.Hello`,
`Core.Supports.Set`/`Add`/`Remove`, `Core.Ping`, `Core.KeepAlive`, `Core.Goodbye`), and typed messages
for `Client.Media`, `Client.GUI`, `Client.Map`, `Char.Login`, `External.Discord`, `Char.Vitals`,
`Room.Info` and `Comm.Channel.Text`.

It does not depend on TelnetNegotiationCore. A session takes a `GmcpSend` delegate to send with and
is handed each received message, so it works over any GMCP transport. TelnetNegotiationCore wires
one up in a call:

```csharp
var telnet = await builder
    .AddPlugin<GMCPProtocol>().UseGmcpServerSession(out var gmcp)
    .BuildAsync();

// Later, from the game:
if (gmcp.Supports("Char.Vitals"))
{
    await gmcp.SendAsync("Char.Vitals", """{"hp":100,"maxhp":120}""");
}
```

`GmcpServerSession` records the client's name and version from `Core.Hello`, keeps the module list
from `Core.Supports`, answers `Core.Ping`, reports `Core.KeepAlive`, sends `Core.Goodbye`, and passes
every other message to `OnMessageAsync`. `Supports("Char.Vitals")` is true when the client listed
`Char.Vitals` or `Char`.

`GmcpClientSession` sends `Core.Hello` and the `Core.Supports` messages, times `Core.Ping` round
trips, and reports `Core.Goodbye`. With TelnetNegotiationCore, `UseGmcpClientSession` sends
`Core.Hello` from `WithClientIdentity` and `Core.Supports.Set` as soon as GMCP is agreed:

```csharp
var telnet = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Client)
    .WithClientIdentity("MyClient", "1.0")
    .AddPlugin<GMCPProtocol>()
        .UseGmcpClientSession(out var gmcp, new("Char", 1), new("Room", 1))
    .BuildAsync();
```

Over another transport, call `HelloAsync` and `SetSupportsAsync` yourself when GMCP is agreed.

Typed messages have `ToJson()` and `TryParse`, and either session sends one with `SendAsync(message)`:

```csharp
await gmcp.SendAsync(new CharVitals { Hp = 100, MaxHp = 120 });
```

Other `Char.*` packages have no agreed keys between servers. `GmcpPackages` names them, and the game
sends its own JSON.

`MsdpNames` holds the MSDP command, list and variable names from the MSDP specification
(`MsdpNames.Command.Report`, `MsdpNames.Character.HealthMax`, `MsdpNames.Mapping.Room`), and
`MsdpNames.GmcpPackage`, the `MSDP` package that carries MSDP over GMCP.
