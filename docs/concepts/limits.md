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
library policy, not a protocol constant. Messages that exceed it are **dropped, never truncated** —
half a JSON document is invalid JSON, and a consumer cannot tell it apart from a malformed server.
Reaching the limit is never silent, but what is reported differs per protocol:

| Protocol | At the ceiling |
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

