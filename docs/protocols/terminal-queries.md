# Terminal queries

`TerminalQueryProtocol` asks the terminal behind a telnet connection what it can draw. These are not
telnet options: they are escape sequences written into the data stream, and the terminal answers in
its own input. Over telnet that input is the user's, so the answers arrive with the user's next line,
in front of whatever they typed. The plugin takes them out of the line and reports them; a line that
held nothing else never reaches `OnSubmit`.

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

**Nothing is asked unless you call `ProbeAsync`.** A MUD client that is not a terminal emulator may
print the questions as text. A telnet client in line mode (the usual case for MUDs) hands the
terminal's answer to the local line discipline, which echoes it on the user's screen before they press
Enter. Ask when the terminal type names a terminal emulator, or when the user asks you to.

Only the answers to these questions are removed. Any other escape sequence in a line is the user's and
is delivered as it arrived. `ProbeAsync` throws in client mode: a client's peer is a server, which has
no terminal.

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

