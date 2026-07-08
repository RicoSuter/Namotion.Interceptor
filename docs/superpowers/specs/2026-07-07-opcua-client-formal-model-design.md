# OPC UA Client Formal Model: Lifecycle Pilot for TLA+ Model-Checking and Trace Validation

Status: Draft for review
Scope: `Namotion.Interceptor.OpcUa` client (session and subscription lifecycle), plus a reusable formal-modeling pipeline for this repo
Branch note: authored on `design/opcua-master-comparison-loader`; intended for its own branch (see Open decisions)
Related: `docs/formal/` (process doc, to be created during iteration 1), `docs/superpowers/specs/2026-06-19-websocket-protocol-reliability-design.md`

## Motivation

The OPC UA client is a rich concurrent state machine: `Disconnected -> Connecting -> Connected -> SessionActive`, two distinct reconnect paths (SDK keep-alive auto-reconnect with subscription transfer, and manual reconnect that recreates subscriptions from scratch), stall detection with force-reset, and a buffering window during full-state-sync. Integration tests exercise these paths by sampling: they run one execution and check the outcome. Sampling cannot cover the adversarial interleavings (drop, then reconnect, then a second drop, in a particular order) where reliability bugs live. The WebSocket work already hit this: a divergence backstop that "only fired in the connector tester because the test has a quiescent convergence phase."

Formal modeling replaces sampling with exhaustion within bounds. We build a small abstract model of the client and check correctness properties over every reachable state and every interleaving, then bind the real code back to that model.

## Two verification modes, and the rule that makes them useful

1. **Model-check the design (TLC).** Write an abstract model (state variables and allowed transitions), assert correctness properties, and let TLC enumerate every reachable state up to configured bounds. It either proves the property holds for all of them or returns a concrete counterexample trace. This finds design bugs, needs no code changes, and needs no tests.
2. **Trace-validate the code.** Instrument the client to emit a structured trace of abstract state transitions, run the existing test suite to generate traces, and check each trace is a legal behavior of the model. This binds the real code to the checked design, but only on the executions the tests exercise.

They are complementary: model-checking is exhaustive over the model but says nothing about whether the code matches the model; trace validation confirms the code matches the model but only on sampled runs. Together they squeeze the gap from both sides.

The rule that keeps this honest: **extract the transitions from the code, but write the invariants independently.** The states and actions mirror what the client actually does (faithfully, including its warts). The invariants come from OPC UA semantics and the reliability bar, never from reading the code. If the invariant is derived from the code, TLC only confirms "the code does what the code does," which is worthless. The bug-finding power lives in the gap between a faithful model of the behavior and an independent statement of correctness.

## Pilot scope: lifecycle first

The client has too much surface to model faithfully in one pass. The pilot models the **session and subscription lifecycle** and defers the rest.

In scope:
- Session states: `Disconnected, Connecting, SessionActive, ReconnectingSdk, ReconnectingManual, Stalled, Faulted`.
- A small set of monitored items, each with a `subscribed` flag and a `lastChangeSeq`.
- Both reconnect paths (SDK transfer succeeds vs transfer fails then manual recreate), stall then force-reset, and the buffering window.

Deferred (documented here, modeled later): value-level convergence of property payloads (iteration 2), polling fallback, read-after-write, multi-client conflict.

Why lifecycle first: the lifecycle is fully observable through a single instrumentation hook (`OnCurrentSessionChanged`) plus the reconnect metrics, so iteration 1 proves the entire pipeline (model, TLC, trace-check, JVM in CI, xUnit wiring) at the lowest instrumentation cost. Value convergence is the more expensive half on the instrumentation side (see Iteration 2), so keeping it out of iteration 1 maximizes learning per unit effort.

## The model (iteration 1)

Transitions extracted from the client; invariants stated independently.

### State variables
- `sessionState`: one of the seven states above.
- `linkUp`: boolean, adversary-controlled. The server or network can drop at any time.
- `items`: a set, each with `subscribed` (boolean) and `lastChangeSeq` (natural). `lastChangeSeq` and the buffering flag are scaffolding kept from the start so iteration 2 can attach to them without rework.
- `buffering`: boolean, set while a manual reconnect is in flight.
- `reconnectDeadlineExceeded`: boolean, models the stall timeout.

### Actions
`Connect`, `EstablishSession`, `CreateSubscriptions`, `LinkDrops`, `KeepAliveDetectsLoss`, `SdkReconnectTransferOk`, `SdkReconnectTransferFails`, `StartManualReconnect`, `ManualReconnectRecreates`, `StallTimeout`, `ForceReset`, `KillOrDispose`.

### Invariants
Safety:
- No orphaned item: once the client settles in `SessionActive`, every item is `subscribed`.
- Mutual exclusion: not `SessionActive` and reconnecting at once; `buffering` is set whenever a manual reconnect is in flight.
- No lost re-subscription across the transfer-fails path (the case an integration test found the hard way).

