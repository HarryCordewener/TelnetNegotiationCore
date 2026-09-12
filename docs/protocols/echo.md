# ECHO (RFC 857)

`EchoProtocol` negotiates option 1 — which side puts the characters a user types back on their screen.
A server that takes `WILL ECHO` is telling the client to stop echoing locally and to expect the server
to do it, which is how a password prompt hides what is typed: the server simply stops echoing for the
duration.

```csharp
.AddPlugin<EchoProtocol>()
    .OnEchoStateChanged(echoing => { _localEcho = !echoing; return ValueTask.CompletedTask; })
```

`IsEchoing` is the same fact as a property. `EnableEchoAsync()` and `DisableEchoAsync()` drive the
negotiation by hand — the pair a server calls around a password prompt.

**Nothing is echoed for you unless you ask.** A server that agreed to echo still has to write the
bytes back, and what to write is an application decision — a password prompt echoes nothing, a line
editor echoes something other than what arrived. For the plain case, `UseDefaultEchoHandler()` echoes
each received byte back verbatim; `WithEchoHandler((b, encoding) => ...)` replaces it with your own.
