# EOR and SUPPRESS-GO-AHEAD — prompt markers

Two options, one purpose: letting a peer say "this is where my output stops and yours begins". Both
route into the same `.OnPrompt(() => ...)` callback, and
[Detecting prompts](../guides/prompts.md) is the page that explains when to use which — including
`PacketPatchProtocol`, the fallback for a server that marks nothing at all.

## `EORProtocol` (RFC 885 / [EOR](https://tintin.mudhalla.net/protocols/eor))

The peer negotiates option 25 and then sends `IAC EOR` at the end of each prompt. The clearest signal
there is, when a server offers it.

```csharp
.AddPlugin<EORProtocol>()
    .OnPrompt(HandlePromptAsync)
```

`IsEOREnabled` reports whether the negotiation took. `EnableEORAsync()` and `DisableEORAsync()` drive
it by hand for a server that wants to turn the marker on or off mid-session.

## `SuppressGoAheadProtocol` (RFC 858)

In the default NVT, RFC 854's `IAC GA` is how a half-duplex peer says the line is now yours, and
this plugin is what notices one and reports it through `OnPrompt`. **It is not a guarantee.** RFC 854
requires no `GA` after any particular line, and plenty of servers never send one; what this plugin
gives you is a callback for the peers that do. Output that carries no marker does not reach
`OnPrompt` from here at all — [`PacketPatchProtocol`](../guides/prompts.md) is the separate fallback
for that.

```csharp
.AddPlugin<SuppressGoAheadProtocol>()
    .OnPrompt(HandlePromptAsync)
```

A client always accepts a peer's `SUPPRESS-GO-AHEAD` offer, as RFC 1123 §3.2.2 requires. Once that
is agreed the peer stops sending `GA`, so there is nothing left for this plugin to report — which is
the other reason `PacketPatchProtocol` exists.
