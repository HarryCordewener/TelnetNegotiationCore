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

The default NVT — no options negotiated — ends every prompt with a bare `IAC GA`, RFC 854's own
Go-Ahead, and this plugin is what notices one.

```csharp
.AddPlugin<SuppressGoAheadProtocol>()
    .OnPrompt(HandlePromptAsync)
```

A client always accepts a peer's `SUPPRESS-GO-AHEAD` offer, as RFC 1123 §3.2.2 requires, which stops
the marker arriving at all; `PacketPatchProtocol` is the fallback for exactly that case.
