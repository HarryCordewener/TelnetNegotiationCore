# Protocols

Every telnet option is a plugin, and a plugin you do not register is an option this connection answers
`WONT` / `DONT` to. `AddDefaultMUDProtocols()` registers the nine a MUD usually wants — NAWS, GMCP,
MSDP, MSSP, TTYPE, CHARSET, EOR, SUPPRESS-GO-AHEAD and MXP — in one call.

| RFC / specification | Plugin | Page |
| --- | --- | --- |
| [RFC 855](http://www.faqs.org/rfcs/rfc855.html) Telnet Option Specification | *(the interpreter itself)* | [Getting started](../guides/getting-started.md) |
| [RFC 1091](http://www.faqs.org/rfcs/rfc1091.html) Terminal Type, and [MTTS](https://tintin.mudhalla.net/protocols/mtts) | `TerminalTypeProtocol` | [TTYPE and MTTS](terminal-type.md) |
| [RFC 1073](http://www.faqs.org/rfcs/rfc1073.html) Window Size (NAWS) | `NAWSProtocol` | [NAWS](naws.md) |
| [GMCP](https://tintin.mudhalla.net/protocols/gmcp) | `GMCPProtocol` | [GMCP](gmcp.md) |
| [MSSP](https://tintin.mudhalla.net/protocols/mssp) | `MSSPProtocol`, `MSSPPlaintextProtocol` | [MSSP](mssp.md) |
| [MSDP](https://tintin.mudhalla.net/protocols/msdp) | `MSDPProtocol` | [MSDP](msdp.md) |
| [RFC 885](http://www.faqs.org/rfcs/rfc885.html) / [EOR](https://tintin.mudhalla.net/protocols/eor) End Of Record | `EORProtocol` | [EOR and SGA](eor.md) |
| [RFC 858](http://www.faqs.org/rfcs/rfc858.html) Suppress Go-Ahead | `SuppressGoAheadProtocol` | [EOR and SGA](eor.md) |
| [RFC 2066](http://www.faqs.org/rfcs/rfc2066.html) Charset, including TTABLE | `CharsetProtocol` | [CHARSET](charset.md) |
| [RFC 1572](http://www.faqs.org/rfcs/rfc1572.html) New Environment, and [MNES](https://tintin.mudhalla.net/protocols/mnes) | `NewEnvironProtocol` | [NEW-ENVIRON and MNES](new-environ.md) |
| [RFC 1408](http://www.faqs.org/rfcs/rfc1408.html) Environment | `EnvironProtocol` | [ENVIRON](environ.md) |
| [MCCP](https://tintin.mudhalla.net/protocols/mccp) 2 and 3, over [RFC 1950](https://tintin.mudhalla.net/rfc/rfc1950) zlib | `MCCPProtocol` | [MCCP](mccp.md) |
| [RFC 857](http://www.faqs.org/rfcs/rfc857.html) Echo | `EchoProtocol` | [ECHO](echo.md) |
| [RFC 1079](http://www.faqs.org/rfcs/rfc1079.html) Terminal Speed | `TerminalSpeedProtocol` | [Terminal speed](terminal-speed.md) |
| [RFC 1372](http://www.faqs.org/rfcs/rfc1372.html) Flow Control | `FlowControlProtocol` | [Flow control](flow-control.md) |
| [RFC 1184](http://www.faqs.org/rfcs/rfc1184.html) Line Mode (MODE) | `LineModeProtocol` | [Line mode](line-mode.md) |
| [RFC 1096](http://www.faqs.org/rfcs/rfc1096.html) X-Display Location | `XDisplayProtocol` | [X-Display Location](x-display.md) |
| [RFC 2941](http://www.faqs.org/rfcs/rfc2941.html) Authentication | `AuthenticationProtocol` | [Authentication](authentication.md) |
| [RFC 2946](http://www.faqs.org/rfcs/rfc2946.html) Encryption | `EncryptionProtocol` | [Encryption](encryption.md) |
| [MXP](https://www.zuggsoft.com/zmud/mxp.htm) (telnet option 91) | `MXPProtocol` | [MXP](mxp.md) |
| [MCP 2.1](https://www.moo.mud.org/mcp/mcp2.html) | `MudClientProtocol`, `McpCordProtocol` | [MCP](mcp.md) |
| *(no option — silence-inferred prompts)* | `PacketPatchProtocol` | [Detecting prompts](../guides/prompts.md) |

## What is not here

Being a telnet negotiation library, this one does not implement the **content** layers that ride on
top of a negotiated option: ANSI, Pueblo, and MXP's own tags and entities are the host application's
to render. MXP is negotiated (option 91 and its start marker); its markup is not parsed.

RFC 1184's SLC (Set Local Characters) and FORWARDMASK subnegotiations are not implemented — the
plugin covers MODE, which is the part in common use.

RFC 860 TIMING-MARK is not implemented, which is worth knowing if you want peer-liveness detection
rather than the [keep-alive](../guides/keep-alive.md)'s `IAC NOP`.
