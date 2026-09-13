<p align="center">
  <img src="https://raw.githubusercontent.com/HarryCordewener/TelnetNegotiationCore/main/docs/images/LargerLogo.png" alt="Telnet Negotiation Core" width="480">
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/TelnetNegotiationCore"><img alt="NuGet" src="https://img.shields.io/nuget/v/TelnetNegotiationCore?style=for-the-badge&logo=nuget&logoColor=white&color=blue"></a>
  <a href="https://www.nuget.org/packages/TelnetNegotiationCore"><img alt="Downloads" src="https://img.shields.io/nuget/dt/TelnetNegotiationCore?style=for-the-badge&color=blue"></a>
  <a href="https://github.com/HarryCordewener/TelnetNegotiationCore/actions/workflows/dotnet.yml"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/HarryCordewener/TelnetNegotiationCore/dotnet.yml?branch=main&style=for-the-badge&label=build"></a>
  <a href="https://scorecard.dev/viewer/?uri=github.com/HarryCordewener/TelnetNegotiationCore"><img alt="OpenSSF Scorecard" src="https://img.shields.io/ossf-scorecard/github.com/HarryCordewener/TelnetNegotiationCore?style=for-the-badge&label=scorecard"></a>
  <a href="https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/LICENSE"><img alt="License" src="https://img.shields.io/github/license/HarryCordewener/TelnetNegotiationCore?style=for-the-badge"></a>
  <a href="https://discord.gg/SK2cWERJF7"><img alt="Discord" src="https://img.shields.io/discord/1193672869104861195?style=for-the-badge&logo=discord&logoColor=white&label=discord"></a>
</p>

# Telnet Negotiation Core

A testable implementation of telnet, and as many of its RFCs as are viable, for .NET. Written with
MUDs in mind: every telnet option is a plugin you opt into, the interpreter is a state machine over
the byte stream, and every outgoing write is serialized for you.

```bash
dotnet add package TelnetNegotiationCore
```

- **[Documentation](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/docs/index.md)** — guides, concepts and reference.
- **[Protocols](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/docs/protocols/index.md)** — every RFC and MUD specification supported, one page each.
- **[Getting started](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/docs/guides/getting-started.md)** — one connection, end to end.
- **[Changelog](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/CHANGELOG.md)** · **[Security policy](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/SECURITY.md)** · **[Discord](https://discord.gg/SK2cWERJF7)**

## Quick start

`BuildAndStartAsync` wires the connection and starts the read loop. Pass an `IDuplexPipe` (Kestrel's
`connection.Transport`), a `TcpClient`, or a raw `Stream`:

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

await readTask;   // completes when the connection closes
```

`AddDefaultMUDProtocols()` registers the nine a MUD usually wants in one call, and
`AddTelnetServer()` / `AddTelnetClient()` register the whole thing with the DI container. See
[Getting started](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/docs/guides/getting-started.md).

## What it speaks

Each row is a plugin you register, and one page in
[the protocol reference](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/docs/protocols/index.md).

| Specification | | Specification | |
| --- | --- | --- | --- |
| [RFC 855](http://www.faqs.org/rfcs/rfc855.html) | Telnet Option Specification | [MSSP](https://tintin.mudhalla.net/protocols/mssp) | Mud Server Status Protocol |
| [RFC 857](http://www.faqs.org/rfcs/rfc857.html) | Echo | [MSDP](https://tintin.mudhalla.net/protocols/msdp) | Mud Server Data Protocol |
| [RFC 858](http://www.faqs.org/rfcs/rfc858.html) | Suppress Go-Ahead | [GMCP](https://tintin.mudhalla.net/protocols/gmcp) | Generic Mud Communication Protocol |
| [RFC 885](http://www.faqs.org/rfcs/rfc885.html) | End Of Record | [EOR](https://tintin.mudhalla.net/protocols/eor) | End Of Record |
| [RFC 1073](http://www.faqs.org/rfcs/rfc1073.html) | Window Size (NAWS) | [MTTS](https://tintin.mudhalla.net/protocols/mtts) | Terminal type capabilities |
| [RFC 1079](http://www.faqs.org/rfcs/rfc1079.html) | Terminal Speed | [MNES](https://tintin.mudhalla.net/protocols/mnes) | Mud New Environment Standard |
| [RFC 1091](http://www.faqs.org/rfcs/rfc1091.html) | Terminal Type | [MCCP](https://tintin.mudhalla.net/protocols/mccp) | Compression, 2 and 3 |
| [RFC 1096](http://www.faqs.org/rfcs/rfc1096.html) | X-Display Location | [MXP](https://www.zuggsoft.com/zmud/mxp.htm) | MUD eXtension Protocol (option 91) |
| [RFC 1184](http://www.faqs.org/rfcs/rfc1184.html) | Line Mode (MODE) | [MCP 2.1](https://www.moo.mud.org/mcp/mcp2.html) | MUD Client Protocol, with cords |
| [RFC 1372](http://www.faqs.org/rfcs/rfc1372.html) | Flow Control | [RFC 1950](https://tintin.mudhalla.net/rfc/rfc1950) | ZLIB compression |
| [RFC 1408](http://www.faqs.org/rfcs/rfc1408.html) | Environment | [RFC 2066](http://www.faqs.org/rfcs/rfc2066.html) | Charset, including TTABLE |
| [RFC 1572](http://www.faqs.org/rfcs/rfc1572.html) | New Environment | [RFC 2941](http://www.faqs.org/rfcs/rfc2941.html) | Authentication |
| | | [RFC 2946](http://www.faqs.org/rfcs/rfc2946.html) | Encryption |

Being a telnet *negotiation* library, it does not render the content layers that ride on top: ANSI,
Pueblo, and MXP's own tags are the host application's to draw. MXP is negotiated; its markup is not
parsed.

## State

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
[CONTRIBUTING.md](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/CONTRIBUTING.md)
for how to build and test, and
[SECURITY.md](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/SECURITY.md) for
anything that should not be public.

## License

[Apache-2.0](https://github.com/HarryCordewener/TelnetNegotiationCore/blob/main/LICENSE).
