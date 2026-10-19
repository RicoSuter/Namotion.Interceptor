# Change queue processor: draining on the flush tick

Maintainer notes for how `ChangeQueueProcessor` consumes its `PropertyChangeQueueSubscription` in buffered mode. Consumer-facing behavior is in [tracking.md](../tracking.md) and the processor's XML docs; the delivery rules it applies are in [connector-delivery.md](connector-delivery.md).

## Why the tick drains

With a buffer time above zero the processor used to run two loops: a dequeue loop that awaited `WaitToDequeueAsync`, filtered each change and moved it into a `ConcurrentQueue`, and a flush loop that drained that queue on every `PeriodicTimer` tick. Nothing reaches the connector between ticks, so the dequeue loop only decided earlier what the tick would have decided anyway. Its cost was the wake-up: every write that finds the consumer idle queues a work item to the pool's global queue, which under a loaded pool takes milliseconds to run (about 15 ms median with 512 long items queued, against about 12 µs for a blocked thread).

Buffered mode therefore has one loop. Each tick drains the subscription with `TryDequeueImmediate`, filters into the buffered queue, and starts a flush. The consumer never arms a wait, so a write costs the enqueue and a fence, and an idle processor costs one timer tick per buffer time, as the flush loop already did. Immediate mode (buffer time zero) keeps the `WaitToDequeueAsync` loop, since there each change is written as it is dequeued.

## The tick does not await the flush

The tick starts the flush and keeps the returned `ValueTask` rather than awaiting it. While that flush is still in flight, later ticks drain and bound but start no new flush; the first tick that finds it complete observes its result and starts the next. The flush gate already serializes flushes, so this changes nothing about who touches the merger.

This is what keeps `maxQueueDepth` (issue #281) working. Its purpose is bounded memory when the connector is slow, and a slow connector is exactly a flush that stays in flight across ticks. A tick that awaited the flush would stop draining for that whole time and leave the bound to the unbounded subscription queue.

Not awaiting is not enough on its own, since the flush runs synchronously until its first await, and a write handler may block synchronously, or the merge may wait for a subject lock. So the flush yields once it has taken a non-empty batch, before the merge, and continues on a pool thread while the tick loop returns to its timer. The yield is a work item reused by every flush, which the flush gate admits one at a time, queued to the current thread's local queue: that thread runs it as soon as the tick loop suspends, without waiting behind the global queue, which is the latency this change removes, and without allocating. An idle tick takes no batch and never yields.

Because the merge runs off the tick, a drain can still judge a change while the merge decides an earlier one, as the dequeue loop could. The first publish therefore keeps ordering itself against confirmations under the subject lock as before ([connector-delivery.md](connector-delivery.md)); nothing in that reasoning depended on the dequeue loop being a loop.

One consequence: once a slow write completes, the next flush waits for the next tick, up to one buffer time, where the old flush loop resumed on the tick that had coalesced during the write.

## maxQueueDepth

The bound is enforced as the tick moves changes into the buffered queue, dropping the oldest and counting each drop in `DropCount`, as before. Drops are reported to the drop handler once per tick rather than once per overflowing change.

Between two ticks the changes wait in the subscription queue, unbounded, as they waited before whenever the dequeue loop's wake-up was late. The memory held is therefore the bound plus at most one buffer time of writes plus the batch in flight. That is the same order as the batch a healthy tick takes anyway, and it does not grow with how long the connector stays slow.

Each drain takes at most the subscription's count at the start of that drain, so a producer that outpaces the drain cannot keep a tick from reaching its flush. What it leaves is taken on the next tick.

## Filters at tick time

The self-source echo filter, the property filter and, on a bounded processor, the `ChangeDeliveryFilter.IsCurrent` check now run when the tick drains, not when the dequeue loop woke. Each moves later by at most one buffer time, which places it between the old decision point and the merge, which already decides supersession at flush time.

- **Echo filter.** `NeedsWriteBack` reads a sticky "written to a source" mark, which only goes from false to true. Deciding later can only turn a skipped echo into a written-back confirmation, never the reverse, so the repair it exists for is never lost.
- **`IsCurrent`.** A newer commit only grows, so deciding later skips more superseded changes, and only ones the merge would have dropped by the same rule.
- **Property filter.** Its contract already says the property may be unregistered by the time it runs. A subject detached within the buffer time is now filtered out instead of written once more after its detach. The old window was the dequeue loop's wake-up latency, which was not bounded at all.

## Teardown

Order after the loop ends, whether by cancellation or by `Dispose`:

1. `teardownStarted` is set, as before, so `ProcessAsync` starts `TeardownFlushBound`.
2. The subscription is drained once more, synchronously, before anything is awaited. A change written before the stop would otherwise wait in the subscription for a tick that never comes.
3. The flush still in flight, if any, is awaited, with its result observed as on a tick.
4. The final flush runs with the teardown token, then the completion handler, both unchanged.

The drain comes before the await so that, for an externally owned subscription, it cannot run after `ProcessAsync` has given up at the bound and the owner has started consuming the subscription again. For the same reason it stops as soon as the processor is disposed when the subscription is external: what it leaves stays with the owner. With an owned subscription it keeps draining after `Dispose`, so a change left in the subscription is counted as a terminal drop at the final flush, as the dequeue loop counted it.

A loop that ended by throwing (a property filter that threw) skips step 2, so a filter that throws on every change cannot replace the original exception from within `finally`. The changes left behind stay in the subscription, as they did when the dequeue loop threw.

Terminal accounting does not change: `CloseDeliveryAndDrain`, `TryAdmitMergedDelivery` and the terminal handler see the same buffered queue and delivery state. `Dispose` additionally disposes the timer, so a running loop ends at once instead of at its next tick.

## Externally owned subscriptions

The internal constructor takes a subscription that outlives the processor, and the source base reuses it across reconnects. Single consumer holds by construction: the loop and the teardown drain run on the processing task and never concurrently with each other, and the teardown drain finishes before anything is awaited (see Teardown).

Changes that arrive after the teardown drain stay in the subscription for its owner, which parks them in the retry queue before the next attempt. That is the same handover as before, with the drain taking what the dequeue loop would have taken.

One behavior differs. The dequeue loop also ended when the subscription was disposed under it, because `WaitToDequeueAsync` returned false. The tick loop does not observe that, since the subscription exposes no completion flag, so a buffered processor whose external subscription is disposed keeps ticking on an empty queue until it is canceled or disposed. The only caller disposes the subscription after the processor has stopped, so nothing relies on it, and adding a public `IsCompleted` to the subscription for this alone is not worth the API.

## QueueDepth

`QueueDepth` still counts the buffered queue only. A change waiting in the subscription for the next tick is not counted: an external subscription is shared by the whole context, so its count includes changes the filters will drop and would make the metric noisy. On a healthy processor it therefore reads near zero, rising when a flush stays in flight across ticks, which is the backlog the metric exists to show.
