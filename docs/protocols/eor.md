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

RFC 885 negotiates EOR "independently for each direction", so there are two answers, not one:

- `PeerMarksRecords` — the peer has agreed to send `IAC EOR`. Set by its `WILL`. This is the
  direction that decides whether an inbound marker is a prompt or, per RFC 885, a NOP.
- `MarksOutboundRecords` — this end has agreed to mark its own records. Set by the peer's `DO`,
  which asks *this* end to send the marker. This is the direction that decides whether an outbound
  prompt ends with `IAC EOR`.

A server's usual handshake (`WILL` out, `DO` back) turns on only the second; a client's usual
handshake (`WILL` in, `DO` out) turns on only the first. Neither implies the other, and a refusal of
one leaves the other standing.

`IsEOREnabled` reports whether either is on. It is the older, vaguer question, kept because that is
what it always answered; prefer whichever of the two above matches the direction you mean.
`EnableEORAsync()` and `DisableEORAsync()` drive `MarksOutboundRecords` by hand, for a server that
wants to turn its own marker on or off mid-session.

`SuppressGoAheadProtocol` splits the same way and for the same reason — see `IsGoAheadSuppressed`
(the peer's direction) against `SuppressesOutboundGoAhead` (this end's), independent per RFC 858 §5.

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
