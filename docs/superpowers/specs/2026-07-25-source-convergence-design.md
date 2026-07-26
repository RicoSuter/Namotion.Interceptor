# Source convergence

Status: proposed
Scope: the outbound path from the local model to an external source, and the verification that the two agree
Supersedes: PR #355

This document states what is broken today with evidence, what we intend to build, and the decisions needed to start. Steps 1 and 2 are specified to hand-off detail with acceptance criteria. Step 3 specifies a model, rules and invariants but deliberately not a transition table; Part 7 explains why and what makes that gate trustworthy.

## Part 1: where we are today

Verified against the code on `master`.

| Problem | Evidence |
|---|---|
| Connectors report success for changes they never transmitted, so writes are silently lost | `OutboundWriter.cs:45-49` returns `WriteResult.Success` when the filtered batch is empty. Skipped changes appear in neither the success count nor `FailedChanges`: `ProcessWriteResults` re-applies the same filter (`:95-96`) and computes `successCount` over post-filter results (`:107`). `WriteResult`'s contract says unlisted changes count as written (`WriteResult.cs:14-18`). MQTT does the same for unmapped and unserialisable changes (`MqttSubjectClientSource.cs:257`, `:268`, `:295`) |
| The transient versus permanent classification never reaches a retry decision (#332) | Computed at `OutboundWriter.cs:101-114`, consumed only by a log line and exception counts. `OpcUaStatusCodeClassifier.cs:20-27` says so in its own remarks. `WriteRetryQueue.FlushAsync` requeues all failures unconditionally (`:170`) |
| A permanently failing write blocks every other queued write | `FlushAsync` aborts the whole loop on the first failing batch (`WriteRetryQueue.cs:172`) and requeues failures at the head (`:214`). Head-of-line blocking, worse than "retried forever" |
| Queued writes are never retried after an in-place reconnect (#362, liveness) | `ReapplyRetryQueue` has one call site, `SubjectSourceBase.cs:99`, in the base retry loop. The four connector reconnect paths call only `LoadInitialStateAndResumeAsync` (`OpcUaSubjectClientSource.cs:491`, `SessionManager.cs:80`, `WebSocketSubjectClientSource.cs:659`, `MqttSubjectClientSource.cs:549`) |
| Source-wins reconciliation is not enforced on those four paths (#362, correctness) | Same evidence |
| A chatty property evicts an unrelated pending write | `WriteRetryQueue` is a `List` with global eviction by `RemoveRange(0, over)` (`:71-76`) |
| Writes during the connect and reconnect-delay windows are lost | The outbound subscription is created in the `ChangeQueueProcessor` constructor (`:88`), instantiated at `SubjectSourceBase.cs:87-94`, after listen and load |
| Outbound memory is unbounded under a slow transport (#281) | `maxQueueDepth: null` at `SubjectSourceBase.cs:93` |
| There is no way to tell whether a property is in sync (#195) | Source state reports connection lifecycle, not value agreement |
| Polling and subscription produce different value representations | `SubscriptionManager.cs:204` converts; `PollingManager.cs:390` passes the raw node value to `:423` |
| Read-after-write is enabled by default but tracks nothing | `ReadAfterWriteManager.cs:90-94` registers only when the requested sampling interval is exactly `0` and the server revised it upward; the default is `null` (`OpcUaClientConfiguration.cs:118`) |
| Read-after-write mis-dates or throws on servers that omit source timestamps | `ReadAfterWriteManager.cs:329` casts `DataValue.SourceTimestamp` to `DateTimeOffset`. The SDK returns `DateTimeKind.Unspecified`, so the cast applies the local offset: values are silently shifted, and the SDK's "not supplied" `DateTime.MinValue` throws in any positive-offset timezone. Note this row is unreachable while the row above holds, since nothing registers by default |
| Read-after-write discards the observation that would prove divergence | `ReadAfterWriteManager.cs:331-337` skips a readback whose source timestamp predates the local write timestamp |

Two structural facts the design must respect:

- **Equality-vetoed writes produce nothing.** `PropertyValueEqualityCheckHandler` is `[RunsFirst]` and does not call `next` on equality (`:10-20`), so no terminal write, no sequence, no publication. An inbound value equal to the model is therefore invisible downstream, and it is the most valuable observation there is.
- **Commit and dispatch are separated.** The terminal commits under `SyncRoot` (`WriteInterceptorFactory.cs:29-36`); dispatch happens on the unwind after the lock is released (`PropertyChangeInterceptor.cs:163`, `:185-202`). Anything treating "no queued change" as "no pending write" is wrong.

## Part 2: where we want to go

**Quiescent convergence.** Once local writing stops and the transport is healthy, the local model and the source agree, or the property is visibly marked as not in sync.

Not guaranteed:

- **Instantaneous agreement.** Local-first applies before the source sees it. Write-through remains the job of source transactions.
- **Exactly-once delivery.** Any transport can accept a write whose response is lost. Final-value writes are idempotent; the promise is eventual final-value convergence with bounded retries.
- **Convergence where the source cannot be observed.** Those properties report `NotVerifiable`.
- **Preservation of a local write racing an inbound change.** Under the default policy the source wins, so a local write can be discarded when inbound latency exceeds capture latency. Inherent to local-first with source authority, stated rather than hidden.

## Part 3: principles

**P1. Send outcomes update intent. Observations update belief. A send outcome never updates belief.** An acknowledgement proves a request was accepted, not what the source holds, and on two of three transports not even that it was transmitted.

**P2. Belief is a hint. No destructive action on belief alone.** Overwriting the local model requires a fresh observation, obtained by readback where the transport supports one. Where it does not, the policy degrades to keeping the local value and reporting divergence.

**P2a. Freshness of the observation is not sufficient: the apply must also be guarded.** A readback is taken at a point in time, and a local write can commit while it is in flight. A destructive apply is therefore vetoed when a local commit newer than the observation exists, enforced by compare-and-set at the terminal. Without this, a write issued during the readback round trip is silently overwritten and the property reports `InSync` while permanently diverged, satisfying every other rule.

**P3. Sequencing orders local events only.** A local counter answers "did this local write commit after that point". It cannot rank two source observations, and no transport here offers a portable source-side sequence.

**P4. The machine decides, a separate layer acts.** Every external fact (gate acquired, quiescent, belief age, readback outcome) enters as an event parameter, never an ambient read. Actions are queued and performed outside the transition, because dispatch is synchronous on the writing thread and an inline model write would re-enter the machine before the transition settles.

## Part 4: the model

### Clocks

**`commitSeq`** is a monotonic counter stamped at the terminal write, threaded through `PropertyWriteContext`, used only to order local commits and to ask whether an observation was staged after a given commit.

**Decision (was open): the counter is per context, not per subject.** A per-subject counter needs a new `IInterceptorSubject` member, which is source-breaking for the generator, `DynamicSubject`, every hand-written subject in this repository and every external one. A per-context counter is a single `Interlocked.Increment`, requires no interface change, and is a strict superset for the only two uses above, both of which need ordering rather than density. The cost is one contended increment per committed write; the stamp PR is benchmark-gated, and if contention proves material, per-subject remains available as an explicit breaking change with approval. `IInterceptorSubject` already exposes `SyncRoot`, `Context`, `Data`, `Properties` and `AddProperties`, so `Data` is a third option, at the cost of a dictionary lookup per write inside the lock, which the timestamp path already pays once.

**Belief is ordered by arrival**, because P3 forbids ranking observations by a local counter and no source-side order exists. Combined with P2 and P2a this is safe: a stale or out-of-order belief causes a redundant verification, never a destructive action.

**`ownershipEpoch`** changes when a property's binding changes (claim, release, or a NodeId rebind while ownership persists). **`connectionEpoch`** changes per session. Both are carried on the observation, captured when the subscription, poll or load producing it was created, so an observation from an abandoned session or superseded load is discarded on arrival.

The two are handled differently: an **ownership** change invalidates outstanding intent, because it targeted a different binding. A **connection** change must not, because the property is the same one and discarding intent would drop queued writes on every reconnect.

### Per-property state

```
belief
    observedValue, observedAt, epoch      last observation, arrival ordered
    verifiedAt                            when last confirmed by a fresh read

intent (while a local write is outstanding)
    desiredValue, desiredSeq              latest local intent
    outbox                                bounded list of sent values not yet accounted for,
                                          each with its sequence, send time and expiry
    attempts                              retry budget consumed by the current desired value
    ownershipEpoch                        binding this intent targets

fault (persistent, acknowledgeable)
    lastRejection                         value, status, timestamp, reason

decisions                                 bounded ledger of recorded discard decisions (see I5)
```

The **outbox replaces a single attempt slot**. One slot cannot distinguish our own delayed echo from a third-party change once overwritten or cleared, which loses a newer local write while reporting `InSync`. `attempts` resets whenever `desiredValue` changes.

**Outbox expiry (was deferred, now settled because R2 depends on it).** An entry expires at the later of: the transport's confirmation grace window, or the connection epoch in which it was sent. Entries never survive a connection epoch change, because a dead session can no longer echo them. Without expiry, a third party setting a value we sent earlier would have its change classified as our echo and divergence suppressed.

### Two comparison predicates

- **`≈sync`**: tolerant, decides whether model and source agree. Per mapping; for OPC UA at least as wide as the configured deadband.
- **`≈confirm`**: decides whether an observation is the echo of a value we sent. It is **not** exact equality. The echo is our value after a round trip through the value converter (`OutboundWriter.cs:154-156` out, `SubscriptionManager.cs:204` back), which is not the identity for `decimal`, `float` narrowed from `double`, `DateTimeOffset` to `DateTime`, or asymmetric enum conversions. `≈confirm` therefore uses a **representation epsilon**: tight, and a different quantity from `≈sync`'s deadband tolerance. Exact comparison here would fail confirmation on correctly applied writes, exhaust the budget, and revert a value the device accepted.

Convergence is scoped to leaf value properties. Subject-valued and collection-valued properties are `NotVerifiable`.

### Observability classification

Per property, maintained across reconnects: `Subscribed`, `Polled`, `LoadOnly`, `Unobservable`. A property whose monitored item failed but which remains readable is `LoadOnly`. `Unobservable` means neither push nor readback.

Load bearing in four places:

1. A property with no confirmation channel never waits for an echo, so it cannot burn its budget on grace expiries and cannot be faulted for silence.
2. `Unobservable` properties never take a destructive policy action.
3. The idle convergence interval defaults on for OPC UA and **covers `Subscribed` properties too, not only `LoadOnly`**. A lost notification (deadband, queue overwrite, sampling straddle) leaves belief and model agreeing on a stale value with no divergence signal; the idle readback is the only thing that heals it. Scoping the idle check to `LoadOnly` would void the guarantee for the largest class of properties.
4. Readbacks are budgeted and batched (`LoadInitialStateAsync` already batches by `MaxNodesPerRead`, `OpcUaSubjectClientSource.cs:202`). After a mass-divergence event this is one read per diverged property; without a budget it is a storm.

### States and reported states

Internal states: `Clean`, `Dirty`, `InFlight`, `Awaiting`.

Reported states, and the mapping, which the rest of the system consumes (#195, PR #354):

| Internal | Belief and fault | Reported |
|---|---|---|
| `Clean` | belief present, `model ≈sync belief`, no unacknowledged fault | `InSync` |
| `Clean` | belief present, `model ≉sync belief` | `Diverged` |
| `Clean` | unacknowledged fault, regardless of value agreement | `Diverged` |
| `Clean` | no belief, or classification `Unobservable` | `NotVerifiable` |
| `Dirty`, `InFlight`, `Awaiting` | any | `Pending` |

Events: `LocalCommit`, `Observation`, `SendPicked(selection)`, `SendOutcome(attemptSeq, kind)`, `InFlightDeadline(attemptSeq)`, `GraceExpiry(attemptSeq)`, `ReadbackCompleted(value)`, `ReadbackFailed`, `OwnershipEpochChange`, `ConnectionEpochChange`, `LoadComplete`, `TransactionConfirmed`, `FaultAcknowledged`, `OwnershipReleased`, `ConvergenceTick(quiescent, beliefAge)`.

### Rules

Each prevents a specific failure that a review constructed.

| | Rule | Prevents |
|---|---|---|
| R1 | Every send outcome is matched to its own attempt sequence, never the current desired sequence | A late outcome for a superseded attempt destroying the live one |
| R2 | An observation matching an unexpired outbox entry is our echo, not a source change | A newer local write discarded as "the source moved", then reported `InSync` |
| R3 | No destructive action without a fresh observation | Stale belief overwriting the model with an old value |
| R4 | Agreement uses `≈sync`; confirmation uses `≈confirm` with a representation epsilon | An unchanged value confirming a write, and a converted echo failing to |
| R5 | Properties with no confirmation channel never enter a confirmation wait | Budget exhaustion and reverting a write the device accepted |
| R6 | Every non-terminal state has a deadline that eventually resolves it to a reported state | Properties reporting `Pending` forever, which also makes I7 vacuous |
| R7 | Connection epoch changes preserve intent; ownership epoch changes discard it with a recorded decision | Reconnects silently dropping queued writes |
| R8 | A send outcome distinguishes retriable non-attempt from structurally impossible; the latter faults | An unmappable property retrying forever, which is #332 renamed |
| R9 | Adoption writes carry a source origin | The adopted value echoing back to the device as fresh local intent |
| R10 | Quiescence is per property; misreading it is non-destructive by R3 and R12 | One chatty property starving convergence for its whole subject |
| R11 | Actions are queued, not executed inside a transition | Re-entrancy, since dispatch is synchronous on the writing thread |
| R12 | A destructive apply is vetoed by compare-and-set when a local commit newer than the observation exists | A write committed during the readback round trip being silently overwritten |

### Invariants

| | Invariant | How asserted |
|---|---|---|
| I1 | An intent exists if and only if the state is not `Clean` | per step |
| I2 | `desiredSeq` is at least every outbox entry's sequence | per step |
| I3 | `attempts` never exceeds the budget and resets when `desiredValue` changes | per step |
| I4 | Belief changes only by adopting an arriving observation; no rule may reorder or reject one by comparing it to another observation | per step, as a transition property rather than a monotonicity claim |
| I5 | Every local commit reaches exactly one accounted outcome: delivered, superseded, discarded with an entry in the decisions ledger, or faulted | over the run, using the ledger; not per step |
| I6 | No destructive model write occurs without a fresh observation and a passing R12 guard | per step, given a formal freshness predicate over `verifiedAt` and the staleness bound |
| I7 | With no further local commits and a healthy transport, every property reaches `InSync`, `Diverged` or `NotVerifiable` within a bounded number of events | liveness, bounded horizon, with the premise encoded as an enumerator constraint |

I4 replaces the previous "belief never regresses", which was false: OPC UA has several concurrent observation channels, so a slow readback can legitimately land after a fast notification. What matters is not monotonicity but that nothing tries to order two observations against each other.

## Part 5: integration

### Staging observations

Belief cannot be fed from the change stream, because an inbound value equal to the model is vetoed before publishing anything. It must be staged at the point of application, before the interceptor chain runs.

`SetValueFromOrigin` covers the OPC UA read-after-write and loader value paths but is **not** a universal funnel. These bypass it and need explicit handling:

| Path | Location | Handling |
|---|---|---|
| Transaction commit replay and rollback | `SubjectPropertyChangeOperations.cs:126-138` | Must stage; transaction verification depends on it |
| `ApplySubjectUpdate` with local origin | `SubjectUpdateApplyContext.cs:52-58` | Must stage |
| Path applies with a null source | `PathExtensions.cs:88-97` | Must stage. `PathExtensions.cs:286` sets a subject-valued property, which is out of convergence scope and needs no staging |

**Staging must be serialised per property.** `SubjectPropertyWriter.Write` takes no lock once buffering ends (`:97-125`), and subscription, polling, read-after-write and idle readback are different threads. Serialisation is new machinery on the inbound path, not a property that holds today.

An earlier revision listed the OPC UA loader's dynamic property creation as a defect that would write loaded values back to the server. That was misdiagnosed: the write at `RegisteredSubject.cs:348` is `null` to `null` and is equality-vetoed, and the property is not source-bound until `ClaimSource` runs later in `MonitorValueNode` (`OpcUaSubjectLoader.cs:400-405`). No action needed.

### Reconnect and reload paths

| Path | Reapplies queued writes today |
|---|---|
| `SubjectSourceBase.ExecuteAsync` (base retry loop) | yes, at `:99` |
| `OpcUaSubjectClientSource.ReconnectSessionAsync` | no |
| `SessionManager.PerformFullStateSyncIfNeededAsync` | no |
| `WebSocketSubjectClientSource.ReconnectAndResumeAsync` | no |
| `MqttSubjectClientSource.OnReconnectedAsync` | no |

`StartBuffering` replaces the pending list (`SubjectPropertyWriter.cs:39-45`), so belief is invalidated at buffer start rather than carried across.

### Transactions

`WriteToSourcesAsync` returns before the local apply and the source write lock is scoped to a single call, so a pending send can interleave between a transaction's external write and its local apply.

- The transaction holds the per-source gate from before its external write through local apply, rollback and registration. The send loop acquires the same gate and re-checks liveness after acquiring it. Multi-source transactions acquire in a deterministic order.
- A send already on the wire is **not abandoned**; it stays in the outbox until accounted for, because abandoning it lets its later echo revert the transaction and report `InSync`.
- Writes inside an open transaction never reach a terminal (`SubjectTransactionInterceptor.cs:85`), so commit replay is the point of sequence issue.

### Transports

**OPC UA, the reference tier.** Per-item status gives an exact per-write outcome. `LoadInitialStateAsync` is the readback mechanism: a complete batched read at `maxAge: 0`. Monitored items give push observations for successfully created, non-deadbanded items. `SourceTimestamp` is optional and omitted by some servers, so no rule may depend on it.

`ReadAfterWriteManager` is not the readback path: it tracks nothing under default configuration and discards exactly the observation that proves divergence.

**WebSocket.** Sender-inclusive broadcast gives echoes; the server snapshot gives readback at connect, when a `welcome` frame supplies it (`WebSocketSubjectClientSource.cs:262`, `:288-291`). No per-item rejection, so faults arrive only through the retry budget.

**MQTT**, per topic mapping, since QoS is mapping-specific:

| Configuration | Convergence |
|---|---|
| QoS 1 or 2, retained | Full: acknowledgement plus readback from the retained message |
| QoS 1 or 2, not retained | Acknowledged delivery, no readback; `NotVerifiable` for the idle check |
| QoS 0, retained | `NotVerifiable` unless periodic retained readback is enabled |
| QoS 0, not retained | `NotVerifiable` |

MQTT has no load (`LoadInitialStateAsync` returns `null`, `:211-215`) and retained arrival has no completion signal.

## Part 6: the plan

### Step 1: outcomes and confirmed bugs

**1a. Standalone, land first.** `PollingManager` value conversion (convert at publish; leave `LastValue` raw so dedup semantics match the subscription path). The read-after-write timestamp fix: guard `DateTime.MinValue` **and** apply `DateTimeKind.Utc` before converting, since the missing `Kind` silently shifts every readback by the local offset; fall back to the received timestamp when absent.

**1b. Atomic: per-change outcomes, retry disposition and the dropped signal.** These are **one change, not three**. Enumerating previously-skipped changes as failures without the disposition and queue fixes turns a silent drop into a head-of-line block that wedges every other property. Contents:

- `WriteChangeOutcome`: `Accepted`, `Transient`, `PermanentlyRejected`, `NotAttempted`, `Unsupported`. **Decision (was open): `NotAttempted` splits.** `NotAttempted` means the transport did not get to it and it must be retried (a batch remainder). `Unsupported` means it can never be attempted as bound (unmapped node, no setter) and must fault. Both arise on the same code path today, so one value cannot carry both and R8 cannot be satisfied.
- `WriteResult` gains outcomes aligned with `FailedChanges`, empty meaning all `Transient` so pre-existing `ISubjectSource` implementations keep working. `WriteResult.Success` stays a zero-allocation singleton and a fully successful batch allocates nothing.
- `WriteRetryQueue`: continue past a failing batch instead of aborting; requeue at the **tail** with per-property coalescing so requeueing cannot invert two writes to one property; requeue only `Transient` and `NotAttempted`; drop `PermanentlyRejected` and `Unsupported`, counting them.
- `OutboundWriter`: build an index map in `CreateWriteValuesCollection` so `ProcessWriteResults` no longer re-runs the filter, which today can misattribute a status if a structural change lands between the two passes. Empty filtered batch returns every change as `Unsupported`, not `Success`.
- `SubjectSourceBase.DroppedWriteCount`, plus rate-limited logging. Without it, closing #332 replaces a noisy-but-visible failure with a silent one, since faults do not arrive until step 3.
- `WriteResult`'s contract text ("consumers may retry a failed change but never revert it") becomes false here, not in step 3, and must change with it.
- `SourceTransactionWriter` now sees unmapped changes in `FailedChanges`, so a transaction over an unmapped property fails where it silently succeeded. Correct, and the transaction tests must be swept.

**1c. Reconcile on in-place reconnects.** Call `ReapplyRetryQueue()`, not `FlushAsync`: the two have opposite semantics, and a flush would install local-wins where the base loop uses source-wins.

**Decision (was open): the call site is the four connector paths, not a post-resume hook.** A hook inside `LoadInitialStateAndResumeAsync` fires on the base loop's own call at `SubjectSourceBase.cs:85`, which precedes both the `ChangeQueueProcessor` construction at `:87-94` and the reconcile at `:99`. Since `ReapplyRetryQueue` writes locally and relies on the running processor to transmit, a hook there would apply values with nothing listening and drain the queue before `:99` could run, deleting the one reconcile that works today. So: make it `protected`, keep `:99`, and call it explicitly after the load at each of the four sites. Drain and reapply under `_flushSemaphore`, since `DrainForLocalReapply` is unguarded today and races a concurrent flush's requeue.

Residual, accepted for step 1 and closed by step 2: the connector reconnect loops are spawned from `StartListeningAsync`, which returns before the processor exists, so a reconnect in that narrow window still re-applies with nothing listening.

**Scope of the #362 claim.** Both halves close for **OPC UA**, and for WebSocket when a snapshot is supplied. **Not for MQTT**: there is no load, so the reconcile compares against unrefreshed local values and resolves local-wins, racing retained delivery.

**1d. Deferred: the two-predicate status classifier.** Splitting `IsTransientError` into a write predicate is correct in principle, but making `BadUserAccessDenied` permanent on writes is a production regression: access levels are mutable server-side (`OpcUaStatusCodeClassifier.cs:12-18` documents exactly this), so the common "connect anonymously, operator grants a role, writes begin succeeding" flow currently heals and would instead drop writes permanently. Defer until step 3's faults can drive an operator-visible retry, or narrow the write-permanent set to codes that cannot heal within a session.

**Acceptance criteria.** Outcome propagation through single-batch, multi-batch, partial-failure and mid-batch-throw; a permanently rejected change does not block later queued writes; per-property ordering survives requeue; the `OutboundWriter` index map attributes a failure to the correct change when an earlier change was filtered out; per connector, a queued write reaches the server after an in-place reconnect and a server-changed property drops its queued write.

### Step 2: intent

The per-property intent map replaces `WriteRetryQueue` and `ReapplyRetryQueue`, with capture live from source start and the `commitSeq` stamp.

- **Intent record**: the coalesced change per property using the existing `MergeWithNewer`, which keeps the earliest old value as the reconcile baseline and the newest new value, plus `attempts` and first-queued time. An A to B to A burst therefore collapses to one intent whose baseline is the original A.
- **Live capture**: hoist the `ChangeQueueProcessor` to the `SubjectSourceBase` lifetime so the subscription exists from source start, with `ProcessAsync` called per iteration. This requires the processor to survive cancel and restart, which needs a test.
- **Retry budget**: not enforced in step 2. There is no fault state to move an exhausted property into until step 3, so transients retry as today.
- **Decision (was open): `writeRetryQueueSize = 0` keeps its meaning.** It currently means "do not buffer; drop writes made while disconnected". The map is inherently bounded and cannot be "sized", but the operator's intent is honoured by not retaining intent across a disconnect for that source. Silently converting an explicit drop into buffering would be an unannounced behaviour change.
- Bound: one entry per owned property, so memory is proportional to owned property count and `WriteRetryQueueSize` becomes a no-op for all other values.

**Stated changes, not claimed away**: reconcile is re-implemented; coalescing changes A to B to A replay; `PendingWriteCount` becomes "properties with outstanding intent", routinely non-zero, so alerts on greater than zero will fire.

**Acceptance criterion**: the connect-window regression test. A source whose `StartListeningAsync` blocks on a gate, a write issued while blocked, and the write must reach the source once the gate opens. No existing test covers this; the current retry-queue test works around the window with a probe loop.

### Step 3: belief and convergence

Belief register and staging, observability classification, the convergence check, reported state and faults, divergence policy, transaction coordination, and the machine satisfying R1 to R12 and I1 to I7.

### Step 4: servers

The same capture and coalescing split without belief or convergence. Closes the server half of #281.

## Part 7: method and gate for step 3

The transition table is not frozen here. It has been written in prose twice and both times review found defects by hand-constructing event sequences, which is what an enumerator does automatically. So: **build the harness and the invariants first, then evolve the machine until it passes.**

The gate is a clean enumeration run rather than a review, but a clean run only means something if the oracle is strong. The previous version of this document made the invariants the whole oracle, and a review then found a permanent-divergence scenario that satisfied every one of them. So the gate requires all of:

1. **A refinement oracle**, not only safety assertions: a reference single-register model with an explicit serialisation point, checking each enumerated run for observational equivalence to some reference run. This catches classes of bug nobody thought to write an invariant for.
2. **Transition coverage plus mutation testing** of the transition function. A clean run at low coverage proves nothing.
3. **A generated adversarial parameter space** (out-of-order outcomes, expired and unexpired outbox entries, epoch changes at every point), rather than a hand-listed set fitted to the bugs previous reviews happened to find.
4. **An explicit abstract domain and horizon**: finite value, epoch and time domains, and a stated stopping condition. I7's premise ("no further local commits, healthy transport") is an enumerator constraint, not an assertion.
5. **A test that no production path bypasses the transition function**, or the claim to test shipped code is unearned.

Bounded enumeration proves less than a model checker: no violation within a horizon, rather than a general proof, and it cannot fully discharge I7. Accepted, in exchange for testing the shipped implementation rather than a model that can drift. This does not depend on the unmerged formal-model work in #358; if that lands, modelling the same machine there is a complement.

Before step 3 starts it also needs: the declared state record; where `≈sync` and `≈confirm` live and how the OPC UA deadband feeds `≈sync`; a prerequisite-or-stub decision on PRs #370 (write lock), #313 (batched read) and #354 (reported-state publisher); and a seed transition table, even a knowingly imperfect one, so the enumerator grades something rather than a third prose specification.

## Part 8: API and breaking changes

| Change | Notes |
|---|---|
| `WriteChangeOutcome` enum, `WriteResult` outcomes | Public, snapshot-pinned in Connectors. Contract text changes in step 1 |
| `SubjectSourceBase.DroppedWriteCount` | Public, new |
| `SubjectSourceBase.ReapplyRetryQueue` becomes `protected` | Snapshot |
| `SubjectSourceExtensions.WriteChangesInBatchesAsync` | Public; encodes the normalisation that outcomes replace |
| `ITransactionWriter.WriteToSourcesAsync`, `SourceWriteResult` | Carry write outcomes on the transaction path; must move in lockstep |
| `SubjectPropertyChange` gains `commitSeq` | Public struct, snapshot; benchmark-gated, since it is copied on every enqueue and dedup pass |
| Per-context commit counter | No interface change (see Part 4 decision) |
| `PropertyWriteContext` carries the sequence | Internal setter |
| Sequence-guarded terminal write (R12) | New core surface, compare-and-set in the existing lock |
| `ChangeQueueProcessor` | Public with a public constructor; step 2 changes how sources drive it |
| `SubjectSourceBase.PendingWriteCount` | Public; meaning changes in step 2 |
| Observation APIs gain an epoch parameter | Connector churn |
| Staging abstraction on the context | New service in Tracking; hot path, must not allocate, must serialise per property |
| Transaction lease API | New public surface, step 3 |
| `WriteRetryQueueSize` obsoleted | Public on OPC UA, MQTT and WebSocket configurations and a `SubjectSourceBase` constructor parameter. Snapshot-pinned on **OPC UA and MQTT only**; WebSocket has no public-API snapshot test. Keep, mark obsolete, honour `0`, land separately |
| New configuration | Divergence policy with per-property override; `≈sync` tolerance and `≈confirm` epsilon; retry budget; grace, in-flight and resolution deadlines; idle interval, staleness bound and readback budget; observability overrides |

**Defaults**: retry budget 3; grace derived from the revised sampling interval on OPC UA and a configured round trip elsewhere; idle interval on for OPC UA covering `Subscribed` and `LoadOnly`; policy `RevertToSource`.

**Not implementable as previously promised.** Rejecting transforming or vetoing source-bound properties at claim time is not statically decidable for a conditional veto inside a user interceptor. Derived properties **are** detectable (`Metadata.IsDerived`) and are rejected at claim time, since `FinalizeOrigin` demotes derived writes to `Local` and they would never quiesce. A transforming hook is detected at runtime instead: the origin is already demoted when the stored value differs from the sent value, and a property failing to converge for that reason is faulted with a specific diagnosis.

**Diagnostics**: the connector has only source-level counters and no `Meter` or `ActivitySource`. Needed: counts by reported state, an enumeration of diverged properties with model value, observed value, observation age and last rejection, the step-1 dropped counter, and rate limiting.

## Part 9: related work

**Superseded or closed**: PR #355 (step 2), PR #333 (step 1b), PR #372 (no fourth origin kind), #332 (step 1b, see 1d for the classifier caveat), #362 (step 1c for OPC UA and WebSocket; MQTT correctness in step 3), #281 (sources step 2, servers step 4), #363 (with #355), #195 (step 3 with #354).

**Coordinate**: PR #354 (a different axis, connection lifecycle versus value agreement; it publishes what step 3 computes, and `PendingWriteCount` changes meaning in step 2), PR #370 (write lock ownership), PR #313 (shared read path), #385 (the stamp half is a step 2 prerequisite), #299 and #277 (diagnostics overlap). PR #358 is not a dependency.

**Re-scope**: PR #349 (repair becomes automatic; keep the transaction-layer classification), PR #375 (verify whether staging before the equality handler subsumes its detection, then close if so), PR #353 and #352 (overflow moves to servers and history stores), PR #209 (subsumed for sources).

**Unrelated**: #282, #228, #200 (lossless delivery is a separate projection with opposite overflow semantics), #373 (inbound ordering), #342 (this is an input to that contract, not a definition of it), #367, #369, #378 (independent refactors).

## Part 10: open items

1. The transition table, by design (Part 7).
2. Whether belief needs bounded history rather than a single slot. Arrival order plus P2 and P2a is believed sufficient; the enumerator is where to find out.
3. Outbox bound, given expiry is now settled.
4. Migration guidance for external `ISubjectSource` implementations beyond the default outcome mapping.
5. Benchmark baseline and threshold for the `commitSeq` gate.
