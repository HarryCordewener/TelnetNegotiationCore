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

To receive GMCP messages, use the `OnGMCPMessage` callback as shown in the initialization example above.

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

