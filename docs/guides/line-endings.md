# Line endings and carriage returns

Telnet's end-of-line rules are older than every convention you are used to, and implementations
disagree about them in a way that matters on the wire. This page says what this library does, what
you can change, and why the choice exists.

## What never changes

`CR LF` ends a line. A bare `LF` ends a line. Both submit whatever text has accumulated, through the
callback you gave `OnSubmit`, and neither terminator is part of the delivered text.

That covers almost all traffic. The setting below exists for the rest.

## What `CR NUL` means, and why it is a choice

RFC 854 says a carriage return that is not followed by `LF` must be sent as `CR NUL`, so that a bare
carriage return can be distinguished from a line ending. Two standards then read the result two
different ways, and both are right about their own case:

- **RFC 1123 §3.3.1**, on a server reading what a user typed: "CR LF and CR NUL MUST have the same
  effect on an ASCII server host when received as input." It ends the line. RFC 1123 also requires a
  client to be *able* to send `CR LF`, `CR NUL` or bare `LF`, and some do send `CR NUL` — BusyBox's
  telnet appends a `NUL` after every `CR`.
- **RFC 854**, and libtelnet's `TELNET_FLAG_NVT_EOL`, on the data stream generally: `CR NUL` carries
  a literal carriage return.

There is no single answer that suits a MUD client reading a server's animation frames *and* a server
reading a line a user typed. So it is a setting.

## The three modes

```csharp
var interpreter = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(OnLine)
    .OnNegotiation(WriteToNetwork)
    .TreatCarriageReturnNullAsLineEnd()
    .BuildAsync();
```

`BuildAsync` requires the mode, the logger and both callbacks, so they are shown here even though
only the last line is about carriage returns. See
[Getting started](getting-started.md) for what each of them is for.

| Method | Mode | A `CR` not followed by `LF` |
| --- | --- | --- |
| `DropCarriageReturns()` | `Drop` (default) | Discarded. A `NUL` after it is consumed too. |
| `TreatCarriageReturnNullAsLineEnd()` | `EndOfLine` | `CR NUL` ends the line. Other carriage returns are discarded. |
| `PreserveCarriageReturns()` | `Preserve` | Delivered as a literal `CR` inside the line's text. |

`WithCarriageReturnMode(CarriageReturnMode)` takes the enum directly if you are choosing at runtime.

**Which to pick.** Leave it alone unless you have a reason — `Drop` is right for a line-oriented
consumer, which is most of them. Choose `EndOfLine` for a server that accepts connections from
arbitrary telnet clients, because RFC 1123 requires it and a client sending `CR NUL` for its
end-of-line key would otherwise never complete a line. Choose `Preserve` when the peer's carriage
returns carry meaning.

## A carriage return at the end of a batch

A carriage return still pending when the input stops contributes nothing, in every mode including
`Preserve`. Until the next byte arrives the machine cannot know whether it is looking at `CR LF`,
`CR NUL` or a data carriage return, and resolving it at the end of a batch would make the same bytes
parse differently depending on where the peer happened to stop sending — a read boundary would change
the meaning of the stream. It reappears as soon as the next byte does.

## `Preserve` and overprint animation

MUDs do use bare carriage returns for effects — spinners, progress bars, anything that overwrites the
line it is on. `Preserve` is what stops this library from discarding those bytes.

Be clear about what it gives you, though: **this library's consumer surface is line-oriented.** A
submitted line arrives complete, so a preserved carriage return reaches you *inside the line's text*,
not as a cursor movement at the moment it arrived. If you are rendering a terminal, you get the bytes
and the interpretation is yours. If you need character-at-a-time output, `OnByte` is the callback to
look at rather than `OnSubmit`.

## Outbound

There are three ways to send, and each frames the bytes differently. None of them translates line
endings inside the bytes you pass.

| Method | IAC (0xFF) in your bytes | What is appended |
| --- | --- | --- |
| `SendAsync(byte[])` | Doubled | `CR LF`, always |
| `SendPromptAsync(byte[])` | Doubled | The prompt marker this end has negotiated (below) |
| `WriteToNetworkAsync(ReadOnlyMemory<byte>)` | Left alone | Nothing |

**`SendAsync` terminates the line for you.** Pass the line without a terminator. The `CR LF` is
added whether or not the bytes already end in one, so a pre-terminated line arrives with a blank
line after it:

```csharp
await telnet.SendAsync(Encoding.UTF8.GetBytes("hello"));     // hello\r\n
await telnet.SendAsync(Encoding.UTF8.GetBytes("hello\r\n")); // hello\r\n\r\n  (a blank line follows)
await telnet.SendAsync([]);                                  // \r\n
```

Line breaks inside the text go out as you wrote them. `"one\r\ntwo"` sends two lines; `"one\ntwo"`
sends a bare `LF` between them, which most MUD clients treat as a line break although RFC 854's
end of line is `CR LF`.

**`SendPromptAsync` ends with a prompt marker instead of a line ending**, so a client can keep the
prompt on its input line. The marker is `IAC EOR` if this end has agreed to send End of Record
(the peer sent `DO EOR`). Without EOR it is `IAC GA`, unless this end has also agreed to suppress
its own Go-Ahead (the peer sent `DO SUPPRESS-GO-AHEAD`); then nothing marks a prompt, and it ends
with `CR LF` like a line.

**`WriteToNetworkAsync` is the raw write** that both of the above, and every negotiation, go
through. It serializes writes and applies the outbound transform (MCCP compression, when that is
on), but it does not escape IAC and does not frame anything. Use it for bytes that are already
telnet: a negotiation you built yourself, or text you have escaped and terminated.

## Related

- [RFC 854](https://www.rfc-editor.org/rfc/rfc854.html) — the NVT, and the `CR NUL` rule
- [RFC 1123 §3.3.1](https://www.rfc-editor.org/rfc/rfc1123.html) — the end-of-line convention for hosts
- [Detecting prompts](prompts.md) — a prompt has no line terminator at all, which is a separate problem
