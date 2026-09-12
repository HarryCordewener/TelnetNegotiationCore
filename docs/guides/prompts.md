# Detecting prompts

A "prompt" is the point where a server has stopped talking and is waiting on the user — `HP:100>`,
`Enter your name:`, that sort of thing — and telnet gives three ways to notice one, all of them
optional and all of them routed to the same `.OnPrompt(() => ...)` callback:

- **`EORProtocol`** — the peer negotiates and then sends `IAC EOR` (RFC 885) at the end of each
  prompt. The clearest signal, when a server offers it.
- **`SuppressGoAheadProtocol`** — the default NVT (no options negotiated) ends every prompt with a
  bare `IAC GA`, RFC 854's own Go-Ahead. A client always accepts a peer's `SUPPRESS-GO-AHEAD` offer
  (RFC 1123 §3.2.2), which stops the marker; `PacketPatchProtocol` is the fallback when that happens.
- **`PacketPatchProtocol`** — for the many MUD servers that send neither of the above and simply
  leave an unterminated fragment sitting at the end of their output. Modelled on Mudlet's posting
  timer and TinTin++'s `#config {PACKET PATCH}`: if nothing more arrives within `HoldTime` (500 ms by
  default, `.WithHoldTime(...)` to change it, `[0, 10s]`), the held fragment is reported as a prompt.
  It is a guess, and retires itself permanently the first time either of the other two sources fires
  on a connection — a server that marks its own prompts is never second-guessed. Because it is a
  guess and not a marker, `AddDefaultMUDProtocols` only adds it when given an `onPrompt` callback (see
  above); add it directly with `.AddPlugin<PacketPatchProtocol>()` to use it standalone.

All three take the line buffer's standing partial text at the boundary before invoking the callback,
so a prompt is never glued onto the next line the way it would be if nothing drained the buffer. The
callback carries no payload, so a consumer that already accumulates bytes itself (via
`TelnetInterpreter.CallbackOnByteAsync`) usually already has the text; for the consumer that does
not, `TelnetInterpreter.LastPromptBytes` holds exactly what was taken, in the encoding it arrived in,
valid to read from inside the `.OnPrompt` callback itself. A plugin implementing a fourth prompt
source would call `TelnetInterpreter.TakePartialLineAsPrompt(marked: true)` (via
`IProtocolContext.Interpreter`) at the point its own marker is recognised — see its remarks for the
invariants it maintains and why it must run on the byte-processing loop.

