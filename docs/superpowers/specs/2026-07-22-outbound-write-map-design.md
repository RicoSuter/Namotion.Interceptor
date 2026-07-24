# Outbound write map design

Status: draft for review (revision 11)
Base branch: `feature/outbound-write-map` (currently master + #388; contains no #355 code)
Supersedes: PR #355 (`fix/capture-user-writes-during-connect`)
Prerequisite: the commit-sequence and attachment-generation stamps (partial #385), a per-property last-commit-seq, and a terminal seq-guarded write; see Prerequisite

## Summary

Replace the source-side outbound write path with a per-source **outbound write map**: for each owned property, persistent `{ sourceValue, settledSeq, handledSeq }` plus a transient pending overlay `{ desired, highSeq, attemptValue, attemptSeq, timestamp }` present only while a local write is outstanding. It unifies three things separate today, the change-queue dedup buffer, the write retry queue, and the reconcile-on-reconnect logic, driven by two loops (a fast capture loop and a flush loop) that share the map under one lock.

Correctness rests on a **commit sequence number** carried by each property change (the near-term half of #385). With it the map orders changes by commit rather than by dispatch arrival: two per-property watermarks track what has been dealt with (`handledSeq`) and what the source is confirmed to hold (`settledSeq`, with `sourceValue`), and both advance only forward. The map keeps the existing `SubjectPropertyChange` write contract; the transaction commit path keeps its wire contract, failure attribution, and rollback, but gains one coordination step with the map at commit (see Write payload and transactions).

When a write counts as settled depends on what the source can confirm, expressed as a per-source **confirmation mode** (ack or optimistic).

Phase 1 covers **sources** (model to external source). Phase 2 extends the same core to **servers** (model to connected clients) as a smaller mechanism, though it still needs a persistent published-sequence watermark. Both are specified so the phasing can be chosen with full information; the recommendation is to ship phase 1 first (reasoning in Phasing).

This design is **outbound only** (local model change to external system). Inbound (external to local) stays on `SetValueFromSource` and `SubjectPropertyWriter`; the map only observes inbound writes read-only, to advance `sourceValue` and the watermarks.

## Prerequisite: commit sequence and attachment generation (partial #385)

Each `SubjectPropertyChange` must carry two stamps, both read at the terminal field write inside the `Subject.SyncRoot` critical section (`WriteInterceptorFactory`) and threaded to dispatch through the existing `PropertyWriteContext` (a plain mutable `struct` passed by ref through the chain, the same pattern the write timestamp already uses):

- A monotonic **commit sequence number** from a per-subject counter. Commits to a subject are already totally ordered by `SyncRoot`; the stamp carries that order to dispatch instead of discarding it. The same counter provides the observation token (see Staging). This is issue #385's near-term, agreed item.
- The subject's **attachment generation**, a per-subject field bumped by the lifecycle on attach, detach, and unloaded move (a volatile write; the commit reads it under `SyncRoot`). This is new public core surface: the field must be readable by the core terminal, which cannot reference Tracking, so it lives on the subject and changes the core API snapshot. A write racing the attach or detach boundary may stamp the old generation and be classified to the old side; that is the intended semantics (a racing write is a boundary write), not an exactness violation.

Two further core pieces are required. First, a per-property **last-commit-seq**, recorded at the terminal alongside the existing per-property write timestamp, so a guard can ask "has this property committed past seq X" under `SyncRoot`. Second, a terminal **seq-guarded write** (write only if the property's last-commit-seq does not exceed a given token, evaluated inside the terminal's existing `SyncRoot` section), used by reconcile's guarded re-apply. A value-based compare is deliberately rejected: a racing local write whose value coincides with the expected value would pass a value compare and then lose to the re-applied older value (see Reconcile). An outer lock around a normal setter is not an option either, because the chain's lifecycle unwind takes `_attachedSubjects`, inverting lock order.

Of #385, only the stamp half is required; its second half (in-order or FIFO delivery) is explicitly **not** a prerequisite and is not assumed; the map reconstructs order from the stamp and does not require ordered delivery. The last-commit-seq and the seq-guarded write are required in addition to the stamps. Note the stamp is a *local commit* order: it totally orders local commits to a subject, but an inbound `FromSource` write is stamped when it is applied locally, so the sequence does not encode source-side causality (see Ordering limits). The stamps must land before phase 1.

Performance note: the two stamps grow `SubjectPropertyChange` by roughly a long plus an int, in a struct the codebase deliberately keeps tight, and every subscription copies it. The prerequisite PR is benchmark-gated (the repo's benchmark workflow), and the cost is discussed there, not assumed away here.

## Problem

Local writes to a source-bound property are lost or inconsistently reconciled across the source (re)connect lifecycle, and the current mechanism has structural weaknesses:

1. **Connect and reconnect-delay windows.** On master the outbound subscription is created inside the change-queue processor, after listen and load, so a local write during listen/load or during the retry delay is published to nothing and lost. For write-only or non-read-back properties this is a permanent divergence.
2. **In-place reconnect reconcile gap (#362).** The 3-way reconcile runs only at base-loop reconnects. All three connectors reconnect in place on their own monitor task and never reconcile, so a stale local write can overwrite a source value that changed during the outage.
3. **Unbounded and evicting buffers (F1/F2, #281).** The retry queue is a fixed drop-oldest ring, so a chatty property can evict an unrelated pending write. The change-queue buffer grows without bound under a slow flush (the observed 80k-topic MQTT death spiral).
4. **No retry driver after a transient reconcile-flush failure (F3).** A failed send is requeued but nothing re-drives it until the next unrelated write.
5. **Reorder staleness in all three connectors.** MQTT, OPC UA, and WebSocket send the dispatched value verbatim in arrival order. Dispatch runs outside `Subject.SyncRoot`, so a value delivered later can be an older commit (#385).

## Goals and non-goals

Goals: close 1 to 5 above for sources (phase 1) and the unbounded-server-memory case (#281, phase 2); structurally bound the accumulation a slow transport can cause (the per-property map; a hard bound on the shared subscription itself is deferred future hardening, see Ownership); give one reconcile policy that every load path calls; keep the writer hot path lock-free.

Non-goals: inbound ordering (for example #373); lossless or non-dedup delivery of signal properties (#282, #228, #200), a separate future projection; defining the source consistency contract (#342); in-order delivery (#385 part 2); resolving local-versus-source causal order beyond what a local-first model already gives (see Ordering limits).

## Core concept

Per owned property:

```
persistent (for any property once written or observed by this source):
    sourceValue   // value the source is confirmed to hold, at settledSeq
    settledSeq    // highest commit-seq the source ACCEPTED (drives sourceValue)
    handledSeq    // highest commit-seq DEALT WITH (accepted, rejected, or a source update)

pending overlay (only while a local write is outstanding):
    desired       // new value of the highest-seq local change with seq > handledSeq
    highSeq       // that seq
    attemptValue  // value snapshotted into the current in-flight send
    attemptSeq    // its seq
    timestamp     // changed timestamp of the desired change, for the outbound payload
```

`handledSeq >= settledSeq` always, and neither ever regresses (every advance is `max`-guarded). A change is stale, and ignored, when it cannot be newer than what has already been resolved. The gate is split by origin (below): a **local** change is stale at `seq <= handledSeq`; a **source** update is stale at `seq <= settledSeq`. Separating the two watermarks matters both ways: a permanently-rejected write advances `handledSeq` but not `settledSeq`, so a late lower-seq local retry cannot resurrect it, yet a genuine late source update at a seq above `settledSeq` (even if below `handledSeq`) is still accepted rather than discarded.

An overlay is present only while a local write is outstanding, and its invariant is `highSeq > handledSeq`. `map.Count` of overlays is the pending-write count; a separate volatile counter mirrors it for concurrent reads (see #354). Persistent state is bounded by owned-property count and cleaned on detach.

### The authority rule (via commit sequence)

Dispatch is FIFO by arrival, not commit order: the terminal field write commits under `Subject.SyncRoot`, and dispatch happens on the unwind after the lock is released (`Tracking/InterceptorSubjectContextExtensions.cs:94-96`). The commit-seq stamp restores the order, so the map trusts change values once ordered by seq rather than re-reading the live field:

- **Desired** is the new value of the highest-seq local change with `seq > handledSeq`. Whichever change dispatches first, the highest seq wins.
- **sourceValue / settledSeq** advance only when the source accepts a value (an ack, or an observed source update) at a `seq` greater than the current `settledSeq`. One honest caveat: a source update racing an in-flight send can leave `sourceValue` behind what the ack proved (the settle guard skips the advance), repaired when the transport echoes the sent value back; `sourceValue` is therefore confirmed-or-echo-repaired, not a strict invariant.
- **handledSeq** advances on any resolved outcome (accept, permanent reject, or source update).
- **A source update supersedes an older overlay:** an update at seq `s` drops any overlay whose `highSeq <= s` (the source is now the authority for that property); the overlay survives only if a newer local commit (`highSeq > s`) followed. This token guard applies everywhere an overlay can be dropped, including reconcile.
- **Clear on settle** uses the in-flight **attempt snapshot**, not the current `desired`, so a value that changed mid-send is never falsely recorded as settled.

Because the map stores the change's own captured value snapshot (a clean copy taken inside the interceptor, not a re-read of a backing field), there is no torn-read exposure at capture, flush, or reconcile.

## Phase 1: sources

### Confirmation modes

`sourceValue`/`settledSeq` advance when a write is confirmed at the source. What confirms it is a per-source declared mode:

- **Ack** (OPC UA; MQTT with QoS1; a WebSocket app-level ack if added): a real protocol acknowledgement means the source took the value. Settle on the ack.
- **Optimistic** (MQTT QoS0, WebSocket by default, any transport with no acknowledgement): settle on send-return, a documented weaker guarantee (baseline may record a value the source dropped; reconcile-on-reconnect repairs most divergence).

An earlier design carried a third "echo" mode. It is removed: in a context with `WithEqualityCheck` (the standard `WithFullPropertyTracking` composition) an outbound write leaves the model already holding the value, so the source's echo of it equals the current value and is suppressed by the `[RunsFirst]` `PropertyValueEqualityCheckHandler` before it dispatches, making a model-level echo settle unsatisfiable. Without the equality check the echo does dispatch and is simply handled as an ordinary source update (registers advance); neither mode depends on it. A transport wanting stronger-than-optimistic must provide a real ack. Servers (phase 2) have no confirmation machinery, because server publications never need source-wins reconciliation.

### Ownership, threading, and memory bound

Two loops share the map under a single lock:
- **Capture loop.** Drains the subscription and applies each change by seq. Cheap and non-blocking; it never awaits a transport, so once running it keeps the subscription queue drained.
- **Flush loop.** Sends overlays. It may block on a slow transport, but it holds the lock only to pick entries and to record the outcome, releasing it across the send, so capture proceeds during a slow or hung send.

Decoupling capture from flush bounds the accumulation a slow transport can cause: a single loop that both captured and flushed would, while parked in a slow send, let producers fill the unbounded subscription queue (the #281 shape) with no way to drain it. The per-property coalescing into the map is the bound and lives in the source-local map, not in the shared `PropertyChangeQueueSubscription`, which stays lossless for other consumers. The writer hot path stays lock-free: producers enqueue into the subscription; the capture loop is its single consumer.

The subscription queue itself stays **unbounded in phase 1**. Capture never blocks on a transport, so the queue cannot fall behind for the reason the current processor's buffer does, and the residual exposure (a stalled consumer) is identical to any subscription consumer on master. A bounded backstop is deferred as future hardening, with one recorded constraint: dropping raw changes and resyncing is not a valid recovery, because a dropped local change's intent is unrecoverable after the resync load overwrites the model. A future backstop must record dropped properties and seed provisional overlays from a synchronized `{value, seq}` snapshot (under `SyncRoot`) before the resync load runs.

**Loop start ordering.** The subscription is created at source start, so nothing is missed, but the capture loop begins draining only after the connection attempt's listen, load, and ownership claims complete (mirroring the base-loop ordering that already applies to flush). This defines the disposition of connect-window writes to properties whose ownership is claimed mid-connect: their changes wait in the queue and are captured after the claim exists, never silently dropped. For write-only and non-read-back properties (Problem 1's permanent-divergence case) no staged observation exists, so the overlay is preserved and flushed; for loadable properties the write enters the baseline-absent reconcile branch (see Reconcile) and is preserved and sent, subject to the usual same-property timing race when the source also changed the value. During in-place reconnects capture keeps running (ownership already exists). A `Local` change dequeued for a property this source never owns is ignored. The backlog accumulated before the first successful connect is unbounded during a long first-connect outage, the same exposure as master.

### Write payload

The existing `ISubjectSource.WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange>)` contract is kept. The map constructs the outbound change from the entry: `old = sourceValue`, `new = desired`, `timestamp = timestamp`, carrying the seq, and snapshots `attemptValue = desired`, `attemptSeq = highSeq` at the moment of send. Keeping the contract means `SourceTransactionWriter` keeps its payloads, failure attribution, and rollback unchanged. Its commit path does need one coordination step, though: a transaction writes the source directly, and without ordering against the map a pending flush can write an older overlay value between the transaction's external write and its local `Confirmed` commit, after which the confirmed capture would record the transaction value as `sourceValue` while the source actually holds the flushed value. Two rules close this: the transaction holds the per-source write lock (which flush also takes per send) across its external write, and at commit it **settles the map synchronously** under the map lock (advance `sourceValue`/`settledSeq`/`handledSeq` to the confirmed value and seq, dropping superseded overlays) instead of waiting for the asynchronous capture of the `Confirmed` change, which then gates as stale. Flush's pre-send re-check thereby rejects superseded overlays immediately, with no capture-lag window.

### Capture

The capture loop drains the subscription. Any change whose stamped attachment generation does not match the property's current generation is from a superseded attachment and is ignored (the attach fence; see Detach). Otherwise, per change with commit-seq `s`:
- `Local` on an owned property, `s <= handledSeq`: stale local retry. Ignore.
- `Local` on an owned property, `s > handledSeq`: a pending write. If `s > highSeq` (or no overlay), set `desired = change.new`, `highSeq = s`, `timestamp = change.timestamp`.
- `Origin.Source == thisSource` (source update), `s <= settledSeq`: stale source echo. Ignore.
- `Origin.Source == thisSource`, `s > settledSeq`: the source now holds F. Set `sourceValue = F`, `settledSeq = s`, `handledSeq = max(handledSeq, s)`. Drop any overlay with `highSeq <= s` (source superseded it); keep an overlay only when `highSeq > s`.
- `FromSource`/`Confirmed` from another source on an owned property, `s > handledSeq`: capture and forward as an overlay `desired`, applying the same `s > highSeq` monotonic rule as a local change; `s <= handledSeq` is stale and ignored.

Integration requirement: a connector must claim property ownership **before** applying its initial state for properties that already exist, since capture filters on ownership; the WebSocket client currently applies the snapshot before claiming, fixed in phase 1. Properties the snapshot itself creates (dynamic subjects and properties) cannot be pre-claimed: they are claimed immediately after creation and **seeded** (registers initialized from the applied snapshot value as `sourceValue`, no overlay), which is safe because a property cannot have connect-window local writes before it exists and is not visible to application code before the apply completes.

### Staging (loads)

During a load window (base-loop or in-place) source updates on owned properties do **not** advance the live registers; instead each observed value is **staged** per property. Staging is fed by the load path's applied observations directly, not by dispatched changes, so a loaded value equal to the current model (which the equality handler vetoes before dispatch) still produces a staged observation. Each staged observation carries an explicit **observation token**: the subject's commit counter read under `SyncRoot` **immediately before the apply attempt** (capturing it after an equality veto would let a local write racing the apply be misclassified as older than the observation). Ordering is defined by the token: a local commit with `seq > token` is newer than the observation; one with `seq <= token` is superseded by it. When several observations exist for one property (overlapping loads, reordered arrival), the staging slot keeps the observation with the **highest token**; a lower-token observation never overwrites a higher-token one, regardless of arrival order.

### Flush, retry, permanent-drop

The flush loop is woken by a **composite wait** on a map-level signal set by capture, reconcile, or a backoff timer (the subscription's own wait primitive cannot be shared, since capture is its single consumer). Before sending it re-checks the overlay is still live (`highSeq > handledSeq`), snapshots the attempt (`attemptValue = desired`, `attemptSeq = highSeq`), sends the constructed change, and then per the confirmation mode treats success as settled (ack) or settles on send-return (optimistic):
- On settle, advance `sourceValue = attemptValue` and `settledSeq = attemptSeq` **only if `attemptSeq > settledSeq`** (a concurrent source update may have moved `settledSeq` past this attempt during the send); always `handledSeq = max(handledSeq, attemptSeq)`. Drop the overlay only if `highSeq == attemptSeq`; otherwise keep it with the newer `desired`.
- On transient failure, keep the overlay. The loop is the retry driver (fixes F3); retries run on a backoff.
- On permanent failure (for example `BadNotWritable`), advance `handledSeq = max(handledSeq, attemptSeq)` (a tombstone; `settledSeq`/`sourceValue` are **not** advanced), drop the overlay only if `highSeq == attemptSeq`, and surface once via diagnostics (#332). This requires `WriteResult` to carry the transient/permanent split, which today is only counted inside OPC UA's `OutboundWriter`; exposing it per change is new, snapshot-tested surface, and the classifier needs two predicates, one for subscribe/write access and one for browse/read.

Baseline-absent (no `sourceValue` yet) defaults to send-current. The picked batch is ordered by commit seq within each subject; cross-subject ordering is unspecified. This differs from the current processor's chronological last-occurrence order across properties and is a stated behavior change.

### Reconcile after any load

After every completed load, the flush loop reconciles from the **staged observations** (never a live model read). The overlay supersede rule is token-guarded here exactly as at capture: an overlay with `highSeq > token` represents a local commit **newer** than the observation and is never dropped by reconcile. For each owned property with a staged observation `S` (token `t`):
- **No overlay (clean property):** update the registers to `S`/`t` (`max`-guarded). Nothing else to decide.
- **Overlay with `highSeq > t`:** keep the overlay untouched; flush sends it. Refresh the registers from the observation (`sourceValue = S`, watermarks advance toward `t`, `max`-guarded); the overlay invariant holds since `highSeq > t`.
- **Overlay with `highSeq <= t`:** the three-way decision:
  - `S == desired`: the source already holds the intended value; registers to `S`/`t`, **drop** the overlay (nothing to send).
  - `S == pre-load sourceValue`, **or no pre-load `sourceValue` exists** (baseline-absent, the first-connect case): the observation gives no evidence of divergence, so the local intent is preserved. Re-apply `desired` through the terminal **seq-guarded write** (write only if the property's last-commit-seq is `<= t`), **before touching the overlay**. Three outcomes, decided under `SyncRoot`:
    - Guard passes and the model does not already hold `desired`: the re-apply commits fresh (new seq, current generation); drop the old overlay, advance the registers to `S`/`t`, and capture turns the fresh commit into a new overlay that flush sends.
    - Guard passes and the model already holds `desired` (the load apply was vetoed or never ran): no write is needed; keep the overlay and the registers unchanged, and flush sends the kept overlay.
    - Guard fails (a local commit with `seq > t` exists): keep the overlay and the registers unchanged; the newer commit's dispatched change updates or replaces the overlay when captured. A value compare is deliberately not used: a racing local write whose value happens to equal the staged `S` would pass a value compare and be overwritten by the re-applied older `desired`, violating the token guard.
  - otherwise (`S` differs from a known pre-load `sourceValue` and from `desired`): the source diverged; drop the overlay (source wins), registers to `S`/`t`.
- **No staged observation** for the property (an OPC UA read with a bad status code, an MQTT topic with no retained message): preserve the overlay and the registers unchanged. A missing read is never treated as divergence.

All register advances during reconcile are `max`-guarded like everywhere else, so an older load cannot regress state written by a newer settle.

### Concurrency and the load handshake

Base loop: `LoadInitialStateAndResumeAsync` and reconcile run before flush begins, sequentially, no concurrency.

In-place reconnect: the load runs on the connector's monitor task, concurrently with the loops, and base and connector loads can **overlap** (`SubjectPropertyWriter` explicitly anticipates concurrent calls). The monitor task must not touch the map. Handshake:
1. Each load increments a **suspend generation/refcount** (a plain boolean is unsafe: the first reconcile would clear it while a second load is still applying) that suspends flushing, live-register advance from source updates, and overlay drop for owned properties, and enables staging. The increment and its matching decrement are scoped in a **finally**, so a failed or cancelled load always releases the suspension and wakes the loop.
2. The load runs. The epoch encloses the **whole** of `LoadInitialStateAndResumeAsync`, including the buffered-update replay it performs after the load action, so every apply in that scope is staged under the same regime and no value is processed under two. All these applies dispatch synchronously during each apply's unwind, so by program order (happens-before, even if the physical thread changes across the load await) they are all in the subscription queue before the next step.
3. The same finally enqueues a **load-epoch marker** through the change channel regardless of outcome, carrying success or failure, and raises the pending-reconcile signal on the map-level signal. A marker only on success would let flushing resume after a failed load while its partially applied changes are still queued.
4. When the refcount reaches zero, the capture loop drains **through the last load's marker** (a bounded cut: everything the load applied is provably before the marker, by step 2) before flushing resumes. A success marker triggers reconcile from the staged observations. A failure marker **discards** that epoch's staged observations and applies the missing-read rule (overlays and registers preserved), so nothing is reconciled against a partial snapshot; the retried load restores register freshness moments later. Converting the partial observations into live source updates was considered and rejected as needless machinery. Draining "until observed empty" is deliberately not the rule: under sustained write load an empty queue is a moving target and reconcile could starve; the marker gives the same guarantee with a bounded wait.

The map stores captured change values and staged observations, so a local write whose hint is still queued when suspension begins is not lost even if the load overwrites the model; its value is recovered from its change when capture processes it, and the token-guarded reconcile keeps its overlay. A write that commits during the load window remains the acknowledged timing race between local-wins and source-wins **only** for the case where the source also changed the same property; a purely local racing write survives via the token guard.

Two convergence caveats: a write already on the wire when the reconnect snapshot was taken converges only because the server echoes it back (a per-transport invariant; a no-echo transport in optimistic mode recovers only on the next write or reconnect); and MQTT has no load-complete point (`LoadInitialStateAsync` returns null; retained messages trickle in after subscribe), so on MQTT there are no staged observations, reconcile-after-load does not run, and the #362 stale-overwrite window remains timing-dependent there (see What this closes).

### Detach, re-attach, and move

`PropertyReference` holds a strong subject reference, so stale state pins a detached subject and breaks the count bound. Cleanup is lazy under the lock: every flush and reconcile pass drops state whose property no longer resolves or is no longer owned by this source; state is also dropped on detach.

Stale queued changes across a detach/re-attach are rejected by the **attachment generation** stamped on each change at commit (see Prerequisite): the lifecycle bumps the subject's generation on attach, detach, and unloaded move (a volatile write, no lock taken), and the commit reads it under `SyncRoot`, so capture can reject any change whose stamped generation is not the property's current generation with a plain comparison. A write racing the boundary is classified to the old side and rejected, which is the intended semantics for a boundary race. This replaces an earlier sequence-peek fence, which could discard a genuine post-attach write whose sequence was reserved before the peek and cause permanent divergence. `sourceValue` is re-established by the next load (or by seeding, for snapshot-created properties).

Lock order: the generation gate is a pure comparison at capture under the map lock, so the fence adds no lifecycle-to-map lock pair. The one cycle to avoid is a reconcile re-apply of a **subject-typed** owned property under the map lock, which would take `_attachedSubjects` (`map -> _attachedSubjects`); reconcile re-applies therefore run **off** the map lock, via the terminal seq-guarded write (never an outer `SyncRoot` around the setter, which would invert order against the lifecycle unwind inside the chain).

### What replaces what

The map replaces, for sources: `WriteRetryQueue` (entirely), the source's use of `ChangeQueueProcessor`, and `ReapplyRetryQueue`. Sources construct the change-queue processor privately today, so removing that use is clean on master. There is no borrowed-subscription constructor and no positional supersede on master (those exist only on the #355 branch).

## Ordering limits

The commit-seq orders local commits to a subject, so it fixes reorder *within* the local write stream (max-seq desired, and a stale gate for late local retries). It does not order a local write against a delayed source update: an inbound `FromSource` change is stamped when applied locally, so a late source notification of an already-superseded value receives a fresh high seq and is treated as the newer commit. Under commit-seq authority that is the defined outcome (later commit wins), but it means a genuine local write can be gated by a late source apply. This is the local-first, source-authority timing-dependence, and it is a steady-state property, not only a reconnect one. The map does not resolve it (no transport gives a portable source-side sequence); it is not a regression (master's inbound apply already clobbers a pending local write). Use source transactions when a deterministic outcome is required.

## Phase 2: servers

Servers publish the model's current value to connected clients. The model is authoritative, so server outbound is a smaller mechanism than source outbound: no confirmation modes, no `sourceValue`, no reconcile, no source-wins, no load handshake, because server publications never need source-wins reconciliation. Publication can still throw or be cancelled, so `publishedSeq` advances only after a successful publish and a failed entry stays dirty and retryable (a bounded retry, not a reconcile). OPC UA is a synchronous node update, MQTT injects into the in-process broker, and WebSocket failures are per-connection with a reconnect resync, so the failure surface is small but non-zero.

It is **not** purely transient state, however: it still needs a persistent per-property **published-sequence watermark**. Without it, once the highest-seq entry for a property is published and removed, a delayed lower-seq change would create a new entry and publish stale data. So a server entry keeps the highest-seq change per property for publishing and retains a `publishedSeq` watermark to reject late lower-seq changes, the same staleness guard as `handledSeq`, minus everything else.

The fast-capture / separate-flush split is **mandatory** in phase 2 as well: without it the upstream subscription queue grows unbounded under a slow client fan-out, so #281 would be only conditionally fixed. With the split plus the per-property map and watermark, resident state is bounded at distinct-property count and no property's latest value is lost (unlike the lossy drop-oldest `maxQueueDepth`).

### WebSocket fits one map

WebSocket publishing is per-server broadcast, not per-connection selection: one processor per handler, one serialized message fanned to all connections, a server-level path filter only. A single per-server map fits. Two behaviors must be preserved: the monotonic per-server sequence stamped under the apply lock (snapshot consistency), and sender-inclusive relay (an inbound update's origin is the connection, not the processor source, so it is re-broadcast).

### Blast radius

`ChangeQueueProcessor` is public and API-snapshot-tested, and `WebSocketSubjectHandler.CreateChangeQueueProcessor` returns it publicly, so replacing the server use breaks two package API snapshots plus internal server ordering tests. With the commit-seq stamp the server keeps the highest-seq change per property (more correct than arrival order, still a real value-and-timestamp pair), so publishing behavior is preserved.

## Phasing decision

Recommendation: ship phase 1 (sources) first, then phase 2 (servers).

Reasoning: servers are a smaller mechanism bolted onto three public, snapshot-tested surfaces. Bundling adds two API-snapshot breaks, server ordering review, and MQTT-relay and WebSocket-sequence regression risk to a phase whose hard problems (the load handshake, confirmation modes, attempt state, the two watermarks, staging, the attach fence, overlapping-load refcount) are all source-side. A server regression in a bundled PR could force reverting the source fixes it shipped with.

Cost of phasing: #281 stays open one phase longer (do not use the lossy `maxQueueDepth` as a stopgap), and `ChangeQueueProcessor` plus its tests remain servers-only for one phase. Acceptable because the processor is already server-shaped on master.

Option to bundle: phase 2 is a smaller subset with a favorable WebSocket fit, so one PR is feasible; it is a risk-appetite tradeoff. The prerequisite stamps are shared by both phases.

## What this closes, shrinks, or obviates

- **#385** (commit-order sequence numbers): the near-term stamp half (plus the generation stamp) is a **prerequisite**. Part 2 (in-order delivery) stays out of scope.
- **#362** (in-place reconnect reconcile gap): closed by reconcile-after-any-load on OPC UA and WebSocket. On MQTT there is no load-complete point, so no staged observations exist and the stale-overwrite window remains timing-dependent; #362 is narrowed there, not closed.
- **PR #355**: superseded.
- **#332 / PR #333** (permanent write failures retried forever): folded into the flush loop's permanent-drop (tombstone via `handledSeq`), contingent on exposing the transient/permanent split in `WriteResult`.
- **#372** (add a fourth `Correction` origin kind): closeable. Superseded by #375 independently, and routing on `Origin.Source` identity means no fourth kind is needed.
- **#375** (value-assertion writes): delivery subsumed, detection retained. The map subsumes delivery but not detection (the equality-suppressed divergence #375 targets never dispatches, so the map never sees it); #375's equality-handler detection must survive.
- **#349** (divergence repair after failed commits): partially subsumed. The repair action (source-wins resync via a load) becomes automatic through reconcile-after-any-load; #349's transaction-layer classification and reporting stay.
- **#281** (unbounded server memory): fixed in phase 2 (with the mandatory fast-capture split).
- **#354** (source sync state): integration only. `PendingWriteCount` becomes the map's volatile overlay count. `Synchronized` stays lifecycle-driven and must not be coupled to map-empty (#354 explicitly rejects that).
- **#363** (source-inert supersede cleanup): not applicable on master; it describes a #355-only artifact.

Out of scope, stated explicitly: #282, #228, #200 (lossless delivery); #342 (consistency contract); #373 (inbound ordering).

## Assumptions and open items

1. **Commit-seq, attachment-generation, last-commit-seq, and the terminal seq-guarded write (prerequisite).** A per-subject commit counter stamped under `SyncRoot` and threaded via `PropertyWriteContext`; a per-subject attachment generation (volatile bump by lifecycle, read under `SyncRoot` at commit, new public core surface and a core API snapshot change); a per-property last-commit-seq recorded at the terminal (the write-timestamp pattern); and a terminal seq-guarded write primitive (value compares rejected: value coincidence defeats the guard). Benchmark-gated (struct growth). Must land before phase 1. The sequence is a local-commit order, not a source-causal one (see Ordering limits).
2. **Per-source confirmation mode.** Ack (OPC UA; MQTT QoS1; a WebSocket ack if added) or optimistic (default QoS0/WebSocket, weaker guarantee). No echo mode; the removal argument assumes `WithEqualityCheck`, and without it echoes dispatch and are handled as ordinary source updates.
3. **Two forward-only watermarks.** `settledSeq` (source accepted, drives `sourceValue`) and `handledSeq` (accepted, rejected, or source update). Both advance only via `max`; a permanent reject advances only `handledSeq`. The stale gate is split: local changes gate on `handledSeq`, source updates gate on `settledSeq`.
4. **Token-guarded supersede everywhere.** A source update or staged observation at seq/token `s` drops an overlay only when `highSeq <= s`, at capture and at reconcile alike; an overlay with a newer local commit always survives and flushes. Flush re-checks `highSeq > handledSeq` before sending.
5. **In-flight attempt state, monotonic settle.** Settlement uses the `attemptValue`/`attemptSeq` snapshot; `settledSeq`/`sourceValue` advance only if `attemptSeq > settledSeq`; the overlay drops only if `highSeq == attemptSeq`.
6. **Staged load observations with a pre-apply token.** Load observations are staged (never applied live, never re-read from the model); each carries a token read under `SyncRoot` immediately **before** the apply attempt; reconcile advances registers to the token (`max`-guarded) and treats missing observations as no-ops. Staging is fed by the load path directly, so an equality-suppressed load value still produces an observation. Per property the highest-token observation wins; overlapping loads never let a lower-token value overwrite it.
7. **Overlapping-load refcount with finally-scoped release and outcome marker.** The suspend is a generation/refcount, incremented and decremented in a finally per load, which also enqueues the epoch marker carrying the load's outcome; a failure marker discards that epoch's staged observations (missing-read rule), so flushing never stays suspended and never resumes past undrained load applies. The epoch encloses the whole of `LoadInitialStateAndResumeAsync`, buffered replay included.
8. **New primitives.** A map-level wake signal for the composite wait, and a load-epoch marker enqueued from the load's finally, carrying success or failure (the drain cut; "drain to empty" is rejected as starvation-prone). The subscription's own wait primitive is untouched.
9. **Attachment-generation fence and seq-guarded re-apply.** Capture rejects a change whose stamped generation is not current (boundary races classify to the old side, by design). Reconcile re-applies run off the map lock via the terminal seq-guarded write: the old overlay is dropped only when a fresh re-apply commit exists (recaptured as the new overlay); when the guard fails or the model already holds `desired`, overlay and registers are kept. This avoids a clobbered user write (including one whose value coincides with the staged observation), the vetoed-apply write loss, the overlay-invariant violation, and any lock-order inversion.
10. **Loop start ordering.** Capture begins draining after the attempt's listen, load, and ownership claims; connect-window writes are captured from the queued backlog after claims exist. Dynamic snapshot-created properties are claimed immediately after creation and seeded.
11. **Existing write contract kept; transactions coordinate at commit.** `WriteChangesAsync` still takes `SubjectPropertyChange`, and `SourceTransactionWriter` keeps payloads, failure attribution, and rollback; but the commit holds the per-source write lock across its external write and settles the map synchronously, so a pending flush can never interleave an older value between the external write and the confirmed commit.
12. **Unbounded subscription in phase 1.** No cap and no overflow backstop; the two-loop split prevents the failure mode. A future backstop must seed provisional overlays from a synchronized snapshot before any resync, never drop-and-resync.
13. **Phase-2 published-seq watermark, mandatory split, publish retry.** Servers need a persistent per-property watermark to reject late lower-seq changes and the fast-capture split is mandatory so #281 is truly bounded. `publishedSeq` advances only on a successful publish; a failed publish stays dirty and retryable.
14. **`WriteResult` permanent/transient surface.** Needed for #332; new snapshot-tested surface; classifier needs the two-predicate access split.
15. **Base branch.** Written against master (plus #388); supersedes #355 rather than building on it.
