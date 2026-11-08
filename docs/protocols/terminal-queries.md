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
| Device attributes (DA1) | `CSI c`, always last | `CSI ? n ; … c` | `DeviceAttributes`, `Sixel` (family-aware; see below) |

Terminals answer in the order they are asked, and every terminal answers DA1, so the device
attributes are what settle the Kitty question: an answer to the query image before them means
`KittyGraphics` is true, none means false. `KittyGraphics` stays null when the probe did not ask.

**Nothing is asked unless you call `ProbeAsync` or opt into `ProbeOnStart`.** A MUD client that is not a terminal emulator may
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

## Interpreting sixel support

DA1 starts with a device family, not a feature. Following
[xterm's Primary DA reference](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html),
`Sixel` reads feature 4 only after a VT220-or-later family identifier (62, 63, 64 or 65).
For example, `CSI ? 62 ; 4 ; 22 c` reports true, and `CSI ? 62 ; 22 c` reports false.

VT100-class families 1 (VT100/VT101), 4 (VT132), 6 (VT102) and 7 (VT131) report false.
Their option numbers are not independent feature codes: `CSI ? 1 ; 4 c` does not advertise
sixel, nor does the family identifier in `CSI ? 4 ; 6 c`.

Missing or empty attributes and other families report null (unknown); their parameters are preserved
in `DeviceAttributes`. This includes the VT125's family 12, whose older graphics/options report is
not interpreted here. Applications should send sixel only when `Sixel == true`.

This distinction also matches real emulators: [xterm.js's default DA1 response](https://github.com/xtermjs/xterm.js/blob/master/src/common/InputHandler.ts)
is `CSI ? 1 ; 2 c`, while [mintty's VT220–VT525 responses](https://github.com/mintty/mintty/blob/master/src/termout.c)
use families 62–65 and include the sixel feature code.

## Initial connection negotiation

Inject a configured plugin to send the probes alongside the initial protocol offers:

```csharp
.AddPlugin(new TerminalQueryProtocol()
    .ProbeOnStart(TerminalQueries.All)
    .OnTerminalReport(report => SaveReportAsync(report)))
```

This is server-side and opt-in. It sends queries during the interpreter's initial negotiation,
when the plugins and input filters are ready. `AnnounceSupportAsync` also reruns the startup hooks. Disabled terminal-query plugins skip their
startup probe; re-enabling the plugin allows subsequent announcements to probe again.
Client identity and graphics preference remain application policy.

For MXP, use the existing `.AddPlugin<MXPProtocol>().QuerySupportOnStart("IMAGE")`;
it sends SUPPORT only after MXP mode starts, rather than sending a tag before the client agrees.

After building the interpreter, start feeding transport input, then optionally delay the welcome:

```csharp
var ended = await interpreter.WaitForInitialNegotiationAsync(
    TimeSpan.FromSeconds(1), cancellationToken);
```

The wait ends at the deadline, on a complete application line after protocol filtering, or when the
interpreter is disposed. Caller cancellation returns `InitialNegotiationEnd.Cancelled`. The host must stop startup on
`Cancelled` or `ConnectionClosed` rather than register the connection. It does not pause
input processing, wait for a specific plugin, or prove every capability exchange is complete.
A silent client remains unknown; later replies still update the plugins. The host decides when to
register the connection and emit its welcome, and must keep receiving bytes during this window.

The returned `ValueTask<InitialNegotiationEnd>` should be awaited once. Convert it with `AsTask()`
if it must be shared or passed to task combinators. Zero-duration and already-finished windows
complete synchronously without allocating a timer or linked cancellation source.

For clients with automatic login, keep the full window open even when a command arrives:

```csharp
var ended = await interpreter.WaitForInitialNegotiationAsync(
    TimeSpan.FromMilliseconds(400), cancellationToken, endOnApplicationInput: false);
```

This also ignores application input received before the wait starts. Input callbacks still run:
queue commands in the host until registration is ready. Cancellation and disposal still end the
wait early. The original overload continues ending on application input, including calls that pass
`default` as their second argument.
