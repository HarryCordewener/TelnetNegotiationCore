# TelnetNegotiationCore.Gmcp

The standard GMCP packages for MUD clients and servers: the `Core` package (`Core.Hello`,
`Core.Supports.Set`/`Add`/`Remove`, `Core.Ping`, `Core.KeepAlive`, `Core.Goodbye`), and typed messages
for `Client.Media`, `Client.GUI`, `Client.Map`, `Char.Login` (version 2, as Mudlet speaks it),
`External.Discord`, `IRE.Composer`, `Char.Vitals`, `Room.Info` and `Comm.Channel.Text`.

It does not depend on TelnetNegotiationCore. A session takes a `GmcpSend` delegate to send with and
is handed each received message, so it works over any GMCP transport. With TelnetNegotiationCore:

```csharp
TelnetInterpreter? telnet = null;
var gmcp = new GmcpServerSession((package, data) => telnet!.SendGMCPCommand(package, data));

telnet = await builder
    .AddPlugin<GMCPProtocol>().OnGMCPMessage(gmcp.HandleAsync)
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
trips, and reports `Core.Goodbye`.

Typed messages have `ToJson()` and `TryParse`, and either session sends one with `SendAsync(message)`:

```csharp
await gmcp.SendAsync(new CharVitals { Hp = 100, MaxHp = 120 });
```

Other `Char.*` packages have no agreed keys between servers. `GmcpPackages` names them, and the game
sends its own JSON.
