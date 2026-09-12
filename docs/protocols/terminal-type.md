# TTYPE and MTTS — terminal type

`TerminalTypeProtocol` implements RFC 1091 and the
[MTTS](https://tintin.mudhalla.net/protocols/mtts) extension of it. A server asks `SEND`, and the
client answers one terminal name per request, cycling through its list and repeating the last entry
once the list runs out — which is how a server knows it has reached the end.

**As a client, do not configure this directly.** MTTS defines the first response as the *client name*
and the third as an `MTTS <bitvector>` of capabilities, and NEW-ENVIRON asks for the same facts under
different names, so both come from one place:

```csharp
.WithClientIdentity(new ClientIdentity("MY-CLIENT")
{
    Version = "1.0.0",
    TerminalType = "XTERM",
    Mtts = MttsCapabilities.Ansi | MttsCapabilities.Colors256
})
.AddPlugin<TerminalTypeProtocol>()
```

[Saying who your application is](../guides/client-identity.md) covers that in full, including which
MTTS bits this library calculates rather than takes your word for.

Configure nothing and TTYPE answers `UNKNOWN` — RFC 1091's own word for a terminal that will not name
itself. The library never invents a terminal, and never introduces your application as `TNC`.

**To state the list verbatim**, bypassing identity entirely:

```csharp
.AddPlugin<TerminalTypeProtocol>()
    .WithTerminalTypes("MUINDEX-CRAWLER", "MUINDEX", "MTTS 9")   // sent in this order
```

**As a server**, registering the plugin is enough: it asks, and the answers arrive over the usual
plugin state. `TerminalTypeProtocol.ObservedCapabilities(context)` reports the MTTS bits this library
can see for itself on a connection.
