# StateAlchemist Full Adoption Design

## Purpose

TelnetNegotiationCore already uses a StateAlchemist-generated machine for telnet framing and protocol
subnegotiations, but it still drives that machine one byte at a time, treats its state as an internal copy, and
keeps migration-era dispatch and configuration seams. This work makes the generated machine the single explicit
engine, uses its inspection, diagnostics, purity, exception, and diagram facilities, and adds the smallest
StateAlchemist batching primitive needed to preserve TNC's MCCP boundary semantics.

The work is divided into two independently reviewable repository changes. StateAlchemist gains one general batch
control capability first. TNC then consumes a released version containing that capability. Reusable generic
module fragments and generated dependency injection are intentionally separate future designs: neither is needed
to realize the immediate correctness and performance gains.

## Constraints

- Preserve every public TNC API and every supported target framework.
- Preserve bounded-memory decompression: compressed wire input continues to enter the decoder one byte at a time.
- Preserve ordering and backpressure through the existing single channel consumer.
- Preserve per-channel-item completion accounting used by `WaitForProcessingAsync`.
- Do not process bytes following an MCCP activation marker as uncompressed telnet input.
- Keep the generated machine allocation-free on synchronous transitions and keep batch scanning vectorized where
  StateAlchemist currently supports it.
- Use one StateAlchemist machine path. Remove migration switches rather than maintain two behaviors.
- Every behavior change starts with a failing test in its owning repository.

## StateAlchemist change: cooperative batch boundaries

### API

Add a generated `RequestBatchBoundary()` method that is callable only from code running inside a machine action or
hook, following the same ownership rule as `Enqueue`. Calling it outside an active pump throws
`InvalidOperationException`.

Add batch overloads that report consumption:

```csharp
ValueTask<int> FireUntilBoundaryAsync(ReadOnlyMemory<TValue> values);
int FireUntilBoundary(ReadOnlyMemory<TValue> values);
```

The return value is the number of values consumed. With no requested boundary it equals `values.Length`.
`RequestBatchBoundary()` makes the batch return after the current transition and its queued events finish. For a
run transition, the whole run presented to that transition counts as consumed; the boundary cannot split a run
retroactively.

Existing `FireAsync(ReadOnlyMemory<TValue>)` remains source- and behavior-compatible. It uses the same pump but
continues after cooperative boundaries until the complete input has been consumed. This prevents a library
upgrade from silently changing existing callers.

### Semantics

- A boundary request is scoped to the current batch and cleared before returning.
- Single-value and event firing ignore the boundary after clearing it; there is no unconsumed input to return.
- Exceptions retain existing semantics. An exception propagating from value `n` does not return a count.
- A decision completes, fails, or is cancelled according to existing rules before the boundary is observed.
- Serialized concurrency treats one `FireUntilBoundaryAsync` call as one inbox operation.
- `OnTransitioned` runs before the boundary is observed.

### Verification

Contract tests run against both the reference interpreter and generated machines. They cover no boundary, a
boundary from `Completed`, a boundary followed by queued events, a boundary in a run, repeated calls over a suffix,
outside-pump rejection, synchronous parity, checked concurrency, and unchanged full-batch `FireAsync` behavior.

Documentation updates describe the new API in batching, actions, concurrency, exceptions, and generated API
references.

## TNC change: one generated-machine path

Delete `_useGeneratedMachine`, `UseGeneratedMachine()`, `TelnetInterpreter.UseGeneratedMachine`, conditional
startup, and explicit test calls. Construction always starts `TelnetCoreMachine`; comments and analyzer docs stop
describing Stateless as a live alternative. This is internal cleanup and introduces no public compatibility
surface.

The machine field is stored as `IMachine<byte>` where only the common contract is needed. Concrete generated APIs
remain reachable through a focused internal accessor for inspection and TNC-specific hooks.

## TNC change: phase-aware failure containment

Implement all generated exception hooks on `TelnetCoreMachine`. They forward a structured record to the
interpreter-owned context containing the exception, transition name, source, target, active leaf, and phase.

Recovery policy is explicit:

- `Guard`, `Transform`, and `Complete`: `Skip`, because state has not committed and a half-mutated staying state
  must not be treated as successful.
- `Exited`, `Entered`, and `Completed`: `Continue`, so remaining post-commit actions and transition notification run.
- `OperationCanceledException`: rethrow so connection shutdown is not converted into protocol recovery.

