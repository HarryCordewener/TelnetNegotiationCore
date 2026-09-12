# TelnetNegotiationCore documentation

A testable implementation of telnet and as many of its RFCs as are viable, written with MUDs in mind.
Every option is a plugin; the interpreter is a state machine over the byte stream; all outgoing writes
are serialized for you.

```bash
dotnet add package TelnetNegotiationCore
```

## Start here

- **[Getting started](guides/getting-started.md)** — the builder, one connection, end to end.
- **[Protocols](protocols/index.md)** — every RFC and MUD specification supported, and the plugin for it.
- **[Builder reference](reference/builder.md)** — every callback and setting in one list.

## Guides

| Page | What it covers |
|---|---|
| [Getting started](guides/getting-started.md) | `TelnetInterpreterBuilder`, `BuildAndStartAsync`, `AddDefaultMUDProtocols` |
| [Dependency injection](guides/dependency-injection.md) | `AddTelnetServer()` / `AddTelnetClient()`, Kestrel, `ITelnetInterpreterFactory` |
| [Writing a client](guides/client.md) | A `TcpClient` connection, wired up |
| [Writing a server](guides/server.md) | A Kestrel `ConnectionHandler`, and the MSDP server model |
| [Saying who your application is](guides/client-identity.md) | `ClientIdentity`, and the MTTS bits that are calculated rather than claimed |
| [Detecting prompts](guides/prompts.md) | EOR, Go-Ahead and Packet Patch — three sources, one callback |
| [Keep-alive](guides/keep-alive.md) | `IAC NOP` on an idle connection, and what it does not prove |
| [Managing the read loop yourself](guides/connection-management.md) | `UsePipe`, `UseStream`, `OnNegotiation`, `InterpretByteArrayAsync` |
| [Implementing an authentication mechanism](guides/authentication-mechanisms.md) | Kerberos, SRP, RSA and TLS on top of RFC 2941 |

## Concepts

- [Limits on untrusted input](concepts/limits.md) — what a peer can grow, and what happens at the ceiling.

## Reference

- [Builder reference](reference/builder.md) — every callback and setting.
- [Protocols](protocols/index.md) — one page per telnet option.
- [Changelog](../CHANGELOG.md) — every release.
- [Security policy](../SECURITY.md) — reporting a vulnerability, and how releases are signed.
