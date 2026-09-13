# Security policy

## Supported versions

The latest released version is supported. Fixes go into the next release rather than into patches of
older ones.

| Version | Supported |
|---|---|
| 3.0.x | yes |
| 2.18.x | security fixes only |
| < 2.18 | no |

## Reporting a vulnerability

Report privately through GitHub:
[**open a security advisory**](https://github.com/HarryCordewener/TelnetNegotiationCore/security/advisories/new).
That reaches the maintainers without the report being public, and is the only channel to use for
something exploitable. Please do not open a public issue for it.

Expect an acknowledgement within a week. What happens next depends on what it is: a fix with a
released version, an explanation of why it is not a vulnerability, or a question if the report needs
more to reproduce. You will be credited in the advisory unless you would rather not be.

Anything that is not a vulnerability — a protocol misread, a callback that fires at the wrong time, a
negotiation that stalls — belongs in a public
[issue](https://github.com/HarryCordewener/TelnetNegotiationCore/issues), where it is easier to
discuss.

## What counts

This library reads bytes from a peer that is, by construction, not trusted: a stranger connecting to
your server, or a server your client dialled. Everything it parses is attacker-controlled. Reports
worth making privately include:

- **Memory exhaustion.** An input that makes the interpreter accumulate without bound. Every
  accumulator is supposed to have a ceiling — see
  [Limits on untrusted input](docs/concepts/limits.md) — so an unbounded one is a bug in this
  library, not a tuning question.
- **Escape from the negotiation stream.** Protocol bytes reaching `OnSubmit` as if a user had typed
  them, or user input being read as protocol. Both sides of that boundary matter: an
  [MCP](docs/protocols/mcp.md) message that a player can forge by typing, or a subnegotiation whose
  payload is delivered as a line.
- **A parser that can be made to crash or hang** the byte-processing loop, rather than dropping the
  message and carrying on.
- **Anything that leaks the host.** This library sends no environment variable, user name or terminal
  it was not explicitly given — see [ENVIRON](docs/protocols/environ.md) and
  [NEW-ENVIRON](docs/protocols/new-environ.md). Something that does is a report.
- **A supply-chain problem in what is published**: the package contents, the release workflow, or the
  provenance attestation.

## What is out of scope

- **Telnet is a cleartext protocol.** RFC 854 has no confidentiality and no integrity; anything on the
  path can read and rewrite the session. That is the protocol, not a defect in this implementation.
  Put TLS underneath it if that matters.
- **`AuthenticationProtocol` (RFC 2941) and `EncryptionProtocol` (RFC 2946) are negotiation
  frameworks, not cryptography.** This library carries the messages; your callbacks implement the
  mechanism. RFC 2946's own algorithms — DES, 3DES, CAST — are long broken, and nothing here
  implements them. A report that "the DES mode is weak" is a fact about RFC 2946.
- **MCCP inflates without a ratio ceiling of its own.** A compression bomb is bounded downstream, by
  the line-buffer and subnegotiation limits every inflated byte then passes through, rather than at
  the inflater. If you have a case where that downstream bound does not hold, that *is* a report.

## How releases are made

There are no long-lived publishing keys. Releases are built and published by the
[release workflow](.github/workflows/release.yml) from a tag whose commit is an ancestor of `main`,
pushed to nuget.org through
[trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), and carry
an
[attestation](https://docs.github.com/en/actions/security-guides/using-artifact-attestations-to-establish-provenance-for-builds)
of the workflow and commit that built them.

Verify a downloaded package with the GitHub CLI:

```bash
gh attestation verify TelnetNegotiationCore.3.0.0.nupkg --repo HarryCordewener/TelnetNegotiationCore
```
