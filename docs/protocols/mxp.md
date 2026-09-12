# MXP — MUD eXtension Protocol

`MXPProtocol` negotiates telnet option 91, the option under which a server's output may carry
[MXP](https://www.zuggsoft.com/zmud/mxp.htm) tags and entities.

MXP takes **two steps, not one**. `WILL` / `DO` settles the telnet option, and then the server sends
`IAC SB MXP IAC SE` — the marker that means "everything after this is MXP". Only at that marker does a
client start parsing tags and decoding entities; before it, a client is in plain telnet and would show
the server's `<send>` tags and `&quot;` entities to the player verbatim. Both halves are here: the
server sends the marker when the client says `DO`, and the client recognises it when a server sends it.

```csharp
.AddPlugin<MXPProtocol>()
    .OnMXPEnabled(() => SwitchToMxpRendererAsync())
```

`OnMXPEnabled` fires when MXP output actually begins — after the marker, on both sides — so a host that
switches renderers in that callback cannot emit a tag ahead of it. `IsMXPActive` and
`IsMxpModeStarted` are the same facts as properties.

Unlike [MCCP](mccp.md)'s identically shaped marker, this one does **not** change how the byte stream is
framed: ordinary text keeps flowing through the same state machine.

**Line modes are yours to write.** `ESC[0z` (open), `ESC[1z` (secure), `ESC[2z` (locked), `ESC[6z`
(lock secure) and the rest are in-band output, not negotiation, and are deliberately not sent from
here — which lines a game is willing to let carry live tags is its policy, not this library's. The same
goes for the tags themselves: this library negotiates MXP, it does not render or generate it.

See also: [Adding MXP support to a MUD server](https://www.gammon.com.au/mushclient/addingservermxp.htm).
