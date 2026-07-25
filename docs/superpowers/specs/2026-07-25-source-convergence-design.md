# Source convergence design

Status: draft for review (revision 2)
Base branch: `feature/outbound-write-map` (master + #388)
Supersedes: PR #355, and the outbound write map design (revisions 1 to 14, withdrawn)

## Why this is a rewrite

The withdrawn design maintained a belief about what each source holds by **inferring** it: advance a register from "I sent B and the write returned success," then order those inferences against local commit sequences. Fourteen revisions and a deep review established that this cannot work. The local commit counter answers exactly one question ("did a local write happen after point X"), while the inference also needed source-observation freshness (the counter does not move when the remote changes) and source causality (whether a write landed before or after a source update). Connectors compound it by reporting success for changes they never transmitted.

The decisive property: **an inferred belief is not self-healing, an observed belief is.** A wrong inference persists forever; a wrong observation is corrected by the next observation.

This design keeps the half that was sound (bounded, coalescing outbound intent so no local write is lost) and replaces the inference layer with observation.

## The guarantee

**Quiescent convergence.** Once local writing stops and the transport is healthy, the local model and the source agree, or the property is visibly marked as not in sync.

**Not guaranteed: instantaneous agreement.** In a local-first model a write is applied locally before the source sees it. Consumers needing agreement before the local model changes use source transactions (write-through), which already exist.

**Never silently wrong.** Where a transport or a property cannot support the guarantee, that property reports the fact rather than claiming to be in sync.

## Core principle

> **Sends update intent. Observations update belief. Never conflate them.**
>
> `observedValue` is written only from an observation of the source: a read, a subscription or monitored-item update, or an echo. It is never written from "what I sent," whatever the write call returned.

One honest qualification, corrected from revision 1: the design does keep a **baseline**, but it is a *stored past observation*, not an inference (see Intent entry). Claiming the baseline was eliminated was wrong; what was eliminated is deriving it from write results.

## Architecture

### Intent entry

Per owned property, while a local write is outstanding:

```
desired             // value to send, from the highest-commitSeq local change
commitSeq           // orders local writes against each other
observedAtCapture   // belief value when this intent was created (the baseline)
state               // see lifecycle below
attempts            // retry budget consumed
```

Bounded by owned-property count, one entry per property, so a chatty property cannot evict an unrelated pending write and a slow transport cannot grow the buffer without bound.

`observedAtCapture` is what discriminates "my write never landed" from "the source moved" at reconcile time. Belief itself is a single slot per property that a reconnect load overwrites, so the pre-outage value must be captured into the intent when the intent is created; otherwise the convergence check has no baseline and must guess (re-sending always violates source-wins; adopting always silently discards local writes).

### Intent lifecycle

The withdrawn design's confirmation modes were not removed by observation-only belief, only relocated: deciding **when an intent is finished** is itself a confirmation decision. It is specified here explicitly, because both obvious answers fail.

| State | Meaning | Leaves when |
|---|---|---|
| `Dirty` | Captured, not yet sent | Flush picks it up |
| `InFlight` | Send in progress | Send returns a per-change outcome |
| `AwaitingObservation` | Send reported `Accepted`; waiting for an observation confirming the source holds it | A confirming observation arrives, the source is observed to hold something else, or the grace window expires |

Transitions:

- `Accepted` → `AwaitingObservation`. **Not cleared**: on every transport, observation lags acknowledgement (OPC UA readback or the next sample; MQTT and WebSocket an echo round trip). Clearing on send success makes the property look quiescent immediately, so the convergence check sees a stale belief, concludes "source unchanged," and re-sends. That is an infinite redundant-write loop against a healthy server.
- `Transient` or `NotAttempted` → `Dirty`, retried on backoff.
- `PermanentlyRejected` → `Diverged` (below), intent resolved.
- `AwaitingObservation` + observation equals `desired` → intent cleared, property `InSync`.
- `AwaitingObservation` + observation differs and is newer than `observedAtCapture` → the source moved; resolve by divergence policy.
- `AwaitingObservation` + **grace window expires** → `Dirty`, `attempts` incremented. The grace window is per transport (OPC UA: revised sampling interval plus a buffer; MQTT and WebSocket: a round-trip estimate). Waiting for an observation with no timeout is the mirror failure: a lost QoS 0 echo would leave the property `Pending` forever, and since the convergence check requires quiescence it would never run.
- `attempts` exceeds the retry budget → `Diverged`. This is what makes `Diverged` reachable on transports that cannot report per-item rejection (WebSocket, MQTT), where an unwritable property would otherwise re-send forever. Without it, #332 is reintroduced everywhere except OPC UA.

### Belief register

Per owned property `{ observedValue, observedAt, observationSource }`, written only by observation paths. Invalidated wholesale when a load begins buffering, because a reload is authoritative and a carried-over belief would be stale against the new snapshot.

### Convergence check

Runs when the property is **quiescent**: no intent entry in any state **and the capture queue is drained**. Queue-empty is part of the predicate because a committed local write can sit undrained while the intent map is empty; without it the check reads a false quiescence, adopts a source value under policy, and is then overwritten by the write it did not see. The check therefore runs on the capture loop after a drain, not on an independent timer.

For each property with a current observation:

- `model` equals `observedValue` (per the comparer): `InSync`.
- Differs, and `observedValue` equals the intent's `observedAtCapture`: the source has not moved, our write did not land. Re-send.
- Differs, and `observedValue` has moved: resolve by divergence policy.
- No current observation: `NotVerifiable`. Never `InSync`.

It runs after every load on **all four** reload paths (see Reconnect paths), and on an idle interval (see Observability classification).

An in-place reconnect must force a **fresh readback**, not merely run the check: comparing a stale model against an equally stale belief reports `InSync` and detects nothing.

### Value comparison

The guarantee rests entirely on `model` versus `observedValue`, so the comparer is part of the design, not an implementation detail. Exact equality is wrong here:

- `OpcUaValueConverter` exists because representations differ; an exact comparison on a converted floating point value diverges permanently.
- Arrays and collections need structural comparison.
- Deadbanded properties differ **by design**: the server is instructed to withhold small changes.
- The two OPC UA inbound paths currently produce differently converted values (`PollingManager` omits `ConvertToPropertyValue`, unlike `SubscriptionManager`); that is a bug to fix regardless, listed under Independent fixes.

Therefore: a per-source comparer with per-mapping numeric tolerance, defaulting to exact for reference and integral types, structural for arrays, and for OPC UA a tolerance that must be at least the configured deadband. Convergence is scoped to **leaf value properties**; subject-valued and collection-valued properties (set by the loader via `SetValueFromSource`) are outside the mechanism and report `NotVerifiable`.

### Observability classification

Per property, maintained across reconnects: `Subscribed`, `Polled`, `LoadOnly`, or `Unobservable`. A property is `Unobservable` when its monitored item was dropped after a permanent failure, when `DataChangeTrigger` suppresses value notifications, or when a deadband exceeds the comparer tolerance. These are OPC UA's structural equivalent of MQTT's unretained topics, and they map to `NotVerifiable`.

Because `LoadOnly` and `Unobservable` properties produce no steady-state observations, the **idle convergence interval defaults on for OPC UA**. It is the only mechanism that catches drift no event reports, and asserting divergence detection while defaulting it off is a contradiction.

## Property sync state

Derived from the intent map and belief register, so no new per-property storage:

| State | Meaning |
|---|---|
| `InSync` | Quiescent and `model` compares equal to `observedValue` |
| `Pending` | Intent outstanding in any lifecycle state |
| `Diverged` | Permanently rejected, or the retry budget is exhausted |
| `NotVerifiable` | No observation channel for this property |

This is a **different axis** from #354's `SourceState`, not an extension: #354 answers a connection-lifecycle question ("initial load complete, live updates flowing"), these answer value agreement. They diverge exactly where it matters, since a permanently rejected write leaves a property `Diverged` while its source is legitimately `Synchronized`. This design computes the per-property truth; #354's event stream and wait primitives are the natural way to publish it.

### Divergence policy (configurable)

A property is **always** marked `Diverged` and surfaced in diagnostics. What happens to the model value is configurable per source, with a per-property override:

- `RevertToSource` (**default**): adopt the observed value. The model then tells the truth about the device, and the fault records that the write was rejected. This is the safe default for industrial control, where an HMI showing a setpoint the PLC never accepted is the dangerous failure mode. It also preserves today's source-wins reconnect semantics, so it is the migration-compatible default.
- `KeepLocal`: retain the rejected value and stay `Diverged`, for consumers that prefer to hold intent and resolve manually.

## Transport tiers

**OPC UA (reference tier).** Per-item `StatusCode` gives an exact outcome per write including transient versus permanent classification (available today but currently discarded, see Prerequisites). `LoadInitialStateAsync` is the readback mechanism: a complete, batched read of every owned property with `maxAge: 0`. Monitored items give push observations for successfully created, non-deadbanded items. Note the connector uses `SourceTimestamp`, which is optional in OPC UA and omitted by some servers, so no ordering rule may depend on it being present.

Correction from revision 1: `ReadAfterWriteManager` is **not** the readback path. It registers a property only when the requested sampling interval is exactly `0` and the server revised it upward; the default sampling interval is `null`, so under default configuration it tracks zero properties despite `EnableReadAfterWrite` defaulting to true. Where it is active it also discards a readback whose `SourceTimestamp` predates the local write timestamp, which would throw away precisely the observation that proves divergence. Building on it as revision 1 proposed would have shipped a feature that silently does nothing.

**WebSocket.** Sender-inclusive broadcast gives echoes; the server snapshot gives readback at connect. No per-item rejection, so `Diverged` is reached only through the retry budget.

**MQTT**, per topic mapping, since QoS is mapping-specific:

| Configuration | Convergence |
|---|---|
| QoS 1 or 2, retained | Full: acknowledgement plus readback from the retained message |
| QoS 0, retained | Converges **absent echo loss concurrent with a third-party write**. A dropped publish alone self-heals (no echo, grace expires, re-send). The uncovered case: a third party publishes C, our echo of B is dropped, model and belief both settle on C while the broker retains B, and nothing re-observes on a healthy connection. |
| QoS 0, not retained | `NotVerifiable`. No retention means no observable source state |

MQTT has no load at all (`LoadInitialStateAsync` returns null) and retained arrival has no completion signal, so "the check runs after every load" has no anchor there; MQTT relies on the idle interval and echo grace instead.

## Staging: where observations are captured

Every inbound apply must reach the belief register, and it cannot be fed from the change stream. Three defeaters:

1. `PropertyValueEqualityCheckHandler` is `[RunsFirst]` and vetoes writes whose value equals the current model, publishing nothing. That case, an inbound value **equal** to the model, is the single most valuable observation ("the source confirms we agree") and it is invisible downstream.
2. `FinalizeOrigin` demotes an origin to `Local` when a hook or validator transforms the value, so a source observation can arrive looking like local intent, both losing the observation and injecting phantom intent that echoes back.
3. Buffering delays applies and `StartBuffering` discards the pending list.

All inbound paths funnel through `SubjectChangeContextExtensions.SetValueFromOrigin`, which sits below `SubjectPropertyWriter` and therefore also covers the paths that bypass the writer (OPC UA read-after-write, the loader's structural applies). **Belief is staged there, unconditionally, before the interceptor chain runs.** This resolves what revision 1 listed as an open item; it has one sound answer, not a choice. Constraints: it lives in `Namotion.Interceptor.Tracking` while the register lives in `Namotion.Interceptor.Connectors` (needs a context-registered abstraction), it is on a hot path, and it must not allocate per call.

## Derived properties

Source-bound derived properties are **forbidden**, enforced at claim time with a clear error. A derived property with a setter can currently be claimed, and `FinalizeOrigin` unconditionally demotes derived writes to `Local`, so every inbound apply would register as local intent, echo to the source, and return as another apply: the property would never quiesce and its convergence check would never run. `GetFinalValue()` additionally re-evaluates derived getters outside `SyncRoot`, so a change's value is not the value committed at that sequence. Forbidding the combination removes the whole class for a few lines.

## Reconnect paths

There are **four** distinct reload paths, and the convergence check plus a fresh readback must run on all of them:

| Path | Reapplies queued writes today |
|---|---|
| `SubjectSourceBase.ExecuteAsync` (base retry loop) | yes |
| `OpcUaSubjectClientSource.ReconnectSessionAsync` | no |
| `SessionManager.PerformFullStateSyncIfNeededAsync` | no |
| `SessionManager.AbandonCurrentSession` (buffers, defers load) | no |

Two hazards to design around: `StartBuffering` replaces the pending list, so belief must be invalidated at buffer start rather than carried; and overlapping loads can apply out of order (`LoadInitialStateAndResumeAsync` invokes the apply action before its already-replayed early return), so observations must be stamped with a load generation and out-of-generation observations rejected.

## Prerequisites

1. **Commit-sequence stamp** on each change, taken in the terminal's `SyncRoot` section and threaded through `PropertyWriteContext` (the write-timestamp pattern). Orders local writes against each other only. This is #385's near-term item. Benchmark-gated: it grows `SubjectPropertyChange`, which is copied on every enqueue and dedup pass. **Open question to measure:** a process-wide counter is comparable across subjects but puts a shared cache line on the hottest path; a per-subject counter is cheaper. Measure before choosing.
2. **Per-change write outcomes.** `WriteResult` must report, for every submitted change, exactly one of `Accepted`, `PermanentlyRejected`, `Transient`, `NotAttempted`. Today OPC UA returns `Success` when every change was skipped as unmapped or unwritable, and skipped changes appear in neither the success count nor `FailedChanges`; MQTT skips unmapped and unserialisable changes the same way. This is a public API and snapshot change on `WriteResult`, and it touches all connectors (MQTT and WebSocket can express only `Accepted`, `Transient`, `NotAttempted`).
3. **Two-predicate status classification** for OPC UA. The existing classifier is tuned for the subscription path, where `BadUserAccessDenied` is deliberately transient because access levels are mutable and a monitored item can heal. For a write it is exactly the permanent case. This is a semantic split of a type with two callers whose correctness conditions differ, not a mechanical exposure.
4. **Per-property ownership epoch.** The real trigger is not release and reclaim (`SourceOwnershipManager.ReleaseSource` has no production caller); it is a **NodeId rebind across reload while ownership persists**: `Reset()` clears the node-id property data but leaves properties owned, and a later load can bind a different NodeId and re-claim idempotently. An epoch fences pending intent and stale belief across that rebind.

## Phasing

One design, two phases. Whether they ship as one PR or two is decided at implementation time; both are release-safe alone.

**Phase A: intent, without convergence claims.** The intent map (capture, coalesce, retry, bounded), per-change write outcomes, the two-predicate classifier, the ownership epoch, and the commit-sequence stamp. Delivers: no local write silently lost, bounded outbound memory, permanent failures surfaced once instead of retried forever. Makes **no** convergence guarantee, so none of the intent-lifecycle grace or comparer questions gate it. `WriteRetryQueue` and `ReapplyRetryQueue` are replaced here.

**Phase B: belief and convergence.** The belief register, staging at `SetValueFromOrigin`, the convergence check, sync states, the divergence policy, the comparer, observability classification, and the idle interval. Delivers the quiescent convergence guarantee and the `Diverged` and `NotVerifiable` states.

A later **phase C** applies the same capture and coalescing split to servers (no belief, no convergence: a server publishes, it does not synchronise), closing the server half of #281.

## Disposition of existing work

Update these when the design is accepted.

### Pull requests

| PR | Action |
|---|---|
| **#355** capture user writes during connect | **Close as superseded.** Its problem is solved by Phase A; its review findings are folded here. |
| **#333** drop permanent OPC UA write failures | **Close as superseded by Phase A**, which fixes the same defect properly via per-change outcomes rather than filtering at the queue. |
| **#372** correction origin kind | **Close.** No fourth `ChangeOrigin` kind is needed: divergence is detected by observation comparison, not provenance. |
| **#375** value assertion writes | **Re-evaluate for closure.** Revision 1 said detection must survive; with belief staged at `SetValueFromOrigin` *before* the equality handler, the equality-suppressed divergence this PR targets is now detected by the convergence check. Verify that claim, then close if it holds. |
| **#349** transaction divergence repair | **Re-scope.** The repair action (source-wins resync via a load) becomes automatic. Keep the transaction-layer failure classification and `SourceDivergenceException` reporting. |
| **#354** source sync state | **Keep, coordinate.** Different axis; it publishes what Phase B computes. Two notes to add: `PendingWriteCount` changes meaning (today "writes that failed", under Phase A "properties with outstanding intent", routinely non-zero, so existing alerts on `> 0` will fire) and it should expose the value-agreement axis alongside `SourceState`. |
| **#353** ChangeQueueProcessor overflow policy | **Re-scope to servers and history stores.** Per-property coalescing removes overflow from the source path. |
| **#370** sources own their write lock | **Coordinate.** Phase A and B both use the per-source write lock; agree its ownership before either lands. |
| **#358** TLA+ model of the OPC UA client lifecycle | **Extend.** The intent lifecycle and convergence check are exactly the kind of state machine this model should cover; a formal check of "quiescent implies converged or marked" would be strong evidence. |
| **#313** batch OPC UA browse and read | **Coordinate.** Phase B's readback uses the same read path. |
| **#209** burst flattening | **Re-scope or close.** Per-property coalescing subsumes it for sources; only the server path may still need it. |

### Issues

| Issue | Action |
|---|---|
| **#362** internal reconnects skip the reconcile | **Split.** The liveness half (queued writes never retried after an in-place reconnect) is fixed by an independent stopgap PR now. The correctness half (source-wins not enforced) closes with Phase B. |
| **#332** permanent write failures retried forever | **Closes with Phase A** (per-change outcomes plus `Diverged`). |
| **#281** unbounded memory under slow connectors | **Sources close with Phase A; servers with phase C.** |
| **#385** commit-order sequence numbers | **Near-term stamp half is a Phase A prerequisite.** In-order delivery stays out of scope. |
| **#352** overflow policy | **Re-scope with #353.** |
| **#363** source-inert supersede path | **Close with #355**; it describes a #355-only artifact. |
| **#195** expose connected plus in-sync state | **Served by Phase B plus #354.** Link it. |
| **#277** expose retry and queue depths in diagnostics | **Re-scope**: the depths it names change meaning; replace with intent-map and sync-state diagnostics. |
| **#299** track data value status codes in diagnostics | **Coordinate** with per-change outcomes and the two-predicate classifier. |
| **#342** source consistency contract | **Partially served**, not defined here. The transport tiers and state model are inputs to it. |
| **#282**, **#228**, **#200** lossless delivery | **Out of scope**, unchanged: a separate projection over the same subscription. |
| **#373** inbound ordering | **Out of scope.** |

### Independent fixes to file and land separately

These are real defects found while reviewing, none of which depend on this design:

1. **#362 liveness stopgap**: flush the retry queue after an in-place reconnect. All four reload paths funnel through `LoadInitialStateAndResumeAsync`, and `SubjectSourceBase` owns the `SubjectPropertyWriter`, so a post-resume hook covers them with no connector edits. Uses the existing semaphore-guarded, `IsEmpty`-short-circuited `FlushAsync`, so it adds no concurrency hazard: it performs promptly what the next local write would have performed anyway. Does **not** attempt the reconcile half, which needs the drain and locking work Phase B provides.
2. **`PollingManager` omits `ConvertToPropertyValue`**, unlike `SubscriptionManager`, so the same property yields differently typed values depending on which inbound path delivered it.
3. **Unguarded `(DateTimeOffset)result.SourceTimestamp` cast** in `ReadAfterWriteManager`: a server that omits the source timestamp yields `DateTime.MinValue` with unspecified kind, which throws in a positive UTC-offset host.

## Test plan

The five reliability properties are testable statements, not assertions, and each needs coverage before the corresponding phase ships.

Two harness gaps must close first: `FaultType` offers only `Kill` and `Disconnect`, so reject-write and read-only-node faults do not exist (a test model with a getter-only server property and a setter-bearing client property yields a genuine `BadNotWritable` with no new machinery); and `ConvergenceChecker` compares whole snapshots, so it cannot express a legitimately diverged property and needs an expected-state channel.

Then, per property: writes during an outage all land after recovery (including more distinct properties than the old queue bound, proving coalescing replaced eviction); an in-place reconnect with **no** subsequent local change still delivers queued writes (the #362 regression, which fails today); an A to B to A sequence does not push a stale local value over a changed server value; a dropped monitored item reports `NotVerifiable` rather than `InSync`; a deadbanded sub-tolerance change does not report a false `Diverged`; a read-only server node produces exactly one `PermanentlyRejected`, is not retried, reports `Diverged`, and honours both divergence policies; each of the four reload paths runs the check; a write in flight during a kill lands exactly once; and a long chaos run shows bounded intent-map size with every owned property in exactly one of the four sync states.

## Open items

1. **Grace window and retry budget defaults** per transport. OPC UA can derive the grace from the revised sampling interval; MQTT and WebSocket need a round-trip estimate or a configured value.
2. **Idle convergence interval defaults**: on for OPC UA, and what interval; likely off for MQTT unretained where it cannot help.
3. **Commit-counter scope** (process-wide versus per-subject), to be settled by measurement, see Prerequisites.
4. **Diagnostics and metrics surface.** The connector currently has only source-level counters and no `Meter` or `ActivitySource`. Production needs counts by sync state, an enumeration of diverged properties with model, observed value, observation age and last rejection status, and rate limiting so a divergence storm across thousands of properties does not flood logs.
5. **`WriteRetryQueueSize` obsoletion.** Public and snapshot-pinned on OPC UA and MQTT, and a `SubjectSourceBase` constructor parameter, with no analogue under a per-property bound. Keep it, mark it obsolete with a message, make it a documented no-op, and land that as its own release-safe change.
