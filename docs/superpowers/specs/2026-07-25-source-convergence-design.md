# Source convergence design

Status: draft for review (rewrite)
Base branch: `feature/outbound-write-map` (master + #388)
Supersedes: PR #355, and the outbound write map design (`2026-07-22-outbound-write-map-design.md`, revisions 1 to 14, withdrawn)

## Why this is a rewrite

The previous design tried to maintain a belief about what each source holds by **inferring** it: advance a `sourceValue` register from "I sent B and the write returned success," then order those inferences against local commit sequences. Fourteen revisions and a deep review established that this cannot work. The local commit counter is the only clock available, and it can answer exactly one question ("did a local write happen after point X"), while the inference needed two more it cannot answer: whether one source observation is fresher than another (the counter does not move when the remote changes), and whether a write landed at the source before or after a source update (remote causality). Connectors compound it by reporting success for changes they never transmitted.

The decisive property is this: **an inferred belief is not self-healing, an observed belief is.** A wrong inference persists forever; a wrong observation is corrected by the next observation.

This design keeps the half that was sound (a bounded, coalescing, retrying outbound buffer so no local write is lost) and replaces the inference layer with observation.

## The guarantee

**Quiescent convergence.** Once local writing stops and the transport is healthy, the local model and the source agree, or the property is visibly marked as not in sync. This is the guarantee AGENTS.md asks for ("state agrees once writes settle").

**Not guaranteed: instantaneous agreement.** In a local-first model a local write is applied before the source sees it, so the two differ transiently by construction. Consumers that need agreement *before* the local model changes use source transactions (write-through), which already exist and are unaffected by this design.

**Never silently wrong.** Where a transport cannot support the guarantee (see Transport tiers), the affected properties report that fact rather than claiming to be in sync.

## Core principle: intent and belief are separate

> **Sends update intent. Observations update belief. Never conflate them.**
>
> `observedValue` is written only from an observation of the source: a read, a subscription or monitored-item update, or an echo. It is never written from "what I sent," no matter what the write call returned.

Everything else follows from this rule. It is also what removes machinery: with belief observation-only there is no need for observation tokens, apply sequences, write receipts, a settled/handled watermark split, or ack-versus-optimistic confirmation modes. A transport is characterised by what it can *observe*, not by what its acknowledgements mean.

## Architecture

Three parts per source, and only the first two hold state.

### 1. Intent map (outbound)

Per owned property, while a local write is outstanding: `{ desired, commitSeq }`. Bounded by owned-property count; one entry per property, so a chatty property cannot evict an unrelated pending write and a slow transport cannot grow the buffer without bound.

- **Capture** runs on a fast loop draining the existing `PropertyChangeQueueSubscription`. It records local-origin changes to owned properties, keeping the value of the highest `commitSeq` (dispatch is FIFO by arrival, not commit order, so ordering local writes against each other needs the stamp; this is the one question a local clock genuinely answers, and it is #385's near-term item).
- **Send** runs on a separate loop so a slow or hung transport cannot stall capture. It sends `desired`, and applies backoff retry. The send result never updates belief; it only decides whether to keep retrying, stop (permanent rejection), or wait.
- Capture and send share the map under one lock.

Intent is preserved across a reconnect load: the load overwriting the model does not erase a pending entry, so a write made during an outage still reaches the source once the source is confirmed not to have moved (see Convergence check).

### 2. Belief register (inbound observations)

Per owned property: `{ observedValue, observedAt }`, written only by the observation paths (reads, monitored items, subscription messages, echoes, initial-state loads). Observations are self-correcting, so a stale or out-of-order observation is repaired by the next one rather than persisting.

### 3. Convergence check

Runs when the source is connected and the property is **quiescent**: no pending intent and no send in flight. That condition is known locally from the intent map.

For each property that has been written or observed since the last check (the map's own dirty set gives exactly this set, so this is not a full-graph poll):

- `model == observedValue`: in sync. Nothing to do.
- `model != observedValue`, source unchanged since the pre-outage observation: local intent has not reached the source. Re-send.
- `model != observedValue`, source changed: the source moved. Resolve by policy (default source-wins: adopt the observed value locally).
- No observation available: report `NotVerifiable` (see Transport tiers). Never report in sync.

The check also runs after every load, from both the base retry loop and each connector's in-place reconnect, which is the path that is missing today (#362).

Because the comparison is between the live model and an *observed* value, no sequence algebra is involved and no baseline has to be inferred. The pre-outage `observedValue` is itself an observation, so "did the source change during the outage" is answered by comparing two observations.

## Property sync state

Derived from the map and the belief register, so no new per-property storage is introduced (matching #354's design, which deliberately avoids per-property state and derives it instead):

| State | Meaning |
|---|---|
| `InSync` | Quiescent and `model == observedValue` |
| `Pending` | Local intent outstanding, send in progress or queued |
| `Diverged` | Quiescent, `model != observedValue`, and the difference cannot be resolved by sending (permanent rejection, or an unwritable property) |
| `NotVerifiable` | The transport cannot observe this property's source state (see Transport tiers) |

This is a **different axis** from #354's `SourceState`, not an extension of it. #354 answers a connection-lifecycle question ("initial load complete, live updates flowing"); these states answer a value-agreement question. The two diverge precisely where it matters: a permanently rejected write leaves a property `Diverged` while its source is legitimately `Synchronized`. #354's own problem statement targets the value question but its enum answers the connection one, so publishing #354's state as "in sync" without this axis would be silently wrong in the same way the withdrawn design was.

The relationship is therefore: this design computes the per-property truth; #354's typed event stream, wait primitives, and source-level lifecycle states are the natural way to publish it. `PendingWriteCount` is backed by the intent map's dirty count either way (it already exists on `SubjectSourceBase` today, so no dependency runs in either direction). `Diverged` and `NotVerifiable` are the states the withdrawn design lacked, and they are what makes "never silently wrong" true.

### Divergence policy (configurable)

On a permanent rejection (for example `BadNotWritable`, or a value the source refuses), no amount of resending makes the two agree. The property is **always** marked `Diverged` and surfaced through diagnostics. What happens to the model value is configurable per source:

- `RevertToSource` (**default**): adopt the observed value locally. The model then tells the truth about the device, and the fault records that the write was rejected. This is the safe default for industrial control, where an HMI displaying a setpoint the PLC never accepted is the dangerous failure mode.
- `KeepLocal`: retain the rejected value and stay `Diverged`. For consumers that prefer to hold intent and retry or resolve manually.

## Transport tiers

The guarantee is only as strong as a transport's ability to observe. This is stated per transport rather than promised uniformly, and it is per property where the capability is per mapping.

**OPC UA (reference tier, strongest).** Per-item `StatusCode` gives an exact outcome per write, including transient versus permanent classification; the read API gives explicit readback; monitored items give push observations; server timestamps give source-side time. `ReadAfterWriteManager` already implements the readback path and is the observation mechanism this design builds on rather than replacing. All five reliability properties below hold.

**WebSocket.** Sender-inclusive broadcast provides echo observations, and the server snapshot provides readback at connect. Full convergence; per-write outcomes are weaker than OPC UA (no per-item status), so a rejected write is detected by the convergence check rather than reported directly.

**MQTT**, per topic mapping, because QoS is mapping-specific:

| Configuration | Convergence |
|---|---|
| QoS 1 or 2, retained | Full: acknowledgement plus readback from the retained message |
| QoS 0, retained | Works, timeout-driven: a dropped publish produces no echo, the check sees the difference and re-sends. Self-healing on a lossy transport, at the cost of occasional redundant writes |
| QoS 0, not retained | **`NotVerifiable`.** A bus with no retention exposes no source state to read back, so convergence is undefined and is reported as such rather than claimed |

### Reliability properties (OPC UA reference tier)

1. No local write silently lost.
2. No stale local value pushed over a newer server value.
3. Divergence always detected and visible.
4. Permanent failures surfaced once, not retried forever (#332).
5. Reconnect neither loses nor corrupts state.

Each is a testable statement, verified by the integration suite and the ConnectorTester chaos harness rather than asserted here.

## Prerequisites

Much smaller than the withdrawn design, because observation replaces inference:

1. **Commit-sequence stamp** on each change, taken in the terminal's `SyncRoot` section and threaded through `PropertyWriteContext` (the write-timestamp pattern). Used only to order local writes against each other. This is #385's agreed near-term item. Benchmark-gated: it grows `SubjectPropertyChange`, and the repo benchmarks such changes.
2. **Per-change write outcomes.** `WriteResult` must report, for every submitted change, exactly one of `Accepted`, `PermanentlyRejected`, `Transient`, or `NotAttempted`. Today OPC UA returns `WriteResult.Success` when every change was skipped as unmapped or unwritable (`OutboundWriter.cs:45`), and MQTT skips unmapped or unserialisable changes similarly; the map must never treat a skipped change as sent. OPC UA classification already exists internally and needs exposing per change, with the two-predicate access split (subscribe/write versus browse/read).
3. **Ownership epoch per property.** A property can be released and reclaimed without its subject detaching, so a per-subject attachment generation does not fence it. Cleanup happens on release, not lazily.

Deliberately **not** required any more: observation tokens, apply sequences, write receipts, the settled/handled watermark split, confirmation modes, and the transaction gate protocol (see below).

## Transactions

The withdrawn design proposed holding a per-source write gate across external write, local apply, rollback, and map settlement. That is not implementable on the current contract: `WriteToSourcesAsync` returns before the local apply, revert is a separate call, and `SubjectSourceExtensions` acquires and releases the source write lock inside a single call.

Under this design it is also unnecessary. Transactions do not settle belief, because nothing settles belief except observation; a transaction's confirmed value is observed like any other source value. Transactions and the send loop still must not interleave writes to the same source, which the existing per-source write lock already provides. `ITransactionWriter`, `SourceWriteResult`, and the rollback path are unchanged.

## What this closes

- **PR #355** and the withdrawn write-map design: superseded.
- **#362** (in-place reconnect skips reconcile): closed by running the convergence check after every load, on every path.
- **#332** (permanent write failures retried forever): closed by per-change outcomes plus the `Diverged` state.
- **#281** (unbounded outbound memory): closed by per-property coalescing plus the capture/send split (phase 2 for servers).
- **#354**: extended, not duplicated. `PendingWriteCount` and per-property state come from this map.
- **#385**: its near-term stamp half is a prerequisite; in-order delivery remains out of scope.

Narrowed rather than closed, honestly stated: **#342** (source consistency contract) is partially served by the transport tiers and the state model, but this design does not define the full contract. **#375** and **#349** are *not* obviated; their detection halves remain necessary.

## Phasing

1. **Prerequisites**: commit-seq stamp (benchmark-gated), per-change write outcomes, ownership epoch. Independently justified and release-safe alone.
2. **Sources**: intent map, belief register, convergence check, sync state, OPC UA first as the reference tier, then WebSocket and MQTT with their tiers.
3. **Servers**: the same capture/coalesce/send split without belief or convergence (a server publishes, it does not synchronise), closing the server half of #281.

## Open items

1. Whether the convergence check should also run periodically while idle, or only after loads and at quiescence after writes. Periodic checking catches drift no event reports, at the cost of traffic; likely an opt-in interval per source, default off.
2. The exact staging mechanism by which every inbound apply reaches the belief register. `SubjectPropertyWriter.Write` takes an opaque action with no property or value metadata, and some paths call `SetValueFromSource` directly (OPC UA read-after-write), so there is no single choke point today. This needs a concrete plumbing decision before implementation.
3. Derived properties: `GetFinalValue()` re-evaluates derived getters outside `SyncRoot`, so a change's value is not necessarily the value committed at that sequence. Whether derived properties are ever source-bound outbound, and if so how their value is captured, needs settling.
4. A concurrency and chaos test matrix, written before implementation rather than after, covering: reconnect during an in-flight write, dropped publish on QoS 0, permanent rejection, ownership release and reclaim under load, and overlapping loads.
