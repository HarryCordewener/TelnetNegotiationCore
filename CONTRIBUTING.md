# Contributing

Issues and pull requests are welcome. Nothing here is heavy — it is mostly what you need to know to
get the build green.

## Building

The package targets `netstandard2.0`, `net8.0`, `net10.0` and `net11.0`, and **every target is built
with the .NET 11 SDK**, which [`global.json`](global.json) pins. You need that SDK (RC1 or later)
installed; the .NET 8 and .NET 10 runtimes are what the tests for those targets run on.

```bash
dotnet restore
dotnet build
```

## Testing

Tests use [TUnit](https://github.com/thomhurst/TUnit), which runs as an executable rather than through
`dotnet test`'s VSTest host, so a run names the framework:

```bash
dotnet run --project TelnetNegotiationCore.UnitTests --framework net11.0
```

CI runs that for `net8.0`, `net10.0` and `net11.0`. A change that touches the interpreter or a plugin
should come with a test — this library exists to be testable, and a protocol claim without one is a
claim.

There are two runnable samples to try a change against by hand:
[`TelnetNegotiationCore.TestServer`](TelnetNegotiationCore.TestServer) (Kestrel) and
[`TelnetNegotiationCore.TestClient`](TelnetNegotiationCore.TestClient).

## Adding a protocol

A telnet option is a plugin: derive from `TelnetProtocolPluginBase`, declare `ProtocolType`,
`ProtocolName` and `Dependencies`, and configure the state machine in `ConfigureStateMachine`. The
[analyzer](TelnetNegotiationCore.Analyzers/README.md) enforces the parts that are easy to get wrong,
at compile time.

A new protocol needs a page in [`docs/protocols/`](docs/protocols) and a row in
[`docs/protocols/index.md`](docs/protocols/index.md). Cite the specification, and say plainly where
the implementation departs from it or where the specification is silent — several pages here do, and
that is the most useful thing on them.

## Pull requests

- Branch from `main`. CI must be green: build, tests on all three runtimes, dependency review and
  CodeQL.
- Add a `CHANGELOG.md` entry at the top for anything a consumer would notice.
- Keep the diff legible. A behaviour change and a reformat in one commit is two reviews.

## Security

Do not open a public issue for something exploitable — see [SECURITY.md](SECURITY.md).
