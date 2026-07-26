# Source convergence

Status: proposed
Scope: the outbound path from the local model to an external source, and the verification that the two agree
Supersedes: PR #355

## Summary

Local writes to a source-bound property are lost, silently dropped, or retried forever, and there is no way to tell whether a property's value actually matches its source. This design replaces the outbound write path with three pieces: a bounded per-property **intent** map holding what we want the source to have, a **belief** register holding what we last observed the source to have, and a **convergence check** that compares the model against belief once writing settles and either repairs the difference or reports it.

The organising rule is that **send outcomes update intent, observations update belief, and a send outcome never updates belief**. An acknowledgement proves a request was accepted; it does not prove what the source now holds, and on two of three transports it does not even prove transmission.

The work lands in three steps. Step 1 fixes confirmed data-loss bugs and needs no new design. Step 2 replaces the write retry queue without changing reconnect semantics. Step 3 adds belief and the convergence guarantee, and is gated on exhaustive enumeration of its state machine rather than on further review.

## Problems this addresses

All confirmed against the code on `master`.

| Problem | Evidence |
|---|---|
| Connectors report `Success` for changes they never transmitted, so writes are silently lost | OPC UA returns `WriteResult.Success` when every change was skipped as unmapped or unwritable, and skipped changes appear in neither the success count nor `FailedChanges`; MQTT skips unmapped and unserialisable changes the same way |
| Permanently rejected writes are retried for the lifetime of the process (#332) | The transient/permanent classification is computed but fed only to log counters; `WriteRetryQueue.FlushAsync` requeues everything |
| Queued writes are **never retried** after an in-place reconnect (#362, liveness) | `ReapplyRetryQueue` is called only from the base retry loop; the OPC UA, WebSocket and MQTT in-place reconnect paths do not call it, so queued writes move only as a side effect of the next local write |
| A chatty property evicts an unrelated pending write | `WriteRetryQueue` is a fixed drop-oldest ring |
| Writes during the connect and reconnect-delay windows are lost | The outbound subscription is created inside the change-queue processor, after listen and load |
| Outbound memory is unbounded under a slow transport (#281) | The change-queue buffer grows in proportion to flush duration times mutation rate |
| A stale local write can overwrite a source value that changed during an outage (#362, correctness) | The reconcile runs only on the base-loop path |
| There is no way to tell whether a property is in sync (#195) | Source state reports connection lifecycle, not value agreement |
| The same property yields different value types depending on the inbound path | `PollingManager` omits the `ConvertToPropertyValue` call that `SubscriptionManager` performs |
| Read-after-write is enabled by default but tracks nothing | It registers a property only when the requested sampling interval is exactly `0` and the server revised it upward; the default interval is `null` |
| Reading back a value from a server that omits source timestamps throws | Unguarded `(DateTimeOffset)result.SourceTimestamp` cast in `ReadAfterWriteManager` |

## Guarantee

**Quiescent convergence.** Once local writing stops and the transport is healthy, the local model and the source agree, or the property is visibly marked as not in sync.

Explicitly **not** guaranteed:

- **Instantaneous agreement.** A local-first write is applied locally before the source sees it. Consumers needing agreement before the local model changes use source transactions, which already exist and are write-through.
- **Exactly-once delivery.** Every transport can accept a write whose response is lost, so a retry may duplicate the operation. Final-value writes are idempotent; the promise is eventual final-value convergence with bounded retries.
- **Convergence on transports that cannot be observed.** Those properties report `NotVerifiable` rather than claiming to be in sync.

## Why belief must be observed, not inferred

An earlier version of this design maintained belief by inference: advance a register from "I sent B and the write returned success," then order those inferences with local sequence numbers. That cannot work, and the reason is worth recording so it is not re-proposed.

The only clock available locally is the commit counter. It answers "did a local write happen after point X." It cannot answer whether one source observation is fresher than another (the counter does not move when the remote changes), nor whether a write landed at the source before or after a source update (that is remote causality, and no transport here provides a portable source-side sequence). Connectors compound the problem by reporting success for untransmitted changes.

The decisive property: **an inferred belief is not self-healing; an observed belief is.** A wrong inference persists forever. A wrong observation is corrected by the next observation.

What this does **not** eliminate is sequencing. Commit, dispatch and capture are asynchronous in this codebase, so ordering local commits against each other, and against the moment an observation was recorded, remains necessary. That is a property of the write pipeline and survives any belief model.

## Model

### Clocks and identifiers

- **`commitSeq`** is a per-subject monotonic counter stamped at the terminal write inside `Subject.SyncRoot` and threaded through `PropertyWriteContext`. This is the near-term half of #385.

  *Decision: per-subject, not process-wide.* Every comparison this design makes is between events on a single property, hence a single subject: local commits against each other, and belief against intent. A process-wide counter would add a contended cache line to the hottest write path in the library and buy nothing.

- **`observedAtSeq`** is the value of that same counter read when an observation is staged. It places observations on the same timeline as commits, which is what lets the machine ask "did the source report something after this local write committed?" without needing source-side causality.

- **`epoch`** is a per-property pair of counters, `ownershipEpoch` and `connectionEpoch`, **carried on the observation itself**, captured when the subscription, poll or load that produced it was created. A per-property epoch alone cannot reject a callback from an abandoned session or an observation from an older overlapping load, so the epoch must travel with the event.

### Per-property state

```
belief (present once observed)
    observedValue
    observedAtSeq
    epoch

intent (present while a local write is outstanding)
    desiredValue, desiredSeq        latest local intent
    attemptValue, attemptSeq        the in-flight or awaiting attempt, if any
    baselineValue, baselineSeq      belief as of the FIRST local commit of this intent
    attempts                        retry budget consumed
    epoch                           epoch when the intent was created

fault (persistent, survives intent resolution)
    lastRejection                   value, status, timestamp; acknowledgeable
```

Three of these fields exist for reasons that are easy to miss:

- **`attemptValue`/`attemptSeq` are separate from `desired`** because a local write can commit while an earlier attempt is in flight. An acknowledgement or echo concerns the attempt, and must neither clear nor reject the newer intent.
- **`baselineSeq` is belief at the first local commit, not at capture.** Capture is asynchronous, so an observation can arrive between commit and capture. Recording belief at capture time would conclude "the source has not moved" about a value that arrived *after* our write, and push a stale local value over newer source state.
- **The fault record is persistent storage.** A permanent rejection resolves the intent but must stay visible, and under the default divergence policy the model is made equal to belief, so the fault cannot be derived from a value comparison.

### States

| State | Meaning |
|---|---|
| `Clean` | No intent outstanding |
| `Dirty` | Intent captured, not currently being sent |
| `InFlight` | An attempt is being sent |
| `Awaiting` | An attempt was accepted; waiting for an observation confirming the source holds it |

`Diverged` is not a state: it is a derived report, because a property can be diverged with or without an intent and with or without a fault.

## State machine

The machine is specified per state with explicit guards, because the recurring defect in earlier drafts was an event occurring in a state nobody had considered. Every state lists every event; there are no implicit cells.

The decision logic is a **pure function** `(state, event) -> (state', actions)` with no I/O, no clocks and no async, so it can be exhaustively enumerated in tests (see Validation). Performing the actions (sending, writing the model, arming timers) is a separate layer.

### Alphabet

`S`/`V` are the sequence and value of a local change; `O`/`P` the value and `observedAtSeq` of an observation; `A` an attempt sequence. `≈` is the value comparer. `budget` is the retry budget.

Events: `LocalCommit(S,V)`, `Observation(O,P)`, `SendPicked`, `Accepted(A)`, `Transient(A)`, `PermanentlyRejected(A,status)`, `NotAttempted(A)`, `GraceExpiry(A)`, `EpochChange`, `LoadComplete`, `TransactionConfirmed(S,T)`, `ConvergenceTick`.

**Precondition applied to every event:** an event whose epoch differs from the property's current epoch is discarded before it reaches the machine. An `Observation` with `P <= belief.observedAtSeq` updates nothing, because belief never regresses.

### State: `Clean` (no intent; belief and fault may exist)

| Event | Guard | Actions | Next |
|---|---|---|---|
| `LocalCommit(S,V)` | always | create intent `{desired=V, desiredSeq=S, baseline=belief.value, baselineSeq=belief.observedAtSeq, attempts=0, epoch=current}` | `Dirty` |
| `Observation(O,P)` | always | `belief=(O,P)` | `Clean` |
| `SendPicked` | no intent | ignore | `Clean` |
| `Accepted` / `Transient` / `PermanentlyRejected` / `NotAttempted` / `GraceExpiry` | always | ignore (orphaned outcome for a resolved intent) | `Clean` |
| `EpochChange` | always | invalidate belief; fault is retained | `Clean` |
| `LoadComplete` | always | apply staged observations as `Observation`; then `ConvergenceTick` | `Clean` |
| `TransactionConfirmed(S,T)` | always | create verification intent `{desired=T, desiredSeq=S, attempt=(S,T), attempts=0}` | `Awaiting` |
| `ConvergenceTick` | not quiescent | report `Pending` | `Clean` |
| `ConvergenceTick` | quiescent | run the convergence check below | `Clean` or `Dirty` |

### State: `Dirty` (intent exists, not being sent)

| Event | Guard | Actions | Next |
|---|---|---|---|
| `LocalCommit(S,V)` | `S > desiredSeq` | `desired=V`, `desiredSeq=S`; baseline unchanged | `Dirty` |
| `LocalCommit(S,V)` | `S <= desiredSeq` | ignore (reordered or duplicate) | `Dirty` |
| `Observation(O,P)` | `P > baselineSeq` and `O ≉ baselineValue` | `belief=(O,P)`; source moved since our write; **resolve by policy** | `Clean` |
| `Observation(O,P)` | `O ≈ desiredValue` | `belief=(O,P)`; source already holds our value; clear intent | `Clean` |
| `Observation(O,P)` | otherwise | `belief=(O,P)` | `Dirty` |
| `SendPicked` | write gate held, `desiredSeq` unchanged since selection | `attempt=(desiredSeq, desiredValue)`; emit send | `InFlight` |
| `Accepted` / `Transient` / `PermanentlyRejected` / `NotAttempted` / `GraceExpiry` | always | ignore (orphaned) | `Dirty` |
| `EpochChange` | always | drop intent (it targets a stale binding); invalidate belief | `Clean` |
| `LoadComplete` | always | apply staged observations as `Observation`, then evaluate as above | `Dirty` or `Clean` |
| `TransactionConfirmed(S,T)` | `S > desiredSeq` | replace intent with verification intent | `Awaiting` |
| `TransactionConfirmed(S,T)` | `S <= desiredSeq` | ignore | `Dirty` |
| `ConvergenceTick` | always | report `Pending`; no check | `Dirty` |

### State: `InFlight` (an attempt is being sent)

| Event | Guard | Actions | Next |
|---|---|---|---|
| `LocalCommit(S,V)` | `S > desiredSeq` | `desired=V`, `desiredSeq=S`; attempt untouched | `InFlight` |
| `LocalCommit(S,V)` | `S <= desiredSeq` | ignore | `InFlight` |
| `Observation(O,P)` | `P > baselineSeq` and `O ≉ baselineValue` | `belief=(O,P)`; mark intent for policy resolution when the attempt completes | `InFlight` |
| `Observation(O,P)` | otherwise | `belief=(O,P)` | `InFlight` |
| `SendPicked` | attempt already in flight | ignore | `InFlight` |
| `Accepted(A)` | `desiredSeq > A` | successor pending; do not await this attempt | `Dirty` |
| `Accepted(A)` | `desiredSeq == A` | arm the grace timer | `Awaiting` |
| `Transient(A)` | `attempts + 1 <= budget` | `attempts++`; schedule backoff | `Dirty` |
| `Transient(A)` | `attempts + 1 > budget` | record fault; **resolve by policy** | `Clean` |
| `PermanentlyRejected(A,status)` | `desiredSeq > A` | record fault; retry the newer value | `Dirty` |
| `PermanentlyRejected(A,status)` | `desiredSeq == A` | record fault; **resolve by policy** | `Clean` |
| `NotAttempted(A)` | always | never transmitted; do not consume budget | `Dirty` |
| `GraceExpiry(A)` | always | ignore (not awaiting yet) | `InFlight` |
| `EpochChange` | always | abandon attempt; drop intent; invalidate belief | `Clean` |
| `LoadComplete` | always | apply staged observations; attempt completes normally | `InFlight` |
| `TransactionConfirmed(S,T)` | always | abandon attempt (the transaction wrote under the gate); register verification | `Awaiting` |
| `ConvergenceTick` | always | report `Pending`; no check | `InFlight` |

### State: `Awaiting` (attempt accepted, awaiting a confirming observation)

| Event | Guard | Actions | Next |
|---|---|---|---|
| `LocalCommit(S,V)` | `S > desiredSeq` | `desired=V`, `desiredSeq=S`; the outstanding attempt now only updates belief | `Dirty` |
| `LocalCommit(S,V)` | `S <= desiredSeq` | ignore | `Awaiting` |
| `Observation(O,P)` | `O ≈ attemptValue` and `desiredSeq > attemptSeq` | `belief=(O,P)`; attempt confirmed; successor pending | `Dirty` |
| `Observation(O,P)` | `O ≈ attemptValue` and `desiredSeq == attemptSeq` | `belief=(O,P)`; attempt confirmed; clear intent | `Clean` |
| `Observation(O,P)` | `O ≉ attemptValue` and `P > attemptSeq` | `belief=(O,P)`; source moved after our attempt; **resolve by policy** | `Clean` |
| `Observation(O,P)` | otherwise (older or unrelated) | `belief=(O,P)` | `Awaiting` |
| `SendPicked` | attempt outstanding | ignore | `Awaiting` |
| `Accepted` / `Transient` / `PermanentlyRejected` / `NotAttempted` | always | ignore (duplicate or late) | `Awaiting` |
| `GraceExpiry(A)` | `A != attemptSeq` | ignore (stale timer) | `Awaiting` |
| `GraceExpiry(A)` | `A == attemptSeq`, `attempts + 1 <= budget` | `attempts++`; force a readback where the transport supports one | `Dirty` |
| `GraceExpiry(A)` | `A == attemptSeq`, `attempts + 1 > budget` | record fault; **resolve by policy** | `Clean` |
| `EpochChange` | always | drop intent; invalidate belief | `Clean` |
| `LoadComplete` | always | apply staged observations, then evaluate as above | per `Observation` |
| `TransactionConfirmed(S,T)` | `S > desiredSeq` | replace attempt with the transaction's | `Awaiting` |
| `TransactionConfirmed(S,T)` | `S <= desiredSeq` | ignore | `Awaiting` |
| `ConvergenceTick` | always | report `Pending`; no check | `Awaiting` |

### Sub-machine: resolve by policy

| Policy | Actions |
|---|---|
| `RevertToSource` (default) | sequence-guarded write of `belief.observedValue` to the model, which fails if a local commit newer than `desiredSeq` exists, so adoption cannot clobber a concurrent newer write; clear intent; retain any fault |
| `KeepLocal` | clear intent; leave the model; record the divergence |

In both cases the intent is cleared, so the machine lands in `Clean`. A failed guarded write means a newer local commit exists, whose own `LocalCommit` event creates a fresh intent.

### Invariants

These are the properties the enumeration in Validation asserts after every event.

| | Invariant |
|---|---|
| I1 | An intent exists if and only if the state is not `Clean` |
| I2 | In `InFlight` or `Awaiting`, an attempt is set; in `Clean` or `Dirty`, none is |
| I3 | `desiredSeq >= attemptSeq` |
| I4 | `belief.observedAtSeq` never decreases, and `attempts` never exceeds `budget` |
| I5 | Every local commit is eventually delivered, superseded by a newer commit, resolved by policy, or recorded as a fault. It is never silently dropped |
| I6 | Once quiescent with an observable property, the report is `InSync` or `Diverged`, and `InSync` implies `model ≈ belief.observedValue` |
| I7 | No send is emitted whose value is older than an observation the source produced after it, unless policy chose it |

## Convergence check

Runs per property when quiescent, reached only from `Clean`.

| Condition | Report |
|---|---|
| No belief, or classified `Unobservable` | `NotVerifiable` |
| Belief older than the staleness bound and the transport supports readback | force a readback, then re-evaluate |
| `model ≈ observedValue` | `InSync` (any fault record remains until acknowledged) |
| `model ≉ observedValue` | `Diverged`: create an intent to re-send, unless a fault already records this value, in which case report and do not retry |

The idle check must **produce a fresh observation before comparing** wherever readback exists. Comparing a stale model against equally stale belief reports `InSync` and detects nothing; this applies to the idle interval exactly as it does to reconnect.

### Quiescence fence

Quiescence is not "the intent map is empty and the change queue is empty." A write commits under `SyncRoot` and dispatches later, during interceptor unwind, so a committed write can be invisible to both. Adopting a source value in that window would overwrite a newer local commit.

The fence is a **commit-to-dispatch watermark**: the terminal records, per subject, the highest `commitSeq` whose dispatch has completed. A property is quiescent when it has no intent, the capture queue is drained, and the watermark has reached the subject's latest issued `commitSeq`. Adoption is additionally sequence-guarded, so even a misread of quiescence cannot lose a newer write.

This watermark is new core surface and is the single most important thing for model checking to validate.

## Value comparison

The guarantee rests on `≈`, so the comparer is part of the design. Exact equality is wrong here: `OpcUaValueConverter` means representations differ across paths, arrays need structural comparison, and deadbanded properties differ by design.

A per-source comparer with per-mapping tolerance: exact for reference and integral types, structural for arrays, configurable tolerance for floating point, and for OPC UA a tolerance that must be **at least the configured deadband**. Convergence is scoped to **leaf value properties**; subject-valued and collection-valued properties are `NotVerifiable`.

## Constraints on source-bound properties

Two property shapes cannot participate and are rejected at claim time with a clear error.

**Transforming or vetoing properties.** If the source reports `S` and a hook or validator stores `F`, belief records `S`, the model holds `F`, and `FinalizeOrigin` demotes the origin to `Local`. The property then never converges: adopting `S` produces `F` again, forever. Treating the transformed value as deliberate correction intent is expressible but doubles the semantics of every state above, and no connector needs it.

**Derived properties.** `FinalizeOrigin` unconditionally demotes derived writes to `Local`, so every inbound apply would register as local intent and echo back, and the property would never quiesce. `GetFinalValue()` additionally re-evaluates derived getters outside `SyncRoot`, so the published value is not the value committed at that sequence.

## Staging observations

Belief cannot be fed from the change stream. `PropertyValueEqualityCheckHandler` runs `[RunsFirst]` and vetoes writes equal to the current model, publishing nothing, and an inbound value **equal** to the model is the single most valuable observation, because it is the source confirming we agree. Buffering additionally delays applies, and `StartBuffering` replaces the pending list.

All inbound paths funnel through `SubjectChangeContextExtensions.SetValueFromOrigin`, which sits below `SubjectPropertyWriter` and therefore also covers the paths that bypass it (OPC UA read-after-write, the loader's structural applies). **Belief is staged there, unconditionally, before the interceptor chain runs**, recording value, `observedAtSeq` and epoch. The observation APIs gain an epoch parameter. It lives in `Namotion.Interceptor.Tracking` while the register lives in `Namotion.Interceptor.Connectors`, so it needs a context-registered abstraction, and it is on a hot path and must not allocate per call.

## Observability classification

Per property, maintained across reconnects: `Subscribed`, `Polled`, `LoadOnly`, `Unobservable`.

A property whose monitored item failed but which remains **readable** is `LoadOnly`, not `Unobservable`, because the idle readback still observes it. `Unobservable` is reserved for properties with neither readback nor push channel: a `DataChangeTrigger` suppressing values, or a deadband wider than the comparer tolerance with no read path.

Because `LoadOnly` properties produce no steady-state observations, the **idle convergence interval defaults on for OPC UA**. Asserting divergence detection while defaulting it off would be a contradiction.

## Reported state and divergence policy

| Report | Condition |
|---|---|
| `InSync` | Quiescent, belief present, `model ≈ observedValue` |
| `Pending` | Intent outstanding in any state |
| `Diverged` | Quiescent and `model ≉ observedValue`, or an unacknowledged fault exists |
| `NotVerifiable` | No belief and no observation channel |

This is a **different axis** from the source state proposed in #354, which answers a connection-lifecycle question. They diverge exactly where it matters: a permanently rejected write leaves a property `Diverged` while its source is legitimately synchronized. This design computes the per-property truth; #354 is the natural way to publish it.

**Divergence policy**, per source with a per-property override:

- **`RevertToSource` (default)**: adopt the observed value through the sequence-guarded write. The model then tells the truth about the device, and the fault records that the write was rejected. This is the safe default for industrial control, where an HMI showing a setpoint the PLC never accepted is the dangerous failure mode, and it preserves today's source-wins behaviour, making it migration-compatible.
- **`KeepLocal`**: retain the local value and stay `Diverged`.

Faults are acknowledgeable; acknowledgement clears the record but not a live value difference.

## Transports

**OPC UA, the reference tier.** Per-item `StatusCode` gives an exact per-write outcome including transient versus permanent. `LoadInitialStateAsync` is the readback mechanism: a complete, batched read of every owned property at `maxAge: 0`. Monitored items give push observations for successfully created, non-deadbanded items. The connector uses `SourceTimestamp`, which is optional in OPC UA and omitted by some servers, so **no rule may depend on it being present**.

`ReadAfterWriteManager` is not the readback path and must not be built on: it registers a property only when the requested sampling interval is exactly `0` and the server revised it upward, so under default configuration it tracks nothing, and where it is active it discards a readback whose source timestamp predates the local write, precisely the observation that proves divergence.

**WebSocket.** Sender-inclusive broadcast gives echoes; the server snapshot gives readback at connect. No per-item rejection, so faults are reached only through the retry budget.

**MQTT**, per topic mapping, since QoS is mapping-specific:

| Configuration | Convergence |
|---|---|
| QoS 1 or 2, retained | Full: acknowledgement plus readback from the retained message |
| QoS 1 or 2, not retained | Acknowledged delivery, no readback; steady-state verification unavailable → `NotVerifiable` for the idle check |
| QoS 0, retained | `NotVerifiable` unless periodic retained readback is enabled. A dropped publish alone self-heals, but a dropped echo concurrent with a third-party write leaves model and belief agreeing while the broker retains a different value, with nothing to re-observe on a healthy connection |
| QoS 0, not retained | `NotVerifiable`: no retention means no observable source state |

MQTT has no load (`LoadInitialStateAsync` returns null) and retained arrival has no completion signal, so the load-complete event does not occur there; MQTT relies on the idle interval and echo grace.

## Epochs

- **`ownershipEpoch`** increments on claim, release and **rebind**. The real trigger is not release-and-reclaim (`SourceOwnershipManager.ReleaseSource` has no production caller) but a **NodeId rebind across reload while ownership persists**: `Reset()` clears node-id property data but leaves properties owned, and a later load can bind a different NodeId and re-claim idempotently.
- **`connectionEpoch`** increments per session or connection, so a callback from an abandoned session is discarded.

A local change queued before an epoch change is rejected at capture by comparing its `commitSeq` against the epoch's fence sequence.

## Transactions

A transaction writes the source directly, so it must coordinate with the send loop. Today `WriteToSourcesAsync` returns before the local apply and the source write lock is scoped to a single call, so a pending send can interleave between a transaction's external write and its local apply, leaving the source holding the older value.

1. The transaction holds the per-source write gate from before its external write through local apply, rollback, and its map registration. The send loop acquires the same gate and performs its liveness re-check **after** acquiring it, so a send approved before a waiting transaction cannot emit a superseded value. Multi-source transactions acquire gates in a deterministic order.
2. On commit, the transaction registers an `Awaiting` verification intent per written property, so the convergence check does not revert a successful transaction before its echo arrives.

Rule 1 is not expressible on the current `ITransactionWriter` contract and requires an explicit lease API. That is new public surface, in step 3.

## Reconnect and reload paths

The convergence check plus a fresh readback must run on all of them:

| Path | Reapplies queued writes today |
|---|---|
| `SubjectSourceBase.ExecuteAsync` (base retry loop) | yes |
| `OpcUaSubjectClientSource.ReconnectSessionAsync` | no |
| `SessionManager.PerformFullStateSyncIfNeededAsync` | no |
| `WebSocketSubjectClientSource.ReconnectAndResumeAsync` | no |
| `MqttSubjectClientSource.OnReconnectedAsync` | no |

`SessionManager.AbandonCurrentSession` is not itself a reload; it buffers and clears the session, and a later path performs the load. Two hazards: `StartBuffering` replaces the pending list, so belief is invalidated at buffer start rather than carried across; and overlapping loads can apply out of order, which the load generation in `epoch` rejects.

## What we build

Three steps, each release-safe alone. Whether they ship as one pull request or several is decided at implementation time.

### Step 1: outcomes and confirmed bugs

- **Per-change write outcomes**: `Accepted`, `PermanentlyRejected`, `Transient`, `NotAttempted` for every submitted change. The representation must not allocate per successful change. External `ISubjectSource` implementations get a default mapping from the old all-or-nothing shape so they compile and behave as today.
- **Two-predicate OPC UA status classification.** The existing classifier is tuned for subscriptions, where `BadUserAccessDenied` is deliberately transient because access levels are mutable and a monitored item can heal; for a write it is exactly the permanent case.
- **Flush the retry queue after an in-place reconnect.** All reload paths funnel through `LoadInitialStateAndResumeAsync`, and `SubjectSourceBase` owns the writer, so a post-resume hook covers them with no connector edits. It calls the existing semaphore-guarded, empty-short-circuited flush, performing promptly what the next local write would have performed anyway.
- **`PollingManager`'s missing `ConvertToPropertyValue`.**
- **The unguarded source-timestamp cast** in `ReadAfterWriteManager`.

Closes #332, stops silent loss of skipped writes, and fixes the never-retried production bug. No dependency on the rest of this design.

### Step 2: intent, behaviour-preserving

The per-property intent map replaces `WriteRetryQueue` and `ReapplyRetryQueue`, with capture live from source start, plus the `commitSeq` stamp. **Explicitly preserves today's reconcile semantics** (the existing old-value heuristic as baseline) and makes no convergence claim, so it cannot regress source-wins behaviour.

Closes the connect-window loss, the drop-oldest eviction, and source-side unbounded memory.

### Step 3: belief and convergence

The belief register and staging hook, the commit-to-dispatch watermark, epochs on observation APIs, the sequence-guarded write, the transition table, the convergence check and quiescence fence, the comparer, observability classification, reported state and faults, and transaction coordination. Delivers the guarantee.

**Gated on exhaustive enumeration of the state machine**, not on further review.

### Step 4: servers

The same capture and coalescing split for servers, with no belief and no convergence, since a server publishes rather than synchronises. Closes the server half of #281.

## Validation

A state table removes undefined cases, but it cannot establish correctness under concurrent interleavings, which is where earlier drafts of this design were repeatedly wrong. Step 3 is therefore gated on **exhaustive enumeration of the state machine**, in the repository, not on further review.

Because the decision logic is a pure `(state, event) -> (state', actions)` function, a test can enumerate every event sequence up to a bounded depth from every reachable starting state and assert invariants I1 to I7 after each step. With four states and twelve events this is fast, and unlike an external model it exercises **the shipped code**, so it cannot drift from the implementation.

The interleavings to cover explicitly, expressed as event sequences: a commit whose dispatch is delayed past an observation; an observation arriving between commit and capture; an outcome for an obsolete attempt; overlapping loads producing out-of-order observations; an epoch change mid-flight; and a transaction confirming while a send is in flight.

This replaces an earlier plan to model the machine in TLA+ via #358. That is an unmerged experiment, and depending on it for a production gate would be fragile, would require TLA+ fluency to maintain, and would verify a model that can silently diverge from the code. If #358 lands independently, modelling the same machine there is a useful complement, but nothing here waits on it.

Bounded enumeration proves less than a model checker: it establishes "no violation within N events" rather than a general temporal proof. For a machine this small that is an acceptable trade for testing the real implementation.

### Tests

Two harness gaps close first. `FaultType` offers only `Kill` and `Disconnect`, so reject-write and read-only-node faults do not exist. A getter-only server property against a setter-bearing client property yields a genuine `BadNotWritable` with no new machinery. `ConvergenceChecker` compares whole snapshots and cannot express a legitimately diverged property, so it needs an expected-state channel.

Then: writes during an outage all land after recovery, with more distinct properties than the old queue bound; an in-place reconnect with **no** subsequent local write still delivers queued writes (fails today); an A→B→A sequence does not push a stale local value over a changed server value; a dropped monitored item is classified `LoadOnly` and caught by the idle readback; a sub-tolerance deadband change does not report a false `Diverged`; a read-only server node yields exactly one `PermanentlyRejected`, is not retried, records a fault, and honours both policies; every reload path runs the check; a write in flight during a kill converges to the final value with bounded retries; and a long chaos run shows bounded intent-map size with every owned property in exactly one reported state.

## Public API and configuration changes

| Change | Notes |
|---|---|
| `WriteResult` gains per-change outcomes | Public, snapshot-pinned; touches all connectors; default mapping provided for external implementations |
| `SubjectPropertyChange` gains `commitSeq` | Public struct, snapshot; benchmark-gated, since it is copied on every enqueue and dedup pass |
| Commit-to-dispatch watermark and sequence-guarded write | New core surface |
| Observation APIs gain an epoch parameter | Connectors updated |
| Staging abstraction registered on the context | New service in Tracking |
| Transaction lease API | New public surface on `ITransactionWriter`, step 3 |
| `WriteRetryQueueSize` obsoleted | Public and snapshot-pinned on OPC UA, MQTT **and WebSocket**, and a `SubjectSourceBase` constructor parameter. Keep it, mark it `[Obsolete]`, document it as a no-op, land separately |
| New configuration | Divergence policy and per-property override; comparer tolerance; grace window and retry budget; idle interval and staleness bound; observability overrides |

**Defaults**: retry budget 3 attempts; grace window derived from the revised sampling interval plus a buffer on OPC UA, and a configurable round-trip estimate elsewhere; idle interval on for OPC UA, off where it cannot help (MQTT unretained); divergence policy `RevertToSource`.

**Diagnostics**, which the connector currently lacks entirely (only source-level counters, no `Meter` or `ActivitySource`): counts by reported state, an enumeration of diverged properties with model value, observed value, observation age and last rejection, and rate limiting so a storm across thousands of properties does not flood logs.

## Related work

### Superseded or closed by this design

| Item | Disposition |
|---|---|
| **PR #355** capture user writes during connect | Close as superseded; step 2 solves it |
| **PR #333** drop permanent OPC UA write failures | Close as superseded by step 1, which fixes the cause rather than filtering the symptom |
| **PR #372** correction origin kind | Close: divergence is detected by observation, not provenance, so no fourth `ChangeOrigin` kind is needed |
| **#332** permanent failures retried forever | Closes in step 1 |
| **#362** in-place reconnect skips reconcile | Liveness half closes in step 1, correctness half in step 3 |
| **#281** unbounded memory | Sources in step 2, servers in step 4 |
| **#363** source-inert supersede path | Close with #355; it describes a #355-only artifact |
| **#195** connected plus in-sync state | Served by step 3 together with #354 |

### Coordinate before landing

| Item | Why |
|---|---|
| **PR #354** source sync state | Different axis; it publishes what step 3 computes. Note that `PendingWriteCount` changes meaning after step 2 (from "writes that failed" to "properties with outstanding intent", routinely non-zero), so existing alerts on `> 0` will fire |
| **PR #370** sources own their write lock | Step 3's transaction gate depends on where the lock lives |
| **PR #358** TLA+ model | **Not a dependency.** Step 3 is gated on in-repo enumeration instead. If #358 lands, modelling this machine there is a useful complement |
| **PR #313** batch browse and read | Step 3's readback uses the same path |
| **#385** commit sequence numbers | The stamp half is a step 2 prerequisite; in-order delivery stays out of scope |
| **#299** data value status codes in diagnostics | Overlaps per-change outcomes |
| **#277** retry and queue depth diagnostics | Those depths change meaning; re-scope to the new state |

### Re-scope

| Item | Why |
|---|---|
| **PR #349** transaction divergence repair | The repair action becomes automatic; keep the transaction-layer failure classification and reporting |
| **PR #375** value assertion writes | With belief staged before the equality handler, the equality-suppressed divergence it targets is detected by the convergence check. Verify, then close if it holds |
| **PR #353 / #352** overflow policy | Coalescing removes overflow from the source path; re-scope to servers and history stores |
| **PR #209** burst flattening | Subsumed for sources; only the server path may still need it |

### Unrelated, explicitly out of scope

| Item | Why it is not addressed here |
|---|---|
| **#282**, **#228**, **#200** lossless and non-deduplicating delivery | This design converges *current values*; delivering every intermediate value for signal, alarm and audit properties is a separate projection over the same subscription, with opposite overflow semantics |
| **#373** inbound ordering | A delayed inbound notification reverting a just-written value is an inbound-path concern; this design is outbound only |
| **#342** source consistency contract | The transport tiers and reported states are inputs to that contract, but defining it is a separate piece of work |
| **#367**, **#369**, **#378** plumbing consolidations | Independent refactors; no interaction beyond ordinary merge conflicts |
