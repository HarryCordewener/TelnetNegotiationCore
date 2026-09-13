# Configurable carriage-return handling, and ENVIRON escape decoding

*2026-09-13*

Closes [#111](https://github.com/HarryCordewener/TelnetNegotiationCore/issues/111) and
[#110](https://github.com/HarryCordewener/TelnetNegotiationCore/issues/110).

## Why

Two receive-path defects, both found by the property suite in #109 and both confirmed against other
implementations rather than against the RFCs alone.

**A `NUL` after a carriage return reaches the consumer as a literal `0x00` inside the line.** Every
authority and implementation checked consumes that `NUL`; not one delivers it. So this part is not a
trade-off, it is a defect. What the `CR NUL` pair *means*, however, genuinely differs by direction:

- RFC 1123 §3.3.1, on a server reading user input: "CR LF and CR NUL MUST have the same effect on an
  ASCII server host when received as input" — it ends the line.
- RFC 854, and libtelnet's `TELNET_FLAG_NVT_EOL`, on the data stream generally: `CR NUL` carries a
  literal carriage return.

Both readings are legitimate and neither is right for every consumer, which is why this becomes an
option rather than a fix with one answer.

**Neither `NewEnvironModule` nor `EnvironModule` decodes RFC 1572's `ESC`.** `ESC` is not even among
their declared constants. `VAR 'A' VALUE ESC VALUE 'B'` arrives as `VAR | A | VALUE | 0x02 | VALUE |
B`: the escape leaks through as data and the byte it was escaping is read as a second marker,
splitting the value. `NewEnvironProtocol.AppendEscaped` escapes correctly on the way out, so the
library mis-parses its own output — the same one-directional asymmetry PR #105 fixed for ENCRYPT.
libtelnet decodes it (`if (*c == TELNET_ENVIRON_ESC) { ++c; }`), so this is not a corner
implementations skip.

## Decisions taken

- **Receive only.** TNC adds no outbound line terminator today — the consumer supplies its own bytes
  — and that stays true. No send-side option, and TNC does not begin rewriting outbound data.
- **The default preserves today's behaviour, minus the bug.** A consumer who sets nothing keeps
  CR-dropping and LF-submission exactly as now; only the stray `0x00` disappears.
- **The `ESC` decoding is not configurable.** Both RFCs require it and libtelnet does it
  unconditionally. It is a behaviour change, noted as one, not an option.

## The carriage-return state

Today CR is discarded the instant it arrives — `DropReturn` from `Accepting` and `DropReturnInLine`
from `ReadingCharacters`, both stays that do nothing. Every interesting `CR NUL` behaviour depends on
the byte that comes *next*, so the carriage return has to be remembered, and there is nowhere to
remember it.

A new state, `AfterCarriageReturn`, a child of `Accepting`:

```
Connected
└── Accepting ── Idle [initial], ReadingCharacters, AfterCarriageReturn, DoNothing, GoAhead
```

The two `On(CarriageReturn)` stays become moves into it. Out of it:

| Trigger | Target | Behaviour |
| --- | --- | --- |
| `LF` | `Idle` | Submit the line. `CR LF` is end-of-line in every mode. |
| `NUL` | `Idle` | Mode-dependent: `Drop` writes nothing; `EndOfLine` submits; `Preserve` writes one `CR`. |
| `CR` | stay | A second carriage return: the first is now known not to be part of `CR LF` or `CR NUL`, so `Preserve` writes one `CR` and the others write nothing. |
| `IAC` | `StartNegotiation` | Re-declared, because the new state does not inherit it usefully. `Preserve` writes the pending `CR` first: the carriage return was data, and the `IAC` begins a command after it. |
| anything else | `ReadingCharacters` | `Preserve` writes a `CR` then the byte; the others write just the byte. |

A carriage return that is still pending when the stream stops writes nothing, in every mode
including `Preserve`. That is deliberate rather than an oversight: until the next byte arrives the
machine cannot know whether it is looking at `CR LF`, `CR NUL` or a data carriage return, and
guessing at end of stream would make the same input parse differently depending on where the peer
happened to stop sending. It is the one place `Preserve` loses a carriage return, and the
fragmentation property would fail if it were resolved any other way.

**The mode is read in the transform, not in competing guards.** Three guarded `On(NUL)` transitions
would work but would need explicit `Order` and raise an ambiguity question; one transition whose
`CompletedAsync` switches on the mode needs neither, and the target is `Idle` in all three cases so
nothing forces the branch into the transition table.

**Why `Idle` is a safe target even mid-line.** Landing in `Idle` rather than back in
`ReadingCharacters` looks lossy and is not: the line's accumulated text lives in the *context*, not
in state data, so nothing is discarded. The only consequence is which write transition fires next —
`BeginLine` from `Accepting` rather than `MoreText` from `ReadingCharacters` — and both write the
byte. `"ab" CR "cd" LF` submits `abcd` either way. That is worth stating because it is the one part
of this design that reads wrong at a glance.

**Rejected: a `bool AfterReturn` flag plus guards on the existing states.** Less new machinery, but
this is exactly the `Escaping`-boolean pattern that `MalformedSubnegotiationRecoveryTests` documents
as a *misfire* bug class — a stale flag firing a callback it had not earned. The repository's own
tests argue against it.

**Rejected: handling CR outside the machine**, in an `IByteStreamTransform` or in the interpreter.
The machine owns line assembly (`Write`, `SubmitAsync`), so carriage-return semantics belong with it;
anywhere else duplicates knowledge of where a line ends.

## How the mode reaches the machine

`TelnetCoreContext` gains a **virtual** property:

```csharp
public virtual CarriageReturnMode CarriageReturnMode => CarriageReturnMode.Drop;
```

Virtual and not abstract on purpose: `TelnetCoreContext` is public, `RecordingTelnetContext` and any
consumer subclass keep compiling unchanged, and a test that wants another mode overrides one member.

`GeneratedContext` already holds the owning `TelnetInterpreter`, so it overrides this to return the
interpreter's setting. Transforms and guards may take the context because the machine sets no
`Purity`, and StateAlchemist allows the context as a parameter on any phase method unless the machine
is `Purity.Strict`.

**Rejected: StateAlchemist's `in TConfig config`.** It is the more idiomatic mechanism and expresses
immutability properly — the mode must not change mid-connection. But adding `Config` to `[Machine]`
changes how the machine is constructed, and every `new TelnetCoreMachine(recorder)` across roughly
twenty test files would have to be updated for no behavioural gain. Immutability is achieved anyway,
because the builder fixes the mode before `BuildAsync` and the interpreter exposes it as `init`-only.
Worth revisiting if a second core-level machine setting ever appears.

## The API

```csharp
namespace TelnetNegotiationCore.Models;

/// <summary>What the machine does with a carriage return that is not part of CR LF.</summary>
public enum CarriageReturnMode
{
    /// <summary>Discard it, and consume a NUL that follows. The default.</summary>
    Drop,

    /// <summary>CR NUL ends the line, as RFC 1123 requires of a server reading user input.</summary>
    EndOfLine,

    /// <summary>CR NUL yields a literal CR, as RFC 854 defines and libtelnet implements.</summary>
    Preserve,
}
```

On `TelnetInterpreter`, following `MaxBufferSize`'s existing shape:

```csharp
public CarriageReturnMode CarriageReturnMode { get; init; } = DefaultCarriageReturnMode;
public const CarriageReturnMode DefaultCarriageReturnMode = CarriageReturnMode.Drop;
```

On `TelnetInterpreterBuilder` — `With*` is the convention for settings (`WithKeepAlive`,
`WithMaxBufferSize`), and the three shorthands exist so a call site says what it means rather than
naming an enum member:

```csharp
public TelnetInterpreterBuilder WithCarriageReturnMode(CarriageReturnMode mode);
public TelnetInterpreterBuilder DropCarriageReturns();               // Drop
public TelnetInterpreterBuilder TreatCarriageReturnNullAsLineEnd();  // EndOfLine
public TelnetInterpreterBuilder PreserveCarriageReturns();           // Preserve
```

`WithCarriageReturnMode` validates the enum with `ArgumentOutOfRangeException`, as
`WithMaxBufferSize` validates its argument.

## ENVIRON and NEW-ENVIRON escape decoding

`NewEnvironField` and `EnvironField` gain `Esc = 2` as a declared constant and a `bool TypeEscaped`
alongside the existing `Escaping`.

- A new `[On(Esc)]` stay. If `TypeEscaped` is already set, this is `ESC ESC`: emit one literal `ESC`
  as data and clear the flag. Otherwise set the flag and emit nothing.
- `VarMarker`, `UserVarMarker` and `ValueMarker` gain `Guard(in NewEnvironField self) =>
  !self.TypeEscaped`, so an escaped marker byte is not read as structure.
- The `Capture` run clears `TypeEscaped` as it already clears `Escaping`.
- An `ESC` as the final byte before `IAC SE` escapes nothing and is consumed, matching libtelnet.
- An `ESC` before a byte the RFC does not list as escapable is consumed and the byte delivered
  literally, also matching libtelnet, which skips the `ESC` unconditionally.

**Why the guard-plus-run arrangement works, which #110 could not previously confirm.** A run's stop
set "is computed at compile time from the same rules that pick a transition" — from which transitions
*exist*, not from what their guards return. So a guarded marker keeps its trigger in the stop set, the
run stops there, the guard declines, and resolution falls through to the `[OnAny]` run, which takes
that single byte as data. Runs themselves cannot be guarded ("a guard per value would defeat the
point"), which is why the guards go on the markers instead.

This is not a new pattern: `GmcpModule.Ended` is `[On(SE)]` guarded on `Escaping`, and a payload byte
that happens to be `240` falls through to GMCP's `[OnAny]` run as data. `EscapingProperties`
exercises exactly that over 3,000 generated payloads containing arbitrary bytes, byte-exact. The
mechanism is already load-bearing and already covered.

## Testing

**`CarriageReturnPolicyTests` and `EnvironEscapeDivergenceTests` currently pin the wrong behaviour
and are expected to fail.** That is the signal the fix landed, and both are rewritten rather than
deleted: the assertions become the conforming ones, and the remarks cite RFC 1123 and RFC 1572 rather
than recording a divergence.

New coverage:

- A matrix over the three modes for `CR LF`, `CR NUL`, bare `CR` before text, `CR CR`, `CR IAC`, and
  a trailing `CR` at end of stream.
- That the mode is reachable from the builder, including each shorthand, and that an invalid enum
  value is refused.
- `ESC` before each of the four reserved bytes, `ESC ESC`, a trailing `ESC`, and `ESC` before an
  ordinary byte, in both ENVIRON and NEW-ENVIRON.
- That MNES behaviour is unchanged, since MNES forbids these bytes in names and values and
  `MnesProfileTests` must keep passing.

**The property suite is the regression net.** Adding a state to the hot text path and guards to the
ENVIRON markers are both exactly the kind of change that wedges a connection, and
`EngineProperties.AnyStreamRecoversAndKeepsParsingText` plus all three of `FragmentationProperties`
are what would catch it. Both new mechanisms are validated by mutation, per the rule that a property
which passes against its own mutant proves nothing.

Note that `TelnetTokens.Text` already generates `CR LF`, `CR NUL`, bare `CR` and bare `LF`, and
`EnvironField` already emits the reserved bytes for the escaper — so the existing corpus exercises
both changes without extension.

## Out of scope

- **Any outbound line terminator.** Decided above.
- **Delivering carriage returns for overprint animation.** MUDs do use bare `CR` for ASCII spinners,
  and `Preserve` makes the byte visible, but TNC's consumer API is line-oriented: `OnSubmit` hands
  over complete lines, so there is nowhere for an overprint to land. Character-cell rendering is
  outside this API by design. Worth a documentation note, not a code change.
- **RFC 1091's 40-character terminal-type limit** ([#113](https://github.com/HarryCordewener/TelnetNegotiationCore/issues/113)),
  which is unrelated and unreachable in practice.
