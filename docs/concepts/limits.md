# Limits on untrusted input

Everything this library parses arrives from a peer it does not trust, and a peer decides how much of
it to send. Every accumulator therefore has a ceiling, and reaching one is never silent.

GMCP, MSDP, MSSP and CHARSET TTABLE payloads arrive from an untrusted peer, so each is bounded. The
default is **1 MiB per message**, configurable per protocol:

```csharp
.AddPlugin<GMCPProtocol>()
    .OnGMCPMessage(HandleGMCPAsync)
    .WithMaxMessageSize(256 * 1024)                 // default: 1 MiB
    .OnGMCPMessageTooLarge(x => LogDroppedAsync(x)) // (Package, ReceivedBytes, MaxMessageSize)
```

None of the GMCP, MSDP or MSSP specifications defines a maximum message size, so the limit is a
library policy, not a protocol constant. The limit is **inclusive**: a message of exactly
`MaxMessageSize` bytes is delivered normally, and it is the byte after that which marks the message
as overflowed. Messages that exceed it are **dropped, never truncated** —
half a JSON document is invalid JSON, and a consumer cannot tell it apart from a malformed server.
Exceeding the limit is never silent, but what is reported differs per protocol:

| Protocol | Once it is exceeded |
| --- | --- |
| GMCP | `Error` log naming the package, the bytes received and the limit; `OnGMCPMessageTooLarge((Package, ReceivedBytes, MaxMessageSize))` |
| MSDP | `Error` log with the bytes received and the limit; `OnMSDPMessageTooLarge((ReceivedBytes, MaxMessageSize))` — MSDP messages have no package name |
| MSSP | `Error` log with the bytes received and the limit; `OnMSSPMessageTooLarge((ReceivedBytes, MaxMessageSize))`. The count is over the whole report — every variable name and value together — because a report of a hundred thousand tiny variables costs the same memory as one enormous value |
| CHARSET TTABLE | `Error` log with the bytes received and the limit, plus a `TTABLE-REJECTED` reply to the peer, which is what RFC 2066 provides for this case. No callback: the `OnTTableReceived` callback is never handed a partial table |

The connection is unaffected in every case; the next message is processed normally.

Ordinary (non-negotiation) input is bounded too. A peer decides when to send a newline, so the line
the interpreter assembles is the one accumulator a peer can grow simply by never terminating a line;
`.WithMaxBufferSize(bytes)` sets that ceiling (default 5 MiB). A line past it is dropped whole, with
an `Error` log, and the connection carries on with the next.

## Compression, and the work a peer can buy

The limits above bound how much **memory** a peer can make this side hold. They say nothing about the
**work of getting there**, and [MCCP](../protocols/mccp.md) is where the difference shows: one
compressed byte can inflate to 1,032, and every one of those goes through the state machine before
any downstream ceiling looks at it.

Measured on this library, in Release: 4 KiB of deflate holding 4 MiB of zeros — a ratio of about
1,025:1 — costs roughly **430 ms of a core**, against about 1 ms for 4 KiB of plain telnet. That is
the whole attack: a peer trades its own bandwidth for several hundred times as much of yours, and
memory never grows enough for the line buffer to object.

So the inflater has a ceiling of its own:

| | |
| --- | --- |
| Default | **200:1** cumulative output to input, `.WithMaxExpansionRatio(n)` to change it |
| Not judged below | 1 MiB of output, so a short stream is never condemned by a ratio taken from a handful of bytes |
| At the ceiling | `Error` log naming the bytes and the ratio; the inflater stops for good, `IsMCCP2Enabled` / `IsMCCP3Enabled` go back to `false`, and nothing further is delivered from that direction — exactly what an invalid deflate stream already does |
| Not affected | Real streams. MCCP's own claim is a 75–90% reduction, which is 4:1 to 10:1; zlib's window is 32 KiB, so a sustained ratio far above that needs input built to produce it rather than content that happens to repeat |
