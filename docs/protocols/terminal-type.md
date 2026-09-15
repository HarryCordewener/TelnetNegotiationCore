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

**As a server**, registering the plugin is enough: it asks, and the answers are exposed through
`TerminalTypeProtocol.TerminalTypes` and `TelnetInterpreter.TerminalTypes`. To react as each answer
arrives, register a callback:

```csharp
.AddPlugin<TerminalTypeProtocol>()
    .OnTerminalTypes(types => HandleTerminalTypesAsync(types))
```

The default bundle has the same fluent configuration shape:

```csharp
.AddDefaultMUDProtocols()
.OnTerminalTypes(types => HandleTerminalTypesAsync(types))
```

The callback receives the same read-only snapshot shape as those properties. It runs after every
new answer and once more when the repeated final answer completes the cycle and expands an
`MTTS <bitvector>` into capability names. The next `SEND` request is written before the callback is
awaited, so callback latency cannot stall the negotiation round trip.

`TerminalTypeProtocol.ObservedCapabilities(context)` reports the MTTS bits this library can see for
itself on a connection.

## The 40-character limit

RFC 1091 says "the maximum length of a terminal type name is 40 characters". That constrains
senders, so this library enforces it where a name is *configured* — `WithTerminalTypes`,
`ClientIdentity.Name` and `ClientIdentity.TerminalType` all throw `ArgumentException` naming the
offending value — rather than at the moment of sending, where a mistake would already be a
non-conforming frame on the wire.

It is refused rather than truncated because nothing legitimate comes close. The MTTS cycle sends a
client name, a terminal type and an `MTTS <bitvector>` claim, and the longest values in real use are
terminal types like `XTERM-256COLOR`, at 14 characters. A limit no real client approaches cannot
wrongly refuse one, and silently shortening what an application asked to send would be worse than
telling it.

Receiving stays deliberately liberal: the limit is a rule for senders, and a peer that exceeds it is
still understood. `ClientIdentity.Version` is not constrained either — it goes to MNES as
`CLIENT_VERSION`, not into a TTYPE response, and MNES sets no such limit.
