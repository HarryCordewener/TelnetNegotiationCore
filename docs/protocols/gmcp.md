# GMCP — Generic Mud Communication Protocol

[GMCP](https://tintin.mudhalla.net/protocols/gmcp) carries structured JSON out of band, under a
dotted package name: `Char.Vitals`, `Room.Info`, `Core.Ping`. `GMCPProtocol` negotiates telnet option
201 and hands you each message as a `(Package, Info)` pair.

```csharp
.AddPlugin<GMCPProtocol>()
    .OnGMCPMessage(HandleGMCPAsync)
    .WithMaxMessageSize(256 * 1024)   // default: 1 MiB
```

A message over that ceiling is dropped whole rather than truncated — see
[Limits on untrusted input](../concepts/limits.md).

## Sending

Both clients and servers can send GMCP messages using the `SendGMCPCommand` method. The method takes a package name and JSON data.

```csharp
// Send a simple GMCP message
await telnet.SendGMCPCommand("Core.Hello", "{\"client\":\"MyClient\",\"version\":\"1.0\"}");

// Send character vitals
await telnet.SendGMCPCommand("Char.Vitals", "{\"hp\":1000,\"maxhp\":1500,\"mp\":500,\"maxmp\":800}");

// Send room information
await telnet.SendGMCPCommand("Room.Info", "{\"num\":12345,\"name\":\"A dark room\",\"area\":\"The Dungeon\"}");

// The telnet interpreter will automatically handle GMCP negotiation
// Messages will only be sent if the remote party supports GMCP
```

An empty data string sends the package name alone, with no trailing space: `SendGMCPCommand("Core.Ping", "")`
writes `IAC SB GMCP Core.Ping IAC SE`.

`OnGMCPNegotiated` runs when the peer agrees to GMCP (`true`) or refuses or withdraws it (`false`).
A client introduces itself there, because `Core.Hello` "needs to be the first message that the client
sends". The callback runs after this side's own `DO` or `WILL` has gone out, so whatever it sends
follows the agreement on the wire.

```csharp
.AddPlugin<GMCPProtocol>()
    .OnGMCPNegotiated(async agreed =>
    {
        if (agreed)
        {
            await telnet.SendGMCPCommand("Core.Hello", "{\"client\":\"MyClient\",\"version\":\"1.0\"}");
        }
    })
```

## The Core package

The `TelnetNegotiationCore.Gmcp` package implements the GMCP `Core` package for both sides. It
does not depend on this library: a session sends through a delegate and is handed each message.

- `GmcpServerSession` reads `Core.Hello` (name and version) and keeps the `Core.Supports.Set`/`Add`/`Remove`
  list, up to `MaxModules` (256) names. `Supports("Char.Vitals")` is true when the client listed `Char.Vitals` or `Char`. It also
  answers `Core.Ping`, reports `Core.KeepAlive`, sends `Core.Goodbye`, and passes every other
  message to `OnMessageAsync`.
- `GmcpClientSession` sends `Core.Hello` and the `Core.Supports` messages, times `Core.Ping` round
  trips, and reports `Core.Goodbye`.

```csharp
TelnetInterpreter? telnet = null;
var gmcp = new GmcpServerSession((package, data) => telnet!.SendGMCPCommand(package, data));

telnet = await builder
    .AddPlugin<GMCPProtocol>().OnGMCPMessage(gmcp.HandleAsync)
    .BuildAsync();
```

Mudlet sends `Core.Hello` and then `Core.Supports.Set` as soon as GMCP is agreed. Blightmud sends
`Core.Hello` and adds modules one at a time with `Core.Supports.Add`. TinTin++ and MUSHclient send
neither unless a script does. An empty module list therefore usually means the client never said, so
`SendAsync` sends regardless and `SendIfSupportedAsync` is the opt-in check.

To receive GMCP messages, use the `OnGMCPMessage` callback as shown in the initialization example above.

## Standard packages

The same package has typed messages for the packages Mudlet acts on without a script, and for the
few content packages whose keys a client script depends on. Each has `ToJson()`, a `Package` name in
the casing clients expect, and a `TryParse` for the receiving side. `SendAsync(message)` and
`SendIfSupportedAsync(message)` send one.

| Package | Types |
| --- | --- |
| `Client.Media` | `MediaDefault`, `MediaLoad`, `MediaPlay`, `MediaStop`, `MediaPause` |
| `Client.GUI`, `Client.Map` | `ClientGui`, `ClientMap` |
| `Char.Login` | `LoginDefault`, `LoginCredentials`, `LoginResult`, and for version 2 `LoginUrl`, `LoginToken`, `LoginReconnect`, `LoginAuthCode` |
| `External.Discord` | `DiscordHello`, `DiscordInfo`, `DiscordStatus` |
| `IRE.Composer` | `ComposerEdit`, `ComposerSetBuffer` |
| `Char.Vitals` | `CharVitals` (`hp`, `maxhp`, `mp`, `maxmp`, `mv`, `maxmv`, `xp`, `maxxp`, `nl`, as Mudlet's base UI reads them) |
| `Room.Info` | `RoomInfo` (the IRE shape Mudlet's mapper reads) |
| `Comm.Channel.Text` | `CommChannelText` |

```csharp
await gmcp.SendIfSupportedAsync(new MediaPlay("rain.mp3") { Type = MediaType.Sound, Loops = -1 });
await gmcp.SendAsync(new RoomInfo(6008, "Market Square") { Area = "Midgaard", Exits = new Dictionary<string, long> { ["n"] = 6011 } });
```

Some of these go past the published specifications and follow what Mudlet sends and reads:

- `Client.Media.Pause` pauses matching media; a later `MediaPlay` that matches resumes it.
  `GmcpPackages.ClientMediaObsoleteDefault` is the old `Client.Media` name for `Client.Media.Default`.
- `ClientGui.BaseUi = false` (or `ClientGui.DeclineBaseUi`) tells Mudlet the game brings its own
  interface. `ClientGui.TryParse` also reads the plain form, a version line and a URL line.
- `Char.Login` version 2, which Mudlet announces as `Char.Login 2`: `LoginDefault` carries `Version`
  and the client-driven OAuth fields (`ClientId`, `Scopes`, `NonceRequired`); client messages carry
  `Version` and `TokenStorage`; `LoginUrl` sends a sign-in page; `LoginToken` hands the client a token
  it replays in `LoginReconnect`; `LoginAuthCode` completes a browser sign-in with PKCE. A version 2
  hand-off to the game's own sign-in screen is a `LoginCredentials` with no account
  (`IsEmpty`), and one with an account and `Provider` but no password asks to resume that
  provider's sign-in. The `ToString` of each message leaves out passwords, tokens and codes.
- `IRE.Composer.Edit` opens Mudlet's editor on some text, and `IRE.Composer.SetBuffer` brings it back.
  Its data section is a JSON string, not an object.

Servers disagree on the keys of most other `Char.*` packages, so `GmcpPackages` only names them
(`GmcpPackages.CharStatus`, `GmcpPackages.CharItemsList`, ...) and the game sends its own JSON with
`SendAsync(string, string)`. `GmcpPackages.Is(received, GmcpPackages.RoomInfo)` compares names without
regard to case.

## Messages without a data section

The GMCP specification says the data field "is optional and should be separated from the package
field with a space", and that "when sending a command without a data section the space should be
omitted". A bodyless message such as `Core.Ping` is delivered with `Package = "Core.Ping"` and
**`Info = ""`** — an empty string, not `"{}"`. The tuple reports what was on the wire; a consumer
that would rather see an empty object can substitute one:

```csharp
ValueTask HandleGMCPAsync((string Package, string Info) message)
{
    var json = string.IsNullOrEmpty(message.Info) ? "{}" : message.Info;
    ...
}
```

A message that runs its data straight into the package name with no space (`Char.Vitals{"hp":1}`)
is malformed by that same sentence, but a package name cannot contain `{`, so it is accepted —
split at the first character that cannot belong to a package name — and logged as a warning naming
the package. A payload with no package name at all is discarded, also with a warning that quotes
what was thrown away.

## MSDP over GMCP (MoG)

GMCP can carry MSDP. The specification: *"When using MoG (MSDP over GMCP) the package name is
considered case sensitive and MSDP must be fully capitalized"*, and *"the data field must use the
JSON data syntax with keywords being case sensitive using UTF-8 encoding"* — so MoG is JSON, not
MSDP's own byte encoding:

```
client - IAC SB GMCP 'MSDP {"LIST" : "COMMANDS"}' IAC SE
server - IAC SB GMCP 'MSDP {"COMMANDS" : ["LIST", "REPORT", "RESET", "SEND", "UNREPORT"]}' IAC SE
```

A message whose package is exactly `MSDP` is routed to `OnMSDPMessage` with its data section
forwarded verbatim, which is the same JSON shape a native `IAC SB MSDP` subnegotiation produces
(MSDP tables are JSON objects, MSDP arrays are JSON arrays). Consequences worth knowing:

- The package name is matched **case sensitively**. A `msdp` package is an ordinary GMCP package
  and goes to `OnGMCPMessage`.
- A data section that is not valid JSON is **discarded with an `Error` log** rather than forwarded:
  the callback's contract is JSON. Nothing is thrown onto the read loop, and the connection carries
  on with the next message.
- If no `MSDPProtocol` plugin is registered, a MoG message is delivered to `OnGMCPMessage` with
  `Package = "MSDP"` instead of being dropped.
- `MSDPServerHandler` answers over GMCP, as `MSDP {...}`, when the client agreed to GMCP and not to
  MSDP. A client that agreed to both is answered with native MSDP, which the specification expects
  it to read "interchangably". `TelnetInterpreter.SendMSDPVariablesAsync` makes the same choice for
  anything else you send.

