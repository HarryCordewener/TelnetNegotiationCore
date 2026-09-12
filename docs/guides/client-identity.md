# Saying who your application is

A client introduces itself down two channels — TTYPE (where MTTS defines the first response as the
*client name*) and MNES `CLIENT_NAME` — and both are the same fact, so you set it once:

```csharp
.WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER")
{
    Version = "1.2.0",                                     // MNES CLIENT_VERSION
    TerminalType = "XTERM",                                // 2nd TTYPE response, MNES TERMINAL_TYPE
    Mtts = MttsCapabilities.Ansi | MttsCapabilities.Truecolor
})
```

With that, TTYPE answers `MUINDEX-CRAWLER`, then `XTERM`, then `MTTS <bitvector>`, and a server that
negotiates NEW-ENVIRON receives `CLIENT_NAME`, `CLIENT_VERSION`, `TERMINAL_TYPE` and `MTTS`.

**Configure nothing and the library names nobody.** TTYPE answers `UNKNOWN` — RFC 1091's own word for
a terminal that will not name itself — and NEW-ENVIRON sends no variables at all. It will never
introduce your application as `TNC`, and it will never invent a terminal for it.

**The MTTS bitvector is calculated, not stated.** `Mtts` is only for the claims this library cannot
check: colour depth, mouse tracking, a screen reader — things it does not render and cannot see. The
bits it *can* see, it sets for you, and only when they are true:

| Bit | Set when |
| --- | --- |
| `Utf8` (4) | the interpreter is decoding UTF-8 (the default, until RFC 2066 CHARSET says otherwise) |
| `Mnes` (512) | a `NewEnvironProtocol` plugin is registered, so this connection really will answer MNES |

Your claim and the observed bits are OR-ed together, so a client that renders ANSI and truecolour and
has NEW-ENVIRON registered reports `MTTS 773`. An application that would rather state the whole TTYPE
list itself can bypass all of this:

```csharp
.AddPlugin<TerminalTypeProtocol>()
    .WithTerminalTypes("MUINDEX-CRAWLER", "MUINDEX", "MTTS 9")   // sent verbatim, in this order
```

