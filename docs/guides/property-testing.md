# The property-based regression suite

Most of this repository's tests are examples: feed these bytes, expect that callback. They are the
right tool for a protocol, because a protocol is a specification full of concrete cases, and 900-odd
of them is a good reason to trust the library.

They have one structural weakness. An example-based test only ever asks the question its author
thought to ask. That is tolerable when the code changes a little at a time, and it is a real problem
the moment something large is rewritten — because the tests get updated alongside the rewrite, and
then they demonstrate that the new code matches the new tests rather than that it matches what came
before.

The tests under `TelnetNegotiationCore.UnitTests/Fuzzing/` exist to ask questions nobody thought of.
Each one states an invariant, generates input, and reports the smallest input that breaks it.

## The seven properties

| Property | Where | What it asserts |
| --- | --- | --- |
| Fragmentation invariance | `FragmentationProperties` | A stream fed in chunks parses identically to the same stream fed whole — one byte at a time, at random boundaries, and with zero-length reads interleaved. |
| No-throw | `EngineProperties` | `FireAsync` never throws, for any input. |
| No-wedge liveness | `EngineProperties` | Any garbage, plus a resynchronisation, plus a line, still submits that line. |
| Text transparency | `EngineProperties` | For IAC-free input, every byte reaches the context once and in order. |
| Escaping round-trip | `EscapingProperties` | What `SubnegotiationEscaping` escapes is recovered byte-identically, and escaping never leaves a lone `0xFF`. |
| Bounded memory | `BoundedResourceProperties` | `SubnegotiationBuffer` never exceeds its cap and reports overflow exactly when it should. |
| MCCP bounds | `BoundedResourceProperties` | A compressed stream is either delivered or stopped with a reported reason — never an OOM, never a throw onto the read loop. |

`InterpreterProperties` runs the no-throw and liveness properties again through a fully configured
`TelnetInterpreter`, so the plugin manager, the builder and the byte-stream transforms are covered
and not only the bare machine.

Fragmentation invariance is the one that justifies the rest. Every `ref self` field in
[`TelnetStates.cs`](../../TelnetNegotiationCore/Machine/TelnetStates.cs) — a subnegotiation's option
byte, the window dimensions, each module's own — is state that has to survive a chunk boundary, and
a socket splits wherever it likes. Every example-based test in this repository hands its bytes over
in a single array, so none of them can see a bug of that shape.

## How input is generated

Not as random bytes. Uniform noise essentially never produces a well-formed
`IAC SB <option> … IAC SE` frame, and frame handling is where the state machine lives, so a
byte-level fuzzer would spend its entire budget in the text path.

`TelnetTokens` builds streams from weighted tokens instead:

| Weight | Token |
| --- | --- |
| 35% | A well-formed frame for one of the 21 options, payload drawn from that option's own grammar |
| 25% | Plain text, including `CR LF`, `CR NUL`, bare `CR`, bare `LF` and high bytes |
| 20% | A mutation of a well-formed frame — truncated, missing its `SE`, option replaced, a stray `IAC` or `SB` injected, a byte flipped |
| 10% | A bare verb sequence, including the `IAC`-interrupts-a-pending-option case |
| 10% | Adversarial bytes — a lone `0xFF`, `IAC IAC`, `IAC SE` with nothing open, `IAC SB` with no option |

**Each option's payload grammar comes from its RFC, not from this library's implementation of it.**
That distinction is the whole point: a generator that read its grammar out of the code under test
would agree with whatever that code gets wrong. The citations are on each arm of
`TelnetTokens.Payload`, and the full list with byte values is in the
[design spec](../superpowers/specs/2026-09-12-telnet-fuzz-regression-design.md).

Randomness is a seeded xorshift generator in `Rng`. `System.Random` is not used because its sequence
is not contractually stable across runtimes, and this suite runs on three of them — a case that
fails on `net8.0` has to be reproducible on `net11.0` from the same seed.

## When a property fails

The failure names the smallest input that still breaks the invariant, printed as a C# literal:

```
Property failed on case 36 of seed 0xC0FFEE.
Reason: expected "\ncr-nul\n" but the context received "\ncr-nul "
Shrunk to 1 bytes. Paste this into a named regression test:
    byte[] bytes = [13];
```

Do exactly that. **Commit the counterexample as its own named test** with the bytes inlined and a
remark saying what went wrong. A seed reproduces a failure only while the generator is unchanged; a
byte array reproduces it forever. The fuzzer's value is in what it finds, and the things it finds
belong in the suite as ordinary tests.

Shrinking is delta debugging by halves (`Shrink.Sequence`), applied first at token granularity — which
keeps frames intact and the counterexample readable — and then at byte granularity.

## Adding an option to the generator

When a new protocol is implemented:

1. Add its option byte to `TelnetTokens.Options` and to `ResyncTests.AllOptions`.
2. Add an arm to `TelnetTokens.Payload` building its payload **from its specification**, with the RFC
   or specification cited in a comment. Return it unescaped; `Escaped` handles the `0xFF` doubling
   that every one of these grammars requires.
3. If it has sub-command bytes, add them to `ResyncTests.OptionCommands` so that being left part-way
   through one is covered.
4. Run `ResyncTests`. If `IAC SE IAC SE` does not recover the new module, that is a wedge bug in the
   module, not a reason to widen the token.

## Every property is verified by mutation

A property that cannot fail is worse than no property, because it reads as evidence while providing
none. So each one has been checked by breaking the invariant on purpose and confirming the property
fails: the `0xFF` doubling removed from the escaper, the expansion ceiling set to `int.MaxValue`, the
overflow flag never set, the resynchronisation token shortened to one pair.

That step is not ceremony. Three of these properties were green and worthless when first written, and
the mutation is what said so — most instructively `InterpreterProperties`, which needed two
corrections before a shortened resynchronisation token would fail it at all.

If you change a property, mutate the thing it guards and watch it fail before you trust it again.

## Running them

```bash
dotnet test --project TelnetNegotiationCore.UnitTests --framework net10.0
```

Or just the properties:

```bash
dotnet run --project TelnetNegotiationCore.UnitTests -c Release --framework net10.0 -- --treenode-filter "/*/TelnetNegotiationCore.UnitTests.Fuzzing/*/*"
```

The iteration counts are constants at the top of each class, sized so the whole suite stays around
twenty seconds. They are deliberately modest: a suite people skip has no value at all. Raise them
locally when hunting something specific.
