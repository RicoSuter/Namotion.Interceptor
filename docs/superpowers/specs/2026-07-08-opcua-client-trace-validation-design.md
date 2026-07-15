# OPC UA Client Trace Validation (Phase 2): Binding the Code to the Formal Model

Status: Draft for review
Scope: `Namotion.Interceptor.OpcUa` client, `Namotion.Interceptor` core (trace helper), and the OPC UA test project
Depends on: PR #359 merged to master (the fixed client is what we instrument). Builds on Phase 1 (`docs/formal/opcua-client/OpcUaClient.tla`, PR #358).
Related: `docs/superpowers/specs/2026-07-07-opcua-client-formal-model-design.md`, `docs/formal/README.md`

## Goal

Bind the OPC UA client's actual behavior to the checked lifecycle model, so a future code regression in this area is caught mechanically rather than by review. Phase 1 verified the design; Phase 2 verifies the code stays faithful to it, on the executions the existing integration tests already drive.

## Approach: trace validation over the existing integration tests

Instrument the client to emit a trace of abstract state transitions. Run the existing integration suite so each test becomes a trace generator. Then check each recorded trace is a legal behavior of `OpcUaClient.tla` with TLC. This reuses the tests (no new scenarios to write) and keeps TLC as the single authority on the transition relation, so the model never gets re-implemented in a second place.

Trace validation is conformance on sampled executions, not exhaustive: it only covers what the tests drive. It complements, and does not replace, Phase 1's exhaustive design check.

## Workflow