Unhandled values are logged as critical with the active state. The machine continues because the core framing
table deliberately has recovery fallbacks. The existing outer catch remains as a final invariant boundary and
logs that machine-level recovery itself failed.

Tests inject failures through real context callbacks and assert the selected recovery, subsequent-byte handling,
and structured log fields.

## TNC change: strict purity and immutable configuration

Introduce `TelnetMachineConfig`, initially containing `CarriageReturnMode`. Pass it as StateAlchemist `Config` and
set `Purity.Strict` on `TelnetCoreMachine`.

The six context-using transforms in `TelnetCoreModule` become class-form transitions whose transforms update only
state and whose `Completed` actions perform writes. Carriage-return choices read `in TelnetMachineConfig`; effects
remain post-commit actions. Protocol callbacks already use completed actions and require no semantic change.

Tests construct the machine through one helper that supplies the default configuration, with focused tests for
all carriage-return modes. A structural test asserts no transition definition reports transform/guard context use.

## TNC change: authoritative machine state

`Connected` initializes the RFC defaults for width and height. `TelnetInterpreter.ClientWidth` and
`ClientHeight` read the active `Connected` state after machine startup and return the same defaults before startup.
The NAWS plugin retains its public compatibility properties, updated by the existing completed action, but no
longer writes duplicate interpreter fields.

The machine remains authoritative for parser-owned state. Protocol-owned negotiated status stays in the protocol
objects because it controls outbound behavior and public plugin APIs; moving that state would couple the parser
to runtime plugin enablement and is outside this change.

Tests assert the machine and compatibility properties agree after valid, short, escaped, and surplus NAWS frames.

## TNC change: safe channel batching

The channel remains `Channel<int>` because it also carries the inferred-prompt sentinel. The consumer gathers a
bounded contiguous run of immediately available ordinary wire bytes into a pooled buffer. The cap is fixed and
small enough to bound latency and memory; 4 KiB is the initial value and becomes an internal constant covered by
tests.

When no inbound transform is active, the consumer calls `FireUntilBoundaryAsync` with that batch. MCCP marker
completion installs the transform and requests a StateAlchemist batch boundary. The consumed prefix is marked
handled; the unconsumed suffix is then fed to the newly installed decoder one byte at a time.

When a transform is already active, wire bytes continue to enter `DecodeAsync(byte)` individually. Decoded output
may be fired as a StateAlchemist batch because it is already telnet data, but the decoder can be retired or
replaced by an action, so the same cooperative-boundary loop is used. Byte-processed notifications retain their
current logical count. Channel-item handled accounting advances only for the raw items actually removed from the
channel.

Tests prove:

- ordinary text reaches a run transition as a multi-byte run;
- IAC and newline stop runs correctly;
- an MCCP marker and compressed bytes in the same drained channel batch switch at the exact boundary;
- decompression still receives one wire byte per call;
- inferred-prompt sentinels split batches and retain ordering;
- `WaitForProcessingAsync` does not return before callbacks complete;
- callback exceptions do not strand accounting;
- input larger than the batch cap remains bounded and ordered.

## TNC change: inspection, structural tests, and diagrams

Add generated-machine contract tests over `Definition` and `Plan`:

- every state is reachable;
- every byte is handled from every reachable leaf;
- each telnet option is claimed by at most one protocol transition per negotiation direction;
- every declared run has the expected IAC, SE, CR, LF, or protocol-marker stop transitions on its own state;
- strict purity holds;
- known malformed sequences plan into discard/recovery states rather than wedging.

Publish `TelnetCoreMachine.Mermaid` and `.Dot` under `docs/generated/`. A test compares checked-in artifacts to the
generated constants byte-for-byte, making drift fail CI. The documentation index links the Mermaid source and
explains that it is generated rather than hand-maintained.

StateAlchemist diagram label aliases and module-filtered rendering remain a follow-up enhancement. TNC will first
exercise the existing truthful whole-machine artifacts; actual readability evidence from that output will define
the follow-up API.

## Delivery order

1. Implement and release cooperative batch boundaries in StateAlchemist.
2. Upgrade TNC to that release.
3. Remove migration seams and add phase-aware hooks.
4. Enable strict purity and centralize machine construction.
5. Make NAWS machine state authoritative.
6. Integrate safe channel batching.
7. Add structural tests and checked-in diagrams.
8. Run the complete target-framework build and test matrix, inspect the generated diff, and prepare separate PRs
   for the two repositories.

No PR is opened until the repository owner has reviewed the completed changes and explicitly authorizes the
specific PR if either destination is considered external.
