# StateAlchemist Full Adoption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add cooperative batch boundaries to StateAlchemist, then make TelnetNegotiationCore use its generated machine as the single, pure, observable, batch-driven parser.

**Architecture:** StateAlchemist first gains a consumption-reporting batch pump whose actions can cooperatively end the current batch. TNC consumes that API to batch uncompressed telnet data without crossing an MCCP activation boundary, then adopts generated hooks, immutable configuration, authoritative state inspection, structural tests, and generated diagrams.

**Tech Stack:** C# latest, .NET 8/10/11 and netstandard2.0, Roslyn source generators, TUnit, StateAlchemist, System.Threading.Channels.

**Spec:** `docs/superpowers/specs/2026-09-14-statealchemist-full-adoption-design.md`

## Global Constraints

- Preserve every public TNC API and supported target framework.
- Keep compressed wire input byte-at-a-time and bounded-memory.
- Preserve channel ordering, backpressure, inferred-prompt ordering, and processing barriers.
- Use one generated-machine path and one source of truth for parser state.
- Write and observe each failing test before production changes.
- Open separate PRs against `main`; do not merge them.

---

### Task 1: StateAlchemist cooperative batch contract

**Files:**
- Modify: `src/StateAlchemist/Runtime/IMachine.cs`
- Modify: `src/StateAlchemist.Reference/Interpreter/ReferenceMachine.cs`
- Modify: reference pump implementation files selected by the repository structure
- Test: `tests/StateAlchemist.Contracts/Suite/RunContract.cs`
- Test: generated/reference harness files required to expose the API

**Interfaces:**
- Produces: `ValueTask<int> FireUntilBoundaryAsync(ReadOnlyMemory<TValue>)`, synchronous counterpart, and in-pump `RequestBatchBoundary()`.

- [ ] Add contract tests for full consumption, completed-action boundary, run boundary, queued-event ordering, suffix continuation, and outside-pump rejection.
- [ ] Run the focused contract tests and confirm the missing API/behavior fails.
- [ ] Implement the minimal reference-machine API and pump state.
- [ ] Run the reference contract tests to green.
- [ ] Commit the reference contract and behavior.

### Task 2: Generate cooperative batch behavior

**Files:**
- Modify: `src/StateAlchemist.Generators/Emission/MachineEmitter*.cs` or current equivalent
- Modify: generator model/runtime helpers only where required
- Test: generator snapshot/contract tests selected by repository conventions

**Interfaces:**
- Consumes: Task 1 batch contract.
- Produces: generated implementation behavior identical to the reference interpreter.

- [ ] Enable the contract tests for generated machines and confirm failure.
- [ ] Generate boundary state, request method, consumption-reporting pumps, and unchanged full-batch wrappers.
- [ ] Run generated and reference contract suites to green.
- [ ] Run generator snapshot tests and update intentional snapshots only.
- [ ] Commit generated-machine support.

### Task 3: StateAlchemist documentation and release readiness

**Files:**
- Modify: `docs/concepts/actions.md`
- Modify: `docs/concepts/concurrency.md`
- Modify: `docs/concepts/exceptions.md`
- Modify: `docs/concepts/runs.md`
- Modify: `docs/reference/generated-api.md`
- Modify: package/version/release notes according to repository conventions

**Interfaces:**
- Consumes: Tasks 1-2 final API.
- Produces: a package version TNC can reference.

- [ ] Document exact boundary, exception, decision, concurrency, and run semantics.
- [ ] Build all target frameworks and run the complete StateAlchemist test suite.
- [ ] Pack the release package to a local artifacts directory and inspect its contents.
- [ ] Commit documentation and release metadata.
- [ ] Push `codex/cooperative-batch-boundaries` and open the StateAlchemist PR against `main`.

### Task 4: TNC dependency and migration-seam removal

**Files:**
- Modify: `TelnetNegotiationCore/TelnetNegotiationCore.csproj`
- Modify: `TelnetNegotiationCore/Builders/TelnetInterpreterBuilder.cs`
- Modify: `TelnetNegotiationCore/Interpreters/TelnetGeneratedMachineInterpreter.cs`
- Modify: generated-machine unit tests that call the no-op selector
- Modify: stale analyzer documentation/comments found by symbol search

**Interfaces:**
- Consumes: locally packed StateAlchemist package from Task 3.
- Produces: unconditional generated-machine startup.

- [ ] Add a behavior test proving a normal builder always starts and uses the generated machine without a selector.
- [ ] Run it against the pre-change code and confirm it fails for a meaningful observable expectation.
- [ ] Configure a temporary local NuGet source and upgrade the package reference.
- [ ] Delete the selector flag/property/method and conditional startup; update current-tense comments.
- [ ] Run focused generated-machine tests to green.
- [ ] Commit dependency adoption and seam removal.

### Task 5: TNC phase-aware failure handling

**Files:**
- Create: `TelnetNegotiationCore/Machine/TelnetCoreMachine.Hooks.cs`
- Modify: `TelnetNegotiationCore/Machine/TelnetCoreModule.cs`
- Modify: `TelnetNegotiationCore/Interpreters/TelnetGeneratedMachineInterpreter.cs`
- Test: focused generated-machine failure tests

