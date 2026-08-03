# Connector connect/reconnect write reconciliation (unified capture and reconcile)

Date: 2026-07-08
Status: Design approved, pending spec review
Scope: `Namotion.Interceptor.Connectors` (`SubjectSourceBase`, `ChangeQueueProcessor`, `WriteRetryQueue`), benefiting all source connectors (OPC UA, MQTT, WebSocket). Builds on and supersedes the reconciliation half of PR #355 (`fix/capture-user-writes-during-connect`), which is not yet merged.

## Problem

Local writes made while a source connection is not fully established can be silently lost or handled inconsistently. There are two windows, currently reconciled by two different mechanisms.

1. Connect window (PR #355): writes captured by the per-connection subscription during `StartListeningAsync` and `LoadInitialStateAsync`, reconciled at drain by `ChangeQueueProcessor.IsSuperseded` using a new-value check (drop when the current model value differs from the change's new value).
2. Delay window (this design): writes made during the `retryTime` delay of a hard re-init, when the processor and its subscription have been disposed and no subscription exists. These writes are never captured, so they never enter any queue.

Two defects follow.

Delay-window correctness gap. A write during the retry delay is never captured, so on reconnect `LoadInitialStateAsync` re-reads the source and overwrites it. For a readable owned property the documented guarantee "reconnect, source unchanged, your change survives" is violated (the write is silently dropped). For a write-only or non-read-back owned property the model keeps a value the source never receives, a permanent quiescent-consistency violation.

Connect/reconnect inconsistency. On a reconnect the two windows meet: the same user action (write `O -> W` to a source-unchanged property while reconnecting) is restored if it lands in the delay window (old-value reconcile via the retry queue) but dropped if it lands in the connect window (new-value supersede). Identical action, opposite outcome, decided only by which millisecond the write occurred.

The connect window is milliseconds; the delay window is the `retryTime` delay (default 10 seconds).

## Background: current pump

`SubjectSourceBase.ExecuteAsync` runs a retry loop. Each iteration:

1. creates a `ChangeQueueProcessor` (which creates the capture subscription in its constructor),
2. `StartBuffering`, `StartListeningAsync`, `LoadInitialStateAndResumeAsync`,
3. `ReapplyRetryQueue` (2-way: re-apply a retry-queued change if the current value equals its old value, else drop),
4. `ProcessAsync` (drain subscription, send; #355 supersede applies to the entry backlog).

The subscription lifetime equals the processor lifetime, which is per connection generation. On a hard failure the processor is disposed, the loop waits `retryTime`, then a new processor and subscription are created. During that delay no subscription exists.

Transient disconnects do not hit this: they recover in place via the connector health-check loop without ending the iteration, so the processor stays alive. Only unrecoverable failures recreate it. The gap is therefore hard-reinit-only, but real.

`WriteRetryQueue` is a source-lifetime, bounded (ring buffer, drop-oldest with a throttled warning), thread-safe buffer. It is populated today only by writes that were attempted and failed.

## Goals

- No capture gap at any point in the source lifetime.
- One reconciliation semantic for every not-fully-connected write, so connect and delay windows behave identically.
- Preserve the documented reconnect policy: restore a disconnected write when the source has not changed that property, drop it (source wins) when the source diverged.
- Keep memory bounded during long outages with sustained local writes.
- Preserve the client-to-server sync that PR #355's flake fix established.
- Smallest safe diff. This is a dangerous area: minimize blast radius, add no new concurrency, reuse existing machinery (`WriteRetryQueue`, `ReapplyRetryQueue`, the pump loop), and only improve, never regress existing behavior.

## Non-goals

- TLA+ formal model updates. Tracked and updated independently on another branch.
- Changing the local-first write model or source-transaction path.
- Server (`ISubjectConnector`) connect-window behavior beyond the consequence of removing the shared supersede (see below).

## Design

### One capture, two phases, one reconcile point

Decouple capture from the per-connection processor.

- Source-lifetime subscription. `SubjectSourceBase` creates one `PropertyChangeQueueSubscription` when the source starts and disposes it on shutdown. It always enqueues owned writes, so no window has zero capture. The subscription buffers whenever no consumer is currently attached, so brief handoff instants do not lose writes.
- Not-connected draining is synchronous, not concurrent. At each connection attempt the loop drains the subscription's owned writes into the bounded `WriteRetryQueue` at two points: right after the `retryTime` delay (before `StartListeningAsync`), which also caps memory across repeated failed attempts, and again right after `LoadInitialStateAsync` (the connect window). The drain uses the same source and ownership filter as the processor (skip changes whose source is this source; skip properties not owned by this source). There is no background task and no second consumer: draining, reconcile, and `ProcessAsync` all run sequentially on the pump task, so the subscription always has at most one consumer.
- Connected phase (after reconcile): `ChangeQueueProcessor.ProcessAsync` drains the subscription and sends, as it does today for steady state.
- One reconcile point. After `LoadInitialStateAsync` has reset the model to source values and the connect-window drain has run, a single reconcile step processes everything now in the retry queue. Because every not-fully-connected write flows through a drain then reconcile, connect and delay windows get identical treatment.

Memory is bounded without a background drain: each attempt drains the accumulation since the previous attempt into the bounded (drop-oldest) retry queue, so the subscription holds at most one attempt's worth of writes at a time even during a long outage. No write is dropped except by the retry queue's existing drop-oldest bound, which logs.

### Unified 3-way reconciliation

Replaces both `ChangeQueueProcessor.IsSuperseded` and the current 2-way `ReapplyRetryQueue`. For each parked change `old -> new`, read `current` (post-`LoadInitialStateAsync` model value):

| Condition | Meaning | Action |
|---|---|---|
| `current == new` | the write is already the model value (written after the load, or load left it) | flush: send `new` to the source directly |
| `current == old` | the source is still at the baseline the write was based on | restore: `SetValue(new)` locally, which is captured and sent |
| otherwise | the source diverged from the baseline | drop (source wins) |

The reconcile drains the retry queue, partitions into restore / flush / drop, then:

- restore set: `SetValue(new)` locally. The subscription captures the re-applied write; the subsequent `ProcessAsync` sends it. This is the existing re-apply behavior.
- flush set: delivered to the source via the retry queue's flush path, re-enqueued after the drain so `FlushAsync` sends them (a no-op `SetValue` would not raise a notification, so these cannot ride the restore path). On failure they stay queued and retry on the next reconnect.
- drop set: discard.

Worked cases (all consistent across both windows):

- Delay-window `O -> W`, source unchanged: load applies `O`, `current == old` -> restore.
- Connect-window `O -> W` written before the load, source unchanged: load applies `O`, `current == old` -> restore. (Dropped by #355 today; this is the consistency fix.)
- Any window, source diverged to `S`: `current == S`, matches neither -> drop (source wins).
- Write after the load (`S -> W`, user override): `current == new` -> flush.
- Initial connect (no baseline), pre-load write, source `S` not equal to default: matches neither -> drop (source-authoritative baseline, matching #355's initial-connect intent).

### Pump loop

```
using subscription = context.CreatePropertyChangeQueueSubscription()   // source-lifetime
firstAttempt = true
while (!stopping):
    try:
        if (!firstAttempt) await Task.Delay(retryTime)   // writes here accumulate in the subscription
        firstAttempt = false
        DrainOwnedWritesToRetryQueue(subscription, retryQueue)   // delay-window writes; caps memory per attempt
        StartBuffering()
        listenLifetime = await StartListeningAsync(...)
        await LoadInitialStateAndResumeAsync(...)
        DrainOwnedWritesToRetryQueue(subscription, retryQueue)   // connect-window writes
        Reconcile(retryQueue)                            // 3-way: restore + flush + drop
        using processor = new ChangeQueueProcessor(subscription, ...)  // does not own the subscription
        await processor.ProcessAsync(...)                // connected phase (supersede inert here)
    catch (OperationCanceledException) when stopping: return
    catch (Exception ex):
        log; await DisposeListenLifetime()
        // loop; the next attempt drains whatever accumulated during this failed attempt
```

Handoff safety: draining and `ProcessAsync` run sequentially on the pump task, so the subscription has at most one consumer at any time. It keeps enqueuing across the gaps between drains and `ProcessAsync`, so writes in those instants buffer rather than lose, and are picked up by the next drain or by `ProcessAsync`.

### Component changes

- `PropertyChangeQueueSubscription`: add an internal non-blocking drain (dequeue all currently available items without waiting), used to move accumulated writes without blocking the pump. The blocking `TryDequeue` is unchanged.
- `ChangeQueueProcessor`: add a constructor overload that accepts an existing subscription and does not own it (does not dispose it on `Dispose`). The existing constructor, which creates and owns its subscription, is unchanged, so servers are untouched. `IsSuperseded` is left in place (inert on the source path, see below). Sources pass the source-lifetime subscription; servers keep the existing constructor.
- `SubjectSourceBase`: owns the source-lifetime subscription; adds the synchronous drain-to-retry-queue helper and the reconcile step; restructures `ExecuteAsync` as above. No background task is added.
- `WriteRetryQueue`: add the partitioning reconcile (restore / flush / drop). Reuse `Enqueue`, `FlushAsync`, `DrainForLocalReapply`, and the ring-buffer bound unchanged.
- Reconcile logic (today `ReapplyRetryQueue` in `SubjectSourceBase`): becomes the 3-way reconciler, using `WriteRetryQueue` partitioning plus the local re-apply and the flush call.

### #355 supersede left in place (inert on the source path)

`ChangeQueueProcessor.IsSuperseded` is kept unchanged, not removed. With the drains running before `ProcessAsync`, every window write is already moved to the retry queue and reconciled before the connected phase begins, so at `ProcessAsync` entry the subscription holds only re-applied (restore) writes and post-reconcile steady-state writes. Those satisfy `current == new`, so supersede never drops them: it is inert on the source path. Leaving it in avoids editing `ProcessAsync` and avoids any change to servers, which still construct the processor via the existing constructor and keep their startup-window collapse. This is the smallest, lowest-risk option. Removing the now-redundant supersede is a separate optional cleanup, out of scope here.

## Correctness argument

Flake (`NullableGuidType_ShouldSyncBothDirections`). The flake was a lost write caused by late capture, not by supersede. The source-lifetime subscription captures continuously, earlier and more robustly than #355. In that test the model still holds the written value at reconcile time, so the 3-way reconcile classifies it `current == new` -> flush and sends it. Same send decision #355 reached, by a more robust capture path. Root cause eliminated.

Servers. Untouched. They keep the existing constructor and the existing supersede behavior; no server code path changes.

Clients. Both windows use one 3-way reconcile with no capture gap. Strict improvement over #355: every case #355 sent is still sent, and the `current == old` case #355 dropped is now restored. Delay-window write-only and non-read-back divergence is closed because those writes are parked and reconciled rather than lost.

Quiescent consistency. After writes settle and the connection is up, every owned property's model value has been either confirmed equal to the source (drop), restored and sent (restore), or sent (flush). No owned property is left holding a value the source never received.

## Error handling, thread-safety, memory

- Thread-safety: draining, reconcile, and `ProcessAsync` run sequentially on the pump task, so the subscription has a single consumer by construction; `WriteRetryQueue` remains lock-guarded for concurrent enqueue (drain and failed-write enqueue) and reconcile drain.
- Drain failure: a drain runs on the pump task, so an exception is caught by the existing per-iteration catch and triggers a retry like any other setup failure. Undrained writes stay in the subscription and are drained on the next attempt.
- Flush-branch failure at reconcile: re-queued via `FlushAsync`, retried next reconnect.
- Memory: each attempt drains the subscription into the bounded retry queue, so the subscription holds at most one attempt's worth of writes even during a long outage; the retry queue caps the total with its existing drop-oldest and throttled warning.
- Cancellation: `OperationCanceledException` under the host stopping token exits the loop and disposes the subscription; all other exceptions retry.

## Testing

Reuse and repoint PR #355's tests to the new reconciliation. Add:

- Delay-window write restored on reconnect when source unchanged.
- Delay-window write dropped when source changed (source wins).
- Write-only / non-read-back owned property no longer diverges after a reconnect with a delay-window write.
- Flush branch: write-after-load override reaches the source.
- A write during the `retryTime` delay is drained on the next attempt and reaches the source on reconnect.
- Memory bound: sustained writes across repeated failed attempts stay within the retry queue bound (drop-oldest observed, subscription does not grow unbounded).
- Server: removing supersede keeps clients converging to the terminal value (buffered and immediate mode).
- Existing connectors unit suite green; targeted OPC UA integration.

## Open decisions

Resolved: unify both windows in this PR (not merged); leave #355 supersede in place (inert on the source path after the drains, smallest safe diff); TLA+ out of scope.