Liveness:
- If `linkUp` eventually stays true, the client eventually reaches `SessionActive` with all items subscribed and stays there (`<>[]Converged`). This catches infinite reconnect or stall loops. The weaker "converges at least once" form is satisfied by the first activation and cannot detect a failure to re-converge after a reconnect, so the stay-converged form is used.

### Code anchors (for faithful extraction)
- Session transitions and the single hook: `OpcUaSubjectClientSource.OnCurrentSessionChanged` (in `OpcUaSubjectClientSource.cs`; referenced by method since #359 and #364 shifted line numbers).
- SDK auto-reconnect and transfer: `SessionManager.OnKeepAlive` / `OnReconnectComplete`.
- Manual reconnect and buffering: `OpcUaSubjectClientSource.ReconnectSessionAsync` (buffer, create session, recreate subscriptions, load state and resume).
- Stall and force-reset: `SessionManager.TryForceResetIfStalled`.

### Update: enriched for PR #359 (two leads confirmed as real bugs)

Formalizing these invariants surfaced two places where the clean model diverged from the code. Both turned out to be real defects and were fixed in PR #359, and the model was then enriched so its mutation checks reproduce the pre-fix behavior:

- **Partial re-subscription (fix 1).** `subscribed: [Items -> BOOLEAN]` became `cover: [Items -> {Subscribed, Retrying, Polling, Orphaned}]`, with `HealItem` and `EscalateItem` actions. A transient subscription failure goes to `Retrying` and is healed or escalated to `Polling`, never to `Orphaned`. `NoOrphanedItem` (no `Orphaned` at `SessionActive`) is mutation-proven: sending transient failures to `Orphaned` reproduces the pre-fix `FilterOutFailedMonitoredItemsAsync` drop and TLC finds the counterexample.
- **Abandon window (fix 2).** Added an explicit `Abandoning` state between `ReconnectingSdk` and `ReconnectingManual`, and the invariant `BufferingCoversAbandon` (buffering is on from the moment of abandon). Mutation-proven: not buffering at abandon reproduces the pre-fix `AbandonCurrentSession` gap and TLC reports a violation.

The convergence liveness now requires every item covered (subscribed or escalated to polling). Model checks at `Items = {i1, i2}`, `PollingEnabled = TRUE`, 66 reachable states. See `docs/formal/opcua-client/OpcUaClient.md`.

### Verified against merged master (#364)

PR #364 later centralized status-code classification into `OpcUaStatusCodeClassifier` and changed which codes count as transient, but kept the disposition categories (`KeepForRetry`, `FallbackToPolling`, `Drop`) and the escalation bound (`MaxHealAttemptsBeforeEscalation`). The health monitor still retries transient failures (`SubscriptionHealthMonitor.IsRetryable` now delegates to the classifier). So the abstract coverage behavior the model captures is unchanged, and the model re-checks green against merged master (which includes #359 and #364) with no edits.

## Toolchain

TLA+ with the TLC model checker (TLC runs on the JVM). Set up as a rootless, no-Docker toolchain under `tools/tla/`, proven working in this environment:

- `tools/tla/bootstrap.sh` downloads pinned artifacts into `tools/tla/.cache/` (gitignored): `tla2tools.jar` v1.7.4 (TLC 2.19) and, only when no system Java is present, a portable Temurin JRE 17. Both are SHA-256 verified.
- `tools/tla/tlc <Module.tla>` runs the checker, preferring a system Java (`JAVA_HOME` or PATH) and falling back to the portable JRE.
- Self-test: `tools/tla/tlc selftest/Smoke.tla` reports "No error has been found".
- CI: add `actions/setup-java` (Temurin 17), then call `tools/tla/bootstrap.sh` (fetches only the ~2 MB jar, since system Java is present) and `tools/tla/tlc`. The identical wrapper runs locally and in CI; cache `tools/tla/.cache/` for speed.

## Artifacts and where they live

Distinct lifetimes, kept in distinct places, cross-referenced rather than duplicated (per the repo's one-canonical-location docs convention):

- **This spec** (`docs/superpowers/specs/2026-07-07-...md`): per-pilot decisions and scope. Dated, historical.
- **Process doc** (`docs/formal/README.md`, created during iteration 1 as a living output): the durable, reusable how-to. The two verification modes, the extract-transitions-independent-invariants rule, file layout, TLC commands, how to read a counterexample, the per-iteration loop. Reused by every later iteration and by transactions. Stubbed first, filled with real commands as iteration 1 is built.
- **Model files** (`docs/formal/opcua-client/`): `OpcUaClient.md` (prose model plus a Mermaid state diagram), `OpcUaClient.tla` and `OpcUaClient.cfg` (the machine-checked source of truth).
- **Trace-check harness** (in `Namotion.Interceptor.OpcUa.Tests`): an opt-in trace collector plus a `[Trait("Category","Formal")]` test that runs the integration suite with collection on and then runs TLC trace-checking over the collected traces.
- **Skill: deferred.** Write a skill only after the procedure has repeated and stabilized (end of iteration 1, or the first roll to transactions). Likely one cohesive skill ("author-and-verify-a-formal-model") pointing at the process doc for concepts, not several. Writing it now would bake in unvalidated guesses.

Each iteration gets its own spec and plan; there is no single mega-plan.

## Phase 1: model-check the design

Write `OpcUaClient.tla` and `OpcUaClient.cfg`, run TLC with small bounds (2 to 3 items, up to 2 reconnect cycles). Fix any design bug TLC finds, in the model or the code, before touching instrumentation. No code changes and no test dependency in this phase.

## Phase 2: trace validation

- **Hook:** `OnCurrentSessionChanged(previous, current)` plus the reconnect metrics counters. Every session transition already flows through this single point, and tests already subscribe to it.
- **Collection:** opt-in (behind a flag or test category so normal runs are unaffected). Emit one JSON line per abstract transition.
- **Check:** a `[Trait("Category","Formal")]` test runs the existing integration suite with collection on, then feeds the traces to TLC in trace-checking mode and asserts each is a legal behavior of the model. The spec is the golden artifact; the assertion is conformance, not equality against a stored trace.

Existing integration coverage that will generate traces: initial connect, server-restart reconnect, instant-restart (transfer path), stall-then-recover, large resync, concurrent-change-during-reconnect (`src/Namotion.Interceptor.OpcUa.Tests/Integration/...`), driven by the shared in-process test server (`SharedOpcUaServerFixture`).

## Iteration 2: value convergence (deferred, sketched for continuity)

Model side is an incremental extension, not a rewrite:
- Add `serverValue[item]` and `clientValue[item]` over a tiny abstract domain (2 to 3 tokens).
- Add actions `ServerChangesValue`, `NotificationDelivered` (delayed or dropped allowed), and the buffer-then-replay semantics of manual reconnect.
- Add the quiescent-convergence invariant: once the link is up and the server stops changing and all in-flight notifications settle, `clientValue == serverValue` for every item.

Instrumentation side is the larger increment: it needs two or three more hooks (client apply path `SubjectPropertyWriter`/`SetValueFromSource`, server-side change, buffer start/stop) and a scheme to map concrete values into the abstract domain, or better to record the relation "client applied the value the server held at sequence S." That correlation is the fiddly new work.

Gotcha: adding values multiplies the state space, so the lifecycle bounds will likely need to shrink (fewer reconnect cycles) to keep TLC tractable. Iteration 1 is authored as the extension base (keeping `lastChangeSeq` and `buffering`) so this is a retune, not a rewrite.

## Post-iteration workflow

Per-iteration loop (repeated for each concern):
1. Extend the model with the next concern.
2. Model-check with TLC first; fix design bugs before instrumenting.
3. Extend the instrumentation.
4. Re-run the trace-check until green.
5. Commit model and instrumentation together.

Two expansion axes:
- **Deepen the OPC UA client:** iteration 2 value convergence, iteration 3 polling / read-after-write / multi-client.
- **Roll the pattern to the next component:** the skeleton (`docs/formal/<component>/`, a trace collector, a `[Trait("Category","Formal")]` test) is copy-adaptable. Transactions are the next target, written model-first since their implementation is still forming.

Living-spec maintenance loop (the payoff): once the trace-check is a CI gate, editing the client can make a recorded trace stop conforming, turning the formal test red. That forces a decision: real regression (fix the code) or intended behavior change (update the model, re-run TLC to confirm invariants still hold, commit both). The model cannot silently drift from the code. When TLC finds a counterexample, that trace becomes a new integration regression test, so model-checking feeds the suite too.

## Non-goals

- Proving the C# implementation correct directly. There is no automatic path from `.cs` to proof; trace validation is the only bridge, and it is bounded by the executions the tests exercise.
- Modeling value convergence, polling, read-after-write, or multi-client conflict in iteration 1.
- A single plan spanning all iterations.
- Writing a skill before the procedure is proven.

## Open decisions

1. **Java (resolved):** rootless toolchain under `tools/tla/` (pinned `tla2tools.jar` plus portable Temurin JRE 17, SHA-256 verified, no Docker or sudo), proven with the self-test. CI uses `actions/setup-java` plus the same `tools/tla/tlc` wrapper.
2. **Branch and commit:** this spec and the `tools/tla/` toolchain are authored on `design/opcua-master-comparison-loader`, a different topic. Per the no-commit-to-existing-branches rule, they are left uncommitted pending a decision to commit them on their own branch (for example `design/opcua-formal-model`).
