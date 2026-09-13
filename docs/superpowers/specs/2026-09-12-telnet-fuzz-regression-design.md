# Property-based regression testing for the telnet engine

*2026-09-12*

## Why

`v3.0.0` is tagged at `18ff2da`. The unreleased delta is eight commits and roughly
5,100 changed lines under `TelnetNegotiationCore/`, of which the dominant piece is
[#107](https://github.com/HarryCordewener/TelnetNegotiationCore/pull/107) — the
migration of the negotiation engine from Stateless to StateAlchemist, which rewrote
the state machine of every protocol.

The suite grew by about 5,700 lines over the same window and 899 tests pass on
`net8.0`, `net10.0` and `net11.0`. That is a real result, but it is not regression
evidence for this particular delta, because those tests were written and updated
*alongside* the rewrite. They demonstrate that the new engine does what the new tests
say it does. The characteristic failure of a whole-engine swap is silent behavioural
drift on inputs nobody wrote a test for, and a suite of hand-written examples is
structurally unable to find those: it only ever asks the questions its author thought
to ask.

Property-based testing asks a different kind of question. Instead of "does this input
produce that output", it asserts an invariant over a generated space of inputs, and
reports the smallest input that violates it. That is the right instrument for a state
machine, because a state machine's invariants are simple to state and its input space
is far too large to enumerate.

## What already exists

The migration left behind, almost incidentally, precisely the seam this needs:

- **`TelnetCoreMachine(context)`** with `StartAsync()` and `FireAsync(bytes)` drives raw
  bytes through the engine synchronously, with no network and no plugin stack.
- **`RecordingTelnetContext`** (`TelnetNegotiationCore.UnitTests`, 446 lines) implements
  every abstract member of `TelnetCoreContext` by recording what it was called with —
  60-odd properties spanning every protocol module.
- **`MalformedSubnegotiationRecoveryTests`** established the correct oracle shape: feed
  malformed input, then assert a following `"hello\r\n"` still arrives as a line. It does
  this for about 30 hand-written cases. The same oracle, driven by a generator, covers
  millions.

Nothing in the design below replaces any of that. It generalises it.

## Scope

In `TelnetNegotiationCore.UnitTests`, alongside the existing 899, with bounded
iteration counts and fixed seeds so that every property gates every pull request.
Rejected alternatives: a separate long-running fuzz project with a nightly workflow
(more coverage, but a new project, a new workflow and a triage path nobody owns), and
coverage-guided fuzzing via SharpFuzz (better at finding crashes, much worse at
remaining in the repository as a regression guard, since its findings arrive as crash
artefacts rather than as tests).

The fuzzer drives `TelnetCoreMachine` first — that is where the migration's risk sits —
and the same generators are then lifted to a fully built `TelnetInterpreter` so that
MCCP's stream transforms, plugin ordering and the builder are covered too.

## The oracle

`RecordingTelnetContext` gains two additive members. It is shared by roughly twenty
test classes, so nothing existing changes.

**`PendingText`** exposes the unsubmitted line. `Write` currently accumulates into a
private `StringBuilder` that only surfaces on `SubmitAsync`, which means a stream whose
trailing text never receives its `\r\n` is indistinguishable from one that dropped the
text entirely. Every property that compares two runs needs to see it.

**`Snapshot()`** renders every recorded field to one deterministic string, so two runs
compare with a single equality and a failure prints a readable diff rather than sixty
assertions.

`Write` call boundaries are deliberately *not* recorded. This is load-bearing rather
than an omission: chunking a stream legitimately changes how byte runs batch into
`Write` calls, so a recorder that preserved those boundaries would make the
fragmentation property fail on every case for a reason of no interest to anyone. The
contract under test is which bytes arrive in which order, not how many calls delivered
them.

## Resync

Two properties need a byte sequence that returns the machine to `Idle` from an arbitrary
state, so that a probe line can be appended to garbage and observed.

`IAC SE` alone does not do it. Read from the transition table in `TelnetCoreModule`:
from `ReadingOption`, `OnAny` binds the `IAC` as the subnegotiation's option byte
(`Option = 255`) and the `SE` is then payload; from `EndSubNegotiation`, `IAC` is
consumed by `EscapedInPayload` as a literal and the `SE` is payload again. Both leave
the machine inside `SubNegotiating`.

`IAC SE IAC SE` reaches `Idle` from all thirteen core states. The pairs compose: any
state that the first pair leaves inside `SubNegotiating` is ended by the second, and
`Idle` is closed under the sequence, since `IAC` moves to `StartNegotiation` and `SE`
falls to `UnknownCommand`, which returns to `Idle`.

That reasoning covers the core module only. The sixteen protocol modules contribute
their own substates beneath `SubNegotiation` — CHARSET's translation-table states, MSSP's
variable and value markers, LINEMODE's SLC triplets — and some of those handle `IAC`
themselves. **Verifying the sequence against all 28 options is the first implementation
task.** If some module is not resynchronised by it, that is a finding worth having
before anything is built on top, and the resync token changes to whatever the evidence
supports.

A resync is followed by `\r\n` to flush whatever partial line the garbage accumulated,
so the probe line is compared exactly rather than by suffix.

## Generation

Streams are built from weighted *tokens*, not from random bytes. Uniform noise
essentially never produces a well-formed `IAC SB <option> … IAC SE` frame — the odds of
stumbling onto one are negligible — and frame handling is where the state machine lives.

| Weight | Token |
| --- | --- |
| 35% | A well-formed frame for one of the 28 options, payload drawn from that option's own grammar as its RFC defines it |
| 25% | Plain text, including `CR LF`, `CR NUL`, bare `CR`, bare `LF` and high bytes |
| 20% | A mutation of a well-formed frame: truncated at an arbitrary offset, missing its `SE`, option byte replaced, a stray `IAC` injected, an `SB` nested inside |
| 10% | A bare verb sequence, including the `IAC`-interrupts-a-pending-option path that `WillInterrupted` exists for |
| 10% | Raw adversarial bytes: a lone `0xFF`, `IAC IAC`, `IAC SE` with nothing open, `IAC SB` with no option |

Per-option payload grammars are taken from the RFCs, not from the implementation —
reading the grammar out of the code under test would make the generator agree with any
bug the code contains. The authorities are RFC 854 and RFC 855 for the framing, and per
option: RFC 857 (ECHO, 1), RFC 858 (SUPPRESS-GO-AHEAD, 3), RFC 1091 (TERMINAL-TYPE, 24),
RFC 885 (END-OF-RECORD, 25), RFC 1073 (NAWS, 31), RFC 1079 (TERMINAL-SPEED, 32),
RFC 1372 (TOGGLE-FLOW-CONTROL, 33), RFC 1184 (LINEMODE, 34), RFC 1096 (X-DISPLAY-LOCATION,
35), RFC 1408 (ENVIRON, 36), RFC 2941 (AUTHENTICATION, 37), RFC 2946 (ENCRYPT, 38),
RFC 1572 (NEW-ENVIRON, 39), RFC 2066 (CHARSET, 42). MSDP (69), MSSP (70), MXP (91),
MCCP1/2/3 (85/86/87) and GMCP (201) have no RFC; their grammars come from their
published specifications, which `docs/protocols/` already cites.

Randomness is a seeded xorshift generator written into the test project. No new package
reference: the test project carries only TUnit and Serilog today, Scorecard is already
docking the repository for unpinned NuGet dependencies, and automatic shrinking — the
one thing CsCheck would have given for free — is cheap to hand-write for the two shapes
that occur here.

Shrinking is therefore two functions: one bisects the token list, one drops split
points. A counterexample is reduced to a minimal byte array, printed as a C# literal,
and **committed as its own named regression test with the bytes inlined**. The fuzzer's
value is in what it finds; the found cases belong in the suite as ordinary tests, where
they keep their value whether or not a seed still reproduces them.

## The properties

In descending order of expected yield against this delta.

**1. Fragmentation invariance.** For any stream and any set of split points, feeding it
in chunks produces a snapshot identical to feeding it whole. This is the highest-value
property here. Every `ref self` field in `TelnetStates.cs` — `SubNegotiation.Option`,
`Connected.Width`, `Connected.Height` and each module's own — is state that must survive
a chunk boundary, and a real socket splits wherever it likes. Nothing tests this
systematically today; six test files do it ad hoc for specific sequences.

**2. No-throw.** `FireAsync` never throws, for any input. A throw here surfaces on a
consumer's read loop, where there is nothing useful to do with it.

**3. No-wedge liveness.** For any garbage, garbage + resync + probe line still submits
the probe line. Generalises `MalformedSubnegotiationRecoveryTests` from 30 cases to the
generated space. The bug class is real and recent: that file documents states which had
no transition for `IAC` or `SE` and so wedged the connection for its remaining lifetime
on one malformed byte.

**4. Escaping round-trip.** For any payload, what `SubnegotiationEscaping` escapes on the
way out is recovered byte-identically on the way in. This is the invariant PR #105
violated in both directions for ENCRYPT and AUTHENTICATION, where a `0xFF` went out
unescaped and desynchronised the stream.

**5. Text transparency.** For IAC-free input, every byte reaches the context exactly once
and in order, `CR`/`LF` handling aside.

**6. Bounded memory.** An unterminated subnegotiation stops at `SubnegotiationBuffer`'s
1 MiB cap and reports `Overflowed`, rather than growing while a peer keeps typing.

**7. MCCP ratio ceiling.** Any deflate stream either inflates within the 200:1 bound past
the 1 MiB floor, or is refused with the inflater stopped — never an OOM, never a throw
onto the read loop.

## Budget

Fragmentation invariance runs each case N+1 times and so gets 1,000 cases; the others
get 3,000 each. Seeds are fixed constants. The target is under ten seconds added to the
present seventeen, which keeps the whole suite fast enough that nobody is tempted to
skip it.

## Testing the tests

A property that cannot fail is worse than no property, because it reads as evidence.
Each one is therefore validated against a deliberately broken oracle before it is
trusted: a mutation is introduced that the property must catch — a recorder that drops
`PendingText`, a resync token shortened to a single `IAC SE`, an escaper that passes
`0xFF` through — the property is confirmed to fail, and the mutation is reverted. A
property that passes against its own mutant is not finished.

## Out of scope

Differential execution against the `v3.0.0` assembly. It would be the strongest possible
evidence for the migration specifically, and it is worth keeping in mind as a follow-up,
but it needs a second assembly reference and a shared driver seam, and the properties
above do not depend on it.

## Appendix: verified option grammars

Read from the RFCs on 2026-09-12, for the generator to build frames from. Values are
decimal. Framing throughout is `IAC SB <option> <payload> IAC SE`, and in every payload
below a literal 255 is sent doubled.

**ECHO (1)** — RFC 857. No subnegotiation.

**SUPPRESS-GO-AHEAD (3)** — RFC 858. No subnegotiation.

**TERMINAL-TYPE (24)** — RFC 1091. `IS`=0, `SEND`=1. An `IS` carries an NVT ASCII name,
case-insensitive, **maximum 40 characters**. The RFC does not say whether the name may
contain `IAC`, which makes it a generator target rather than a settled question.

**END-OF-RECORD (25)** — RFC 885. No subnegotiation; `IAC EOR` (239) is the marker.

**NAWS (31)** — RFC 1073. `WIDTH[1] WIDTH[0] HEIGHT[1] HEIGHT[0]`, two bytes each, network
byte order, so a dimension reaches 65535. Zero means "not being sent" and the receiver
falls back to its own default — a distinct case from a genuine zero, and one worth a
property.

**TERMINAL-SPEED (32)** — RFC 1079. `IS`=0, `SEND`=1. An `IS` carries
`<transmit>,<receive>` as decimal ASCII, no leading zeros, no spaces. TNC sends and
parses in this order; checked against the implementation and correct.

**TOGGLE-FLOW-CONTROL (33)** — RFC 1372. `OFF`=0, `ON`=1, `RESTART-ANY`=2,
`RESTART-XON`=3. One sub-command byte, no further payload. Unknown command codes **must
be silently ignored**, which is directly assertable.

**LINEMODE (34)** — RFC 1184. `MODE`=1, `FORWARDMASK`=2, `SLC`=3. MODE mask bits:
`EDIT`=1, `TRAPSIG`=2, `MODE_ACK`=4, `SOFT_TAB`=8, `LIT_ECHO`=16. SLC is a sequence of
three-byte triplets: function, then level-and-flags, then the character. Functions run
`SLC_SYNCH`=1 through `SLC_EEOL`=30. Level occupies the low two bits
(`NOSUPPORT`=0, `CANTCHANGE`=1, `VALUE`=2, `DEFAULT`=3) with flags `SLC_ACK`=128,
`SLC_FLUSHIN`=64, `SLC_FLUSHOUT`=32. Any 255 in a FORWARDMASK or in a triplet is doubled.
A truncated triplet — one or two bytes before `IAC SE` — is a generator case the RFC does
not define, and therefore one where "must not wedge" is the only defensible contract.

**X-DISPLAY-LOCATION (35)** — RFC 1096. `IS`=0, `SEND`=1. `<host>:<dispnum>[.<screennum>]`
as NVT ASCII, no surrounding whitespace.

**ENVIRON (36)** — RFC 1408. `IS`=0, `SEND`=1, `INFO`=2; `VAR`=0, `VALUE`=1, `ESC`=2,
`USERVAR`=3.

**AUTHENTICATION (37)** — RFC 2941. `IS`=0, `SEND`=1, `REPLY`=2, `NAME`=3. An
authentication type is a **two-octet pair**: the type, then modifier bits —
`AUTH_WHO_MASK`=0x01 (0 client-to-server, 1 server-to-client), `AUTH_HOW_MASK`=0x02
(0 one-way, 2 mutual), `ENCRYPT_MASK`=0x14 (0 off, 4 using-telopt, 16 after-exchange),
`INI_CRED_FWD_MASK`=0x08. An odd-length list of pairs is malformed and is a generator
case.

**ENCRYPT (38)** — RFC 2946. `IS`=0, `SUPPORT`=1, `REPLY`=2, `START`=3, `END`=4,
`REQUEST-START`=5, `REQUEST-END`=6, `ENC_KEYID`=7, `DEC_KEYID`=8. The side that sent
`WILL` is the one that sends encrypted data and **may transmit `START`/`END`**; the side
that sent `DO` receives and sends `SUPPORT`. This confirms the gating added in #105.
A `START` keyid is variable length, most significant byte first, **at least one byte**,
and zero means the default key — so a zero-length `START` is malformed, and another
generator case.

**NEW-ENVIRON (39)** — RFC 1572. `IS`=0, `SEND`=1, `INFO`=2; `VAR`=0, `VALUE`=1, `ESC`=2,
`USERVAR`=3. Same numeric values as ENVIRON. The escaping is two-layered and is the most
interesting grammar here: `IAC` doubles as usual, **and** within a name or a value each of
`VAR`, `VALUE`, `USERVAR` and `ESC` is escaped by a preceding `ESC`. A `SEND` with no
arguments requests the default environment; with arguments it carries bare `VAR`/`USERVAR`
type bytes, optionally each followed by a name. An `ESC` as the final byte before `IAC SE`
escapes nothing and is a generator case.

**CHARSET (42)** — RFC 2066. `REQUEST`=1, `ACCEPTED`=2, `REJECTED`=3, `TTABLE-IS`=4,
`TTABLE-REJECTED`=5, `TTABLE-ACK`=6, `TTABLE-NAK`=7. A `REQUEST` is optionally prefixed
`[TTABLE]<version>`, version a single non-zero octet, then a charset list whose
**separator is chosen by the sender and may be any octet except `IAC`**. That is the
sharpest generator target in the whole set: the separator is attacker-controlled, so the
list must parse correctly when it is `;`, a space, a digit, a byte inside a charset name,
or 0.

**MCCP1 (85), MCCP2 (86), MCCP3 (87)** — no RFC; the MCCP specification. v2's start marker
is `IAC SB 86 IAC SE` and v1's is `IAC SB 85 WILL SE`, the latter being why v1's marker is
honoured although the option is never negotiated. What follows either marker is a zlib
stream per RFC 1950.

**MSDP (69)**, **MSSP (70)**, **MXP (91)**, **GMCP (201)** — no RFC; their published
specifications, cited in `docs/protocols/`. MSDP: `VAR`=1, `VAL`=2, `TABLE_OPEN`=3,
`TABLE_CLOSE`=4, `ARRAY_OPEN`=5, `ARRAY_CLOSE`=6, arbitrarily nestable, which makes
unbalanced and deeply nested payloads the cases that matter. MSSP: `VAR`=1, `VAL`=2 in
alternating pairs. GMCP: a package name, a space, then JSON.
