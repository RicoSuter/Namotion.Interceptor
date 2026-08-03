# Connector Reliability Contract

What the connector and source layer actually guarantees today, where each guarantee stops holding, and what is knowingly not guaranteed.

This document is descriptive, not aspirational. Every guarantee below was verified against code on `master` (`1a9d9706`). Proposals for changing these guarantees live in `docs/superpowers/specs/2026-07-28-connector-reliability-redesign.md`.

When this document and another doc disagree, this one is correct: several guarantee claims elsewhere were verified false and are listed in [Corrections to other documents](#corrections-to-other-documents).

## Coordinate system

Reliability in this codebase is defined by three axes. A guarantee is meaningless without all three.

| Axis | Values |
|---|---|
| **Role** | Source (external system owns the data) / Server (local model owns the data) |
| **Direction** | Inbound (external to model) / Outbound (model to external) |
| **Phase** | Connect, Steady, Degraded, Recover, Stop |

Sources get `SubjectSourceBase`, which owns a full pump, buffering, a write retry queue, and an ownership manager. Servers get no base class. That asymmetry is the single largest structural fact about this layer, and most server-side non-guarantees below follow from it.

## Foundation: the core write path

Everything else in this document is built on these.

**Guaranteed.** The terminal store is atomic per subject: backing-field write, `IsWritten`, origin finalization, and the write timestamp all happen under `lock (context.Property.Subject.SyncRoot)` (`WriteInterceptorFactory.cs:29-36`). The timestamp uses `Interlocked` on a `long[1]` holder, so there is no 32-bit tearing (`PropertyReference.cs:98-102`).

**Guaranteed.** Interceptor unwind order is fixed by attributes: equality check, derived recalculation, change dispatch, lifecycle reconciliation, terminal. A consumer receiving a structural change therefore sees the child already attached, context-inherited, and registered.

**Guaranteed.** A write that commits after `Subscribe` or `CreatePropertyChangeQueueSubscription` returns is always delivered. This is enforced by a correct Dekker pairing: a subscriber-side memory barrier after install against a writer-side barrier-then-recheck (`PropertyChangeInterceptor.cs:72,121,271-276`).

**Not guaranteed: the read-compare-write is not atomic.** The generated setter reads the backing field outside the lock (`SubjectCodeGenerator.cs:313`), and the equality handler compares that unsynchronized snapshot (`PropertyValueEqualityCheckHandler.cs:16`). Only the store is locked. Consequences:

- Two writers reading the same value both pass the equality gate, both store, and both publish the same `(Old, New)` transition.
- An inbound source write of `V0` racing a local write of `V1` reads `V0`, compares equal, and is **silently dropped** while the field holds `V1`. Nothing is published and the source is never told.
- `OldValue` is a pre-chain snapshot, so the change queue's merge rule ("keep the oldest old value") can produce a diff whose baseline never existed in the model.

`docs/connectors.md` claims the opposite. See [Corrections](#corrections-to-other-documents).

**Not guaranteed: delivery is not in commit order.** The store happens under `SyncRoot`; dispatch happens after the lock is released (`PropertyChangeInterceptor.cs:163,195`). Two threads writing the same property commit in lock order and can dispatch in the opposite order. Everything downstream faithfully preserves the wrong order.

**Not guaranteed: no re-entrancy bound.** A handler that writes another property re-enters the chain with no depth counter, no cycle detection, and no thread-static guard. `MaxStabilizationIterations = 100` bounds re-evaluation of a single derived property only. An oscillating cycle through observers, INPC write-back, or lifecycle handlers runs to `StackOverflowException`, which is uncatchable.

**Not guaranteed: exception isolation on dispatch.** A synchronous observer that throws skips the remaining deliveries for that write and unwinds past the derived cascade. The value is committed, dependents are left stale, and the exception surfaces from the setter.

## Source, outbound (model to external)

**Guaranteed.** Per-property FIFO from enqueue to `WriteChangesAsync`. All producers enqueue into one queue, drained by a single consumer under an exclusive gate.

**Guaranteed.** Exactly one in-flight write per source, via a semaphore. `ISupportsConcurrentWrites` has zero implementers.

**Guaranteed.** Retry entries are flushed before new changes, failed items re-insert at the front, and a multi-batch failure concatenates failed plus unprocessed remainder, so nothing is skipped within a connected session.

**Guaranteed.** Only claimed properties are ever sent. The change queue filters on `TryGetSource(out var source) && source == this` (`SubjectSourceBase.cs:90`).

**Not guaranteed: cross-property order.** Deduplication places each property at its *last* occurrence, so `[A1, B1, A2]` emits `[B1, A2]`. A now trails B. The old value is preserved by the merge, so the diff baseline is right, but the interleaving is not.

**Not guaranteed: any delivery across the pump-loop boundary.** The `ChangeQueueProcessor` is constructed inside the pump loop and disposed when `ProcessAsync` returns, and **the change subscription dies with it**. During `retryTime` (default 10 seconds), `StartListeningAsync`, and `LoadInitialStateAsync`, no subscription exists. Local writes in that window are never enqueued, never retried, and never reconciled. For read-back properties the initial-state load hides this; for write-only or command properties it is permanent silent divergence.

**Not guaranteed: correct reconnect reconcile.** The optimistic re-apply compares the current value against the change's **old** value, not against the source's value (`SubjectSourceBase.cs:194-197`). Two queued changes `A→B` then `B→C` on a property absent from the initial state both fail the comparison and are dropped, leaving the source at `A` forever, logged as "source wins". An A→B→A round trip during an outage also passes the comparison, so a stale write is re-applied as fresh.

**Not guaranteed: bounded memory.** `maxQueueDepth` is `null` at every production call site (`SubjectSourceBase.cs:93`, `MqttSubjectServer.cs:177`, `OpcUaSubjectServer.cs:211`, `WebSocketSubjectHandler.cs:370`). The bounded-queue drop path and its `DropCount` are unreachable in production. Deduplication is the only thing bounding memory today.

**Not guaranteed: transient versus permanent write failure.** `WriteResult.FailedChanges` means "unconfirmed", explicitly not "rejected". Permanently rejected writes are re-enqueued and retried forever.

**Not guaranteed: drain on stop.** Host shutdown discards the change queue plus up to `bufferTime` of pending changes with no final flush. `WriteRetryQueue.Dispose` drops pending writes with no log and no count. `StopAsync` without disposal leaves properties claimed, so `TryGetSource` still reports a stopped source as the owner.

## Source, inbound (external to model)

**Guaranteed.** Buffer, load, and replay are atomic against inbound writes: the load action runs inside the writer's lock, the buffer drains under the same lock, and only then is buffering disabled. Initial state strictly precedes buffered replay.

**Guaranteed.** Buffered replay is FIFO **by arrival at `Write`**, which is arrival order in this process. Nothing models source emission order.

**Guaranteed.** Echo suppression: the outbound queue skips changes whose origin source is reference-equal to the target source, gated on the origin surviving finalization.

**Not guaranteed: any ordering once buffering ends.** After the buffer is released, `Write` applies on the caller's thread with no serialization beyond the per-store `SyncRoot`. OPC UA alone has three independent inbound producers (subscription, polling, read-after-write). There is no ordering primitive between two inbound updates to the same property, or between an inbound update and a local write.

**Not guaranteed: apply-order versus timestamp.** No apply path compares an inbound `changedTimestamp` against the stored write timestamp. The only ordering check in the repository is connector-local to OPC UA's read-after-write manager.

**Not guaranteed: correction of an equality-suppressed divergence.** When an inbound value equals the stored value but differs from what the source sent (a hook clamped or normalized it), the write is suppressed and nothing is published. The source keeps the rejected value indefinitely, and reconnect does not fix it because the initial-state apply is suppressed for the same reason.

**Not guaranteed: origin survival under in-place mutation.** Origin finalization compares the sent value to the new value. A hook that mutates a reference-typed value in place leaves them the same instance, so the origin survives, the change is echo-suppressed, and the correction never reaches the source. There is no detection or warning.

**Not guaranteed: inbound failure reporting.** Failed applies are logged and dropped with no retry. Five apply sites drop updates with **no log line at all** (`SubjectUpdateApplier.cs:22,77,132`, `SubjectItemsUpdateApplier.cs:46,96`). The documented rationale, that property writes are deterministic so retry would not help, is false: the registry lookup returns null while a subject is mid-attach, which is transient. On the `SubjectUpdate` path one throw aborts the remaining properties and the entire nested subtree.

## Ownership and structural coverage

**Guaranteed.** Claiming is an atomic single-owner compare-and-set. Two racing sources cannot both win.

**Guaranteed.** Owned properties are released when their subject detaches, and all are released on dispose. This requires `WithLifecycle()`; the manager's constructor throws otherwise.

**Not guaranteed: attach-side claiming.** Claiming happens **once**, inside `StartListeningAsync`. No connector subscribes to any attach event. A subject attached after that scan is never claimed, its changes are filtered out at `SubjectSourceBase.cs:90`, and there is no log and no counter. Recovery happens only on a full reconnect. A property added while disconnected is picked up by the next scan, so the failure mode is "works after a restart, never during a session".

**Not guaranteed: read-only properties are rejected at claim time.** No connector filters on `HasSetter` when claiming. A getter-only property is claimed, subscribed, and every received value is discarded by a null setter with no error. Both sides then agree forever on nothing.

**Not guaranteed: an aggregate unclaimed signal.** Properties dropped by mapping or inclusion filters are dropped with zero diagnostics. MQTT logs the candidate count computed before the per-property skips, so it over-reports.

**Not guaranteed: path identity stability.** MQTT's topic caches are invalidated only on subject detach or dispose. A collection reorder changes a property's path but not its cached topic, so an inbound message routes to the property now at the old index. This is silent cross-wiring, not a missing sync.

**Not guaranteed: forward and reverse path symmetry.** Five independent subject-path walkers exist. MQTT builds topics with the Registry builder and resolves inbound topics with the Connectors walker, and the two disagree on `[InlinePaths]` frames, on index typing (`int` versus `string`), on whether inclusion filters apply, and on whether resolution throws or returns null. MQTT therefore publishes `[InlinePaths]` topics it can never accept a write on.

## Transactions

The contract defines five properties. Their status on `master`:

| | Property | Status |
|---|---|---|
| **P1** | Atomicity, best-effort by mode | **Implemented**, with two unguarded holes |
| **P2** | Exactly-once to source | **Implemented** |
| **P3** | Convergence (source-wins resync) | **Not implemented. No code exists.** |
| **P4** | No silent divergence | **Partial** |
| **P5** | Truthful provenance | **Implemented** for the three shipped origin kinds |

P3 is the backstop that most other failure paths are documented as falling back to. `RequestResynchronization`, `WriteFailureKind`, and `SourceDivergenceException` have zero occurrences in `src/`.

**Persistent divergence cells.** Four failure combinations leave the model and the source permanently disagreeing:

| Failure | Local | Source | Reported? |
|---|---|---|---|
| Indeterminate source write (timeout or throw inside a batch) | old | possibly new | reported as "failed", which reads as "did not land" |
| Source revert fails | old | new on the stuck source | yes |
| Commit timeout during the write phase | old | partially new | bare `OperationCanceledException`, no failed-change list |
| Custom writer throws | old | unknown, unreverted | exception only |

**Not guaranteed: commit isolation.** `_isCommitting` does **not** reject tracked writes; the interceptor falls through and applies them straight to the model. Two interleavings do not converge, both because P2's echo suppression makes them stick:

- A non-transactional local write lands before the commit apply on the same property. The local write is queued and later flushed to the source; the commit apply publishes `Confirmed` and is suppressed. Ends: local holds the committed value, source holds the other one, permanently.
- A third-party source write lands between commit stage 1 and the local apply. Same shape.

No test covers the commit window.

**Not guaranteed: validation does not reject authoritative values.** Validators run on every origin. `DataAnnotationsValidator` ignores origin by design. An inbound source value that violates a `[Range]` throws, is caught and logged by the inbound writer, and is dropped: permanent divergence on every attributed property. At commit replay, a value the source already accepted can be rejected locally, and under `Rollback` the old value is then written back to the source, undoing a confirmed write.

**Not guaranteed: ordering between the two write machineries.** The transaction writer calls the source directly and never flushes the retry queue first. A stale queued write can therefore land at the source after a newer committed value.

## Servers

There is no `SubjectServerBase`. The only shared building block servers use is `ChangeQueueProcessor`. `SourceOwnershipManager`, `WriteRetryQueue`, `SubjectPropertyWriter`, `CircuitBreaker`, and `WriteResult` are source-only.

| Capability | OPC UA | MQTT | WebSocket |
|---|---|---|---|
| Outbound loss detection | none | none (relies on MQTTnet QoS1) | sequence numbers, gap detection |
| Heartbeat / liveness | SDK keep-alive | MQTT keepalive | own, 30 s |
| Slow-client handling | SDK queue overflow | SDK, drops at 25000 pending | send-lock skip, broadcast timeout, zombie eviction |
| Backpressure into the change queue | none | none | none |
| Per-client write acknowledgment | SDK write status | broker PUBACK, not model confirmation | none |
| Inbound error reported to client | no, log only | no, log only | generic `InternalError` only |
| Inbound ordering | SDK lock | per-session only | full serialization |
| Restart backoff | exponential 1-30 s + jitter | fixed 5 s | **none, tight loop** (standalone) |
| Detach cleanup | node removal | O(n) cache scan | not applicable |

Cells naming an SDK are guarantees of that SDK, not of this codebase, and none of them are verified: no server checks OPC UA monitored-item queue-overflow status codes or MQTT broker drop counters.

**Not guaranteed: outbound delivery, even on WebSocket.** In the multi-batch broadcast loop, if batch *i* throws, batches *i+1..n* are never sent and **never allocate a sequence number**. Clients see contiguous sequences and detect nothing. Silent permanent loss on the one connector that has loss detection.

**Not guaranteed: model and address space agree (OPC UA).** An inbound write that fails validation is caught and logged *after* the SDK already wrote `node.Value`. The node holds the rejected value, the model holds the old one, the client received `Good`, and nothing re-syncs. A test comparing model state on both sides cannot observe this.

**Not guaranteed: runtime-attached subjects are served (OPC UA).** Every change on a subject attached at runtime is silently discarded, with no log and no counter.

**Not guaranteed: an acknowledged client write was applied (MQTT).** With broker-side publish suppression, a client message whose local write produces no change (equal value, read-only property, or a validation throw) is neither relayed nor applied. The client already received a PUBACK. The read-only and validation cases are permanent.

## Verification: what is actually proven

The `ConnectorTester` is **not in CI**. No workflow references it, so nothing gates on it.

It asserts one thing: after mutations stop and a fixed grace period elapses, all participants' normalized state snapshots are string-equal. That proves eventual convergence of final values and nothing else.

It cannot detect, by construction: lossless delivery or per-property sequence continuity (deduplication makes this invisible), causal or apply ordering, atomicity as observed by a peer, partial batch acceptance or in-doubt writes, queue overflow (the bound is `null` in production), or degraded-but-alive links.

Two implemented capabilities are disabled everywhere: `UseTransactions` is false in all six shipped profiles, and `StructuralMutationRate` is absent from every profile, so collection and object-graph churn is never exercised despite being what most of the snapshot comparer exists for.

Strong unit coverage exists for deduplication semantics, the retry queue ring buffer and re-apply matrix, buffer-load-replay ordering, origin stamping, transaction failure flows, ownership claim and release, and WebSocket sequence numbers. MQTT has zero end-to-end integration tests.

## Known gaps

Ranked by whether the failure is silent, since a silent gap cannot be found by a convergence test.

### Silent and permanent

| Gap | Issue |
|---|---|
| Read-compare-write race drops an inbound value while the field holds a local one | none |
| Reconnect blind window: no subscription during retry, listen, or initial load | partially #362 |
| Reconnect reconcile compares against the wrong baseline, drops live values | none |
| Late-attached subjects are never claimed | #387 |
| Read-only properties are claimed, then every value is discarded | #102 |
| Equality-suppressed divergence leaves the source holding a rejected value | PR #375 |
| Commit-window interleaving leaves model and source permanently split | #338 |
| Inbound validation failure drops an authoritative source value | #342 |
| WebSocket multi-batch broadcast failure loses changes with no sequence gap | none |
| OPC UA node keeps a rejected inbound value; client saw `Good` | none |
| OPC UA discards all changes on runtime-attached subjects | none |
| MQTT swallows a PUBACK'd client write (read-only or validation) | none |
| MQTT topic cache is not invalidated on path change, causing cross-wiring | none |
| MQTT publishes `[InlinePaths]` topics it cannot accept writes on | #240 |
| Five inbound apply sites drop updates with no log at all | none |

### Loud or bounded

| Gap | Issue |
|---|---|
| Delivery is not in commit order | #385 |
| Unbounded change-queue growth under slow connectors | #281 |
| Permanently rejected writes retried forever | #332 |
| No convergence primitive after a failed commit (P3) | #340, PR #349 |
| No drain on shutdown | none |
| No unified connection or synchronization state | #195, PR #354 |
| No re-entrancy bound; oscillating cycles kill the process | #308 |
| Lifecycle handler exception corrupts the reconciliation baseline | #384 |
| Standalone WebSocket server restarts in a tight loop | none |
| MQTT initial-state republish per connect blocks live publishing | #292 |
| GraphQL re-sends the entire root subject on every change | #206 |
| Static type caches block assembly unload | #314 |
| Leaked fallback context pins a dead subject's executor | #207 |

## Corrections to other documents

These claims were verified false and should be fixed at their source.

| Document | Claim | Reality |
|---|---|---|
| `connectors.md:669` | "Individual property updates are atomic and thread-safe without requiring additional synchronization" | True for the store only. The read-compare-write is racy. |
| `connectors.md:61,66` | Buffered updates are "replayed in order" and "in the correct order relative to the initial state" | Only arrival-order FIFO. Nothing orders concurrent producers. |
| `connectors.md:130` | "Individual update failures don't block other updates" | False on the `SubjectUpdate` path: one throw aborts the subtree. |
| `connectors.md:132` | Property writes are deterministic, so retry would not help | False. Registry lookups fail transiently during attach. |
| `connectors.md:62,123` | Queued changes are compared against current property values, re-applied if the source has not changed them | Compares against the change's old value. Outcome is inverted. |
| `connectors.md:25,327` | Ownership is claimed dynamically when subjects attach | Attach-side claiming does not exist. |
| `connectors-opcua-client.md:455,497` | All reconnection paths guarantee eventual consistency through a full state read | Non-Good reads are skipped, reconnect never re-browses, and replay overwrites the read. |
| `connectors-opcua-client.md:642` | Permanent failures should not be retried | They are enqueued and retried forever. |
| `connectors-opcua-server.md:305` | Nodes remain in the address space until server restart | The code does delete them. |
| `connectors-mqtt.md:329-339` | Reconnect stall detection | The code is unreachable. |
| `connectors-websocket.md:417-421` | Unknown property, read-only, and validation failures send an Error | All three error codes have zero producers. |
| `connectors-websocket.md:383` | "Silent drops within the server are impossible" | Read-only and unknown-property writes are silently dropped. |
| `tracking.md:385` | Attach/detach callbacks fire exactly once per transition | Holds only if no handler throws. |
| `tracking-transactions.md:353` | A failed or timed-out source write with successful reverts leaves all values old | Contradicts `WriteResult`: failed means unconfirmed, and may have landed. |
