# Terminal queries

`TerminalQueryProtocol` asks the terminal behind a telnet connection what it can draw. These are not
telnet options: they are escape sequences written into the data stream, and the terminal answers in
its own input. The plugin recognises complete replies in application data immediately, without waiting
for a newline. It removes them before both byte callbacks and `OnSubmit`, then invokes
`OnTerminalReport` once per answer. A submitted line that held nothing else is consumed.

```csharp
.AddPlugin<TerminalQueryProtocol>()
    .OnTerminalReport(report => RecordAsync(report))

// When your application decides to:
await queries.ProbeAsync(TerminalQueries.All);
```

| Question | Sent | Answer | Reported as |
|---|---|---|---|
| Kitty graphics | `ESC _ G i=31,s=1,v=1,a=q,t=d,f=24;AAAA ESC \` | any `ESC _ G … ESC \` | `KittyGraphics` |
| Cell size | `CSI 16 t` | `CSI 6 ; height ; width t` | `CellWidth`, `CellHeight` |
| Version (XTVERSION) | `CSI > 0 q` | `ESC P > \| text ESC \` | `Version` |
| Device attributes (DA1) | `CSI c`, always last | `CSI ? n ; … c` | `DeviceAttributes`, `Sixel` (4 among them) |

Terminals answer in the order they are asked, and every terminal answers DA1, so the device
attributes are what settle the Kitty question: an answer to the query image before them means
`KittyGraphics` is true, none means false. `KittyGraphics` stays null when the probe did not ask.

**Nothing is asked unless you call `ProbeAsync`.** A MUD client that is not a terminal emulator may
print the questions as text. A telnet client in line mode (the usual case for MUDs) hands the
terminal's answer to the local line discipline, which echoes it on the user's screen before they press
Enter. Ask when the terminal type names a terminal emulator, or when the user asks you to.

Only the answers to these questions are removed. Any other escape sequence in a line is the user's and
is delivered as it arrived. `ProbeAsync` throws in client mode: a client's peer is a server, which has
no terminal.


Replies can span network reads or telnet negotiations. Unfinished sequences are held until another
byte resolves them or a line/prompt boundary arrives, when they are released unchanged. CAN/SUB cancels a
pending answer and preserves its bytes. Pending sequences are limited to 64 KiB (or `MaxBufferSize`,
if smaller); longer sequences pass through unchanged until their terminator. Unknown control strings
are kept whole so answer-shaped text inside them is never extracted. The existing seven-bit `ESC`
forms and BEL string-terminator compatibility remain supported; raw eight-bit C1 bytes are not
interpreted as controls, which avoids mistaking UTF-8 continuation bytes for terminal replies.

A line-mode client can still hold the reply locally until Enter. Incremental recognition starts when
the client sends the bytes; it cannot bypass that client-side buffering.