**Interfaces:**
- Produces: structured transition failure reporting and explicit StateAlchemist recovery policies.

- [ ] Add tests for callback failure, subsequent input, cancellation propagation, structured fields, and unhandled values.
- [ ] Run tests and confirm default propagation/current coarse logging fails expectations.
- [ ] Implement hook forwarding and recovery policy; retain one outer invariant catch.
- [ ] Run focused tests to green.
- [ ] Commit failure containment.

### Task 6: TNC strict purity and centralized machine construction

**Files:**
- Create: `TelnetNegotiationCore/Machine/TelnetMachineConfig.cs`
- Modify: `TelnetNegotiationCore/Machine/TelnetCoreModule.cs`
- Modify: `TelnetNegotiationCore/Interpreters/TelnetGeneratedMachineInterpreter.cs`
- Modify: machine-construction test helpers/call sites
- Test: carriage-return and definition tests

**Interfaces:**
- Produces: `TelnetCoreMachine` with `Config = typeof(TelnetMachineConfig)` and `Purity.Strict`.

- [ ] Add a structural purity test and focused carriage-return behavior tests through the centralized factory.
- [ ] Run tests and confirm context-using transforms fail the purity expectation.
- [ ] Move effects from transforms to completed actions and bind immutable configuration.
- [ ] Consolidate test machine construction around one helper.
- [ ] Run focused tests to green.
- [ ] Commit strict-purity adoption.

### Task 7: TNC authoritative NAWS machine state

**Files:**
- Modify: `TelnetNegotiationCore/Machine/TelnetStates.cs`
- Modify: `TelnetNegotiationCore/Machine/NawsModule.cs`
- Modify: `TelnetNegotiationCore/Interpreters/TelnetGeneratedMachineInterpreter.cs`
- Modify: `TelnetNegotiationCore/Interpreters/TelnetNAWSInterpreter.cs`
- Modify: `TelnetNegotiationCore/Protocols/NAWSProtocol.cs`
- Test: NAWS generated-machine tests

**Interfaces:**
- Produces: interpreter dimensions read from `Connected`; compatibility plugin properties mirror completed reports.

- [ ] Add tests for defaults and agreement after valid, short, escaped, and surplus frames.
- [ ] Run tests and confirm duplicate-state behavior fails at least one mutation-sensitive assertion.
- [ ] Initialize root defaults, expose focused state reads, and remove interpreter writes from the plugin.
- [ ] Run NAWS tests to green.
- [ ] Commit authoritative state ownership.

### Task 8: TNC safe channel batching

**Files:**
- Modify: `TelnetNegotiationCore/Interpreters/TelnetStandardInterpreter.cs`
- Modify: `TelnetNegotiationCore/Interpreters/TelnetGeneratedMachineInterpreter.cs`
- Modify: MCCP installation callback/path
- Test: batching, processing barrier, prompt, and MCCP tests

**Interfaces:**
- Consumes: `FireUntilBoundaryAsync` and `RequestBatchBoundary()`.
- Produces: bounded 4 KiB uncompressed batches with exact transform-switch boundaries.

- [ ] Add instrumentation-based behavior tests proving ordinary input forms a multi-byte run and MCCP switches before compressed suffix bytes.
- [ ] Add tests for sentinels, barriers, callback failures, decoder byte-at-a-time input, and over-cap ordering.
- [ ] Run focused tests and confirm single-byte firing fails the run assertion.
- [ ] Implement bounded channel draining and consumed-prefix accounting.
- [ ] Request a batch boundary when an inbound transform is installed or retired.
- [ ] Batch decoded telnet output through the same boundary loop without batching decoder input.
- [ ] Run all affected tests to green.
- [ ] Commit safe batching.

### Task 9: TNC structural contracts and generated diagrams

**Files:**
- Create: `TelnetNegotiationCore.UnitTests/GeneratedMachineDefinitionTests.cs`
- Create: `docs/generated/telnet-core-machine.mmd`
- Create: `docs/generated/telnet-core-machine.dot`
- Modify: `docs/index.md`
- Modify: test project only if artifact copying requires it

**Interfaces:**
- Consumes: `TelnetCoreMachine.Definition`, `Plan`, `Mermaid`, and `Dot`.
- Produces: CI-enforced structural invariants and code-derived diagrams.

- [ ] Add behavior-focused structure tests for reachability, byte coverage, option ownership, run stops, purity, and malformed recovery.
- [ ] Run tests and confirm missing artifact/contract expectations fail.
- [ ] Generate and check in Mermaid/DOT artifacts; link them from docs.
- [ ] Run focused tests to green.
- [ ] Commit structural contracts and diagrams.

### Task 10: Full verification, review, and TNC PR

**Files:**
- Modify: only fixes required by review or full-suite evidence.

- [ ] Restore from the local package source and run the complete TNC test suite for all target frameworks.
- [ ] Build the complete solution with warnings visible and run `git diff --check`.
- [ ] Review both repository diffs against the design and correct all important findings test-first.
- [ ] Re-run both complete suites after final corrections.
- [ ] Push the detached TNC HEAD as `codex/statealchemist-full-adoption`.
- [ ] Open the TNC PR against `main`, linked to the StateAlchemist PR/release dependency.
- [ ] Report PR URLs, exact test counts, release dependency, and any CI still running.
