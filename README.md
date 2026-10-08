<p align="center">
  <img src="docs/images/telnet-negotiation-core-header.png" alt="Telnet Negotiation Core — a terminal and a MUD exchange negotiated capabilities" width="100%">
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/TelnetNegotiationCore"><img alt="NuGet version" src="https://img.shields.io/nuget/v/TelnetNegotiationCore?style=flat-square&logo=nuget&logoColor=white&color=1683b5"></a>
  <a href="https://www.nuget.org/packages/TelnetNegotiationCore"><img alt="NuGet downloads" src="https://img.shields.io/nuget/dt/TelnetNegotiationCore?style=flat-square&color=1683b5"></a>
  <a href="https://github.com/HarryCordewener/TelnetNegotiationCore/actions/workflows/dotnet.yml"><img alt="Build status" src="https://img.shields.io/github/actions/workflow/status/HarryCordewener/TelnetNegotiationCore/dotnet.yml?branch=main&style=flat-square&label=build"></a>
  <a href="https://scorecard.dev/viewer/?uri=github.com/HarryCordewener/TelnetNegotiationCore"><img alt="OpenSSF Scorecard" src="https://img.shields.io/ossf-scorecard/github.com/HarryCordewener/TelnetNegotiationCore?style=flat-square&label=scorecard"></a>
  <a href="LICENSE"><img alt="Apache 2.0 license" src="https://img.shields.io/github/license/HarryCordewener/TelnetNegotiationCore?style=flat-square"></a>
  <a href="https://discord.gg/SK2cWERJF7"><img alt="Discord" src="https://img.shields.io/discord/1193672869104861195?style=flat-square&logo=discord&logoColor=white&label=discord"></a>
</p>

<h1 align="center">Telnet Negotiation Core</h1>

<p align="center">
  A composable, testable Telnet client and server library for .NET, built with MUDs in mind.
</p>

<p align="center">
  <a href="docs/index.md">Documentation</a> ·
  <a href="docs/guides/getting-started.md">Getting started</a> ·
  <a href="docs/protocols/index.md">Protocol reference</a> ·
  <a href="CHANGELOG.md">Changelog</a>
</p>

```bash
dotnet add package TelnetNegotiationCore
```

## Why Telnet Negotiation Core?

Telnet is a byte-stream protocol with stateful negotiation layered throughout the connection. This
library handles that machinery while leaving application behavior in your hands.

- **Composable protocols** — opt into each Telnet option as a plugin or register the common MUD set.
- **Client and server support** — build either side over `IDuplexPipe`, `TcpClient`, or `Stream`.
- **Predictable I/O** — a state machine interprets incoming bytes and outgoing writes are serialized.
- **Modern .NET integration** — fluent configuration, dependency injection, logging, analyzers, and source generators.
- **Built for verification** — protocol behavior is isolated and testable down to exact wire bytes.

## Quick start

`BuildAndStartAsync` wires the connection and starts the read loop. This server example adds window
size, GMCP, and server-status negotiation to a Kestrel connection:

```csharp
var (telnet, readTask) = await new TelnetInterpreterBuilder()
    .UseMode(TelnetInterpreter.TelnetMode.Server)
    .UseLogger(logger)
    .OnSubmit(HandleSubmitAsync)
    .AddPlugin<NAWSProtocol>()
        .OnNAWS(HandleWindowSizeAsync)
    .AddPlugin<GMCPProtocol>()
        .OnGMCPMessage(HandleGMCPAsync)
    .AddPlugin<MSSPProtocol>()
        .WithMSSPConfig(() => new MSSPConfig { Name = "My Server", UTF_8 = true })
    .BuildAndStartAsync(connection.Transport);

await readTask; // Completes when the connection closes.
```

`AddDefaultMUDProtocols()` registers the common MUD protocol set in one call. Use
`AddTelnetServer()` or `AddTelnetClient()` to register the interpreter with a dependency-injection
container. The [getting-started guide](docs/guides/getting-started.md) walks through a complete
connection.