Two steps, chained in CI:

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -c Release -p:EnableModelTrace=true --filter Category=Integration
tools/tla/check-traces.sh artifacts/model-traces/opcua-client/opcua.json docs/formal/opcua-client/OpcUaClient.tla
```

Step 1 writes one newline-delimited JSON file (`opcua.json`), one line per behavior (one client instance per test). Step 2 validates each behavior against the model and exits non-zero if any fails, printing the offending line and the TLC counterexample.

## Emission mechanism

A `[Conditional("MODELTRACE")]` static helper. The C# compiler removes every call to it (and its argument evaluation) when `MODELTRACE` is undefined, so the shipping build has zero instrumentation, zero cost, and provably no behavioral effect.

- **Symbol:** dedicated `MODELTRACE`, orthogonal to Debug/Release, defined in the repo-root `Directory.Build.props` behind a property:

  ```xml
  <DefineConstants Condition="'$(EnableModelTrace)'=='true'">$(DefineConstants);MODELTRACE</DefineConstants>
  ```

  The trace-validation build sets `-p:EnableModelTrace=true` on a Release build. Normal and shipping builds never set it. Validating a Release build (not Debug) keeps timing and JIT close to production; the only difference is the inert observation calls (a small probe effect remains, inherent to in-process tracing).

- **Placement:** the helper (a `[Conditional]` static method plus an `AsyncLocal` hook field) lives in core `Namotion.Interceptor`, so it is callable from every layer, including future models (transactions, core concurrency). It is inert dead code in a shipping build.

- **Split shim and sink:** the production assembly holds only the minimal shim and hook. The test infrastructure holds the real machinery: registering the hook, buffering events, sequence-stamping, JSON serialization, and flushing. JSON and file I/O never enter production.

- **Per-dimension events, folded test-side:** the abstract state is spread across subsystems (session in `SessionManager`, item coverage in `SubscriptionManager` and the health monitor, buffering in the writer). Each subsystem calls the helper at its own mutation site, right after the mutation and under the lock that guards it, with that dimension's delta plus a monotonic sequence number (a shared counter). The test-side assembler folds the per-dimension events into full-state snapshots.

- **Per-client isolation:** the sink is `AsyncLocal`-scoped so parallel tests and multiple client instances each get their own trace; the sequence counter is per-sink.

## Abstract state mapping (the main fidelity risk)

Mapping concrete C# state to the model's variables is where unfaithfulness would hide. Decisions:

- `state`: derived from the session and reconnect flags (`SessionManager`) into the eight model states, including `Abandoning` (from `AbandonCurrentSession`) and the SDK vs manual reconnect distinction.
- `cover[i]`: per owned monitored item, `Subscribed` (live), `Retrying` (transient failure kept, via `ClassifyFailedItem` returning `KeepForRetry`), `Polling` (escalated), or `Orphaned` (a retryable item dropped, the bug state). Permanent design-time-error items are legitimately unmonitorable and are excluded from the observed `Items` set, so `Orphaned` appears only on the real defect.
- `buffering`: from the writer's buffering flag.
- `linkUp`: emitted by the test harness, not the client internals, because the test controls the server and fault injection and therefore knows the true link state. This matches the model, where `linkUp` is the environment the test drives.

## Checking mechanism

- `docs/formal/opcua-client/OpcUaClientTrace.tla` is a trace-spec that `EXTENDS OpcUaClient`, reads a behavior from the JSON (via the TLA+ Community Modules `Json` parser), and asserts it is a legal behavior: each recorded state satisfies the invariants and each adjacent pair is a valid `Next` step for the recorded values. TLC reports exactly where a non-conforming trace breaks.
- `tools/tla/check-traces.sh <trace.json> <model.tla>` resolves the companion `<Model>Trace.tla` by convention, sets the trace path (an env var the trace-spec reads), and runs TLC per behavior. Generic, so transactions and later models reuse the driver; only the per-model trace-spec lives with the model.
- **Toolchain addition:** the bootstrap gains one pinned, checksummed artifact, `CommunityModules-deps.jar`, added to the `tlc` classpath. This is the only new dependency.
- **Fallback:** if JSON-in-TLA proves fiddly, a small converter turns each behavior into a TLA constant sequence that TLC checks, avoiding the extra jar at the cost of a converter to maintain. Preference is the `Json` module, which handles the nested per-item coverage map natively.

## Scope and incremental validation

- **Validate every emitted per-client trace.** A non-conformance is a signal: either a code bug or a gap in the model, both worth knowing. There is no in-scope allowlist that silently prunes traces to stay green.
- **Emit all three dimensions from the start** (uniform, cheap via the one helper), but **bring them into the TLC check incrementally**: validate the session-state projection green first, then widen to item coverage, then buffering. TLC can check a trace that pins only some variables (a projection), so the pipeline is de-risked dimension by dimension without a second instrumentation pass.
- **Known model-completeness edges** are modeled or excluded visibly with a documented reason, never silently. The current one is dynamic discovery (`OpcUaDynamicServerClientTests`), where the item set grows at runtime, which the model's fixed `Items` cannot represent. Excluded with a reason for the first cut; modeled in a later iteration. Disposal and shutdown are not edges: a trace simply ends, and trace validation checks steps, not termination.

## Trace file format and location

- Newline-delimited JSON, one behavior per line, each behavior an array of full-state snapshots.
- Written to a known, gitignored location (`artifacts/model-traces/opcua-client/opcua.json`), agreed by the test infra (writer) and the script (reader) via a fixed path or a `MODEL_TRACE_DIR` env var.

## CI

A dedicated job builds the OPC UA projects with `-p:EnableModelTrace=true` (Release), runs the integration tests to produce `opcua.json`, then runs `check-traces.sh`. Normal CI and shipping builds do not set the property. Cache `tools/tla/.cache/` for the toolchain.

## Non-goals (deferred to iteration 2)

- Value-level convergence (per-item server and client values, notification delivery, buffer-then-replay) and multi-client value semantics. Multi-client tests still produce conforming per-client lifecycle traces here; only their value interactions are out of scope.
- Dynamic item-set growth in the model.
- A reusable skill for authoring trace-validated models. Write it once the procedure has repeated (this being the first application), likely alongside rolling the pattern to transactions.

## Success criteria

- The full integration suite runs under Release + MODELTRACE and produces `opcua.json`.
- `check-traces.sh` validates every emitted per-client behavior against the session projection with zero non-conformances, then the coverage and buffering dimensions are enabled and reach zero non-conformances (bugs or model gaps resolved along the way).
- Reverting either #359 fix in the code makes a trace non-conform (the binding actually catches the regressions Phase 1 modeled).
- The check runs in CI, gating the OPC UA area against model drift.

## Open decisions

1. **JSON-in-TLA vs converter** for the checking mechanism. Preference: `Json` community module. Settle during implementation if it proves fiddly.
2. **Trace path convention** (`MODEL_TRACE_DIR` env var vs a fixed repo-relative path). Minor; pick during implementation.