## Supported protocols

Each row is a plugin you register, and one page in
[the protocol reference](docs/protocols/index.md).

| Specification | | Specification | |
| --- | --- | --- | --- |
| [RFC 855](https://www.rfc-editor.org/rfc/rfc855) | Telnet Option Specification | [MSSP](https://tintin.mudhalla.net/protocols/mssp) | Mud Server Status Protocol |
| [RFC 857](https://www.rfc-editor.org/rfc/rfc857) | Echo | [MSDP](https://tintin.mudhalla.net/protocols/msdp) | Mud Server Data Protocol |
| [RFC 858](https://www.rfc-editor.org/rfc/rfc858) | Suppress Go-Ahead | [GMCP](https://tintin.mudhalla.net/protocols/gmcp) | Generic Mud Communication Protocol |
| [RFC 885](https://www.rfc-editor.org/rfc/rfc885) | End Of Record | [EOR](https://tintin.mudhalla.net/protocols/eor) | End Of Record |
| [RFC 1073](https://www.rfc-editor.org/rfc/rfc1073) | Window Size (NAWS) | [MTTS](https://tintin.mudhalla.net/protocols/mtts) | Terminal type capabilities |
| [RFC 1079](https://www.rfc-editor.org/rfc/rfc1079) | Terminal Speed | [MNES](https://tintin.mudhalla.net/protocols/mnes) | Mud New Environment Standard |
| [RFC 1091](https://www.rfc-editor.org/rfc/rfc1091) | Terminal Type | [MCCP](https://tintin.mudhalla.net/protocols/mccp) | Compression, 2 and 3 |
| [RFC 1096](https://www.rfc-editor.org/rfc/rfc1096) | X-Display Location | [MXP](https://www.zuggsoft.com/zmud/mxp.htm) | MUD eXtension Protocol (option 91) |
| [RFC 1184](https://www.rfc-editor.org/rfc/rfc1184) | Line Mode (MODE) | [MCP 2.1](https://www.moo.mud.org/mcp/mcp2.html) | MUD Client Protocol, with cords |
| [RFC 1372](https://www.rfc-editor.org/rfc/rfc1372) | Flow Control | [RFC 1950](https://www.rfc-editor.org/rfc/rfc1950) | ZLIB compression |
| [RFC 1408](https://www.rfc-editor.org/rfc/rfc1408) | Environment | [RFC 2066](https://www.rfc-editor.org/rfc/rfc2066) | Charset, including TTABLE |
| [RFC 1572](https://www.rfc-editor.org/rfc/rfc1572) | New Environment | [RFC 2941](https://www.rfc-editor.org/rfc/rfc2941) | Authentication |
| | | [RFC 2946](https://www.rfc-editor.org/rfc/rfc2946) | Encryption |
| | | [Pueblo](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/docs/protocols/pueblo.md) | Pueblo handshake (in-band, no option) |
| | | [Terminal queries](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/docs/protocols/terminal-queries.md) | Kitty graphics, sixel, cell size and version, asked of the terminal (in-band, no option) |

Being a telnet *negotiation* library, it does not render the content layers that ride on top: ANSI,
Pueblo, and MXP's own tags are the host application's to draw. MXP is negotiated and Pueblo is
handshaken; neither's markup is parsed.

## Project status

Stable. The package targets `netstandard2.0`, `net8.0`, `net10.0` and `net11.0`. Building from source
needs the .NET 11 SDK (RC1 or later), which `global.json` pins.

Releases are published from GitHub Actions by
[trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) — no
long-lived key exists — and carry an attestation of the workflow and commit that built them:

```bash
gh attestation verify TelnetNegotiationCore.3.0.0.nupkg --repo HarryCordewener/TelnetNegotiationCore
```

## Contributing

Issues and pull requests are welcome — see
[CONTRIBUTING.md](CONTRIBUTING.md)
for how to build and test, and
[SECURITY.md](SECURITY.md) for
anything that should not be public.

## License

[Apache-2.0](LICENSE).
