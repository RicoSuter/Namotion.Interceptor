# Connector Teardown Ownership Redesign

## Status

Approved in conversation on 2026-08-31. Implementation starts from `master` in an isolated worktree and supersedes the existing PR 485 implementation.

## Context

PR 485 tries to keep connector shutdown bounded while accounting for accepted changes that remain unconfirmed at the deadline. Its current implementation lets terminal close race with handler invocation, lets the processor and retry queue count the same batch, and spreads ownership across processor state, source state, and retry state. Incremental fixes add states without establishing a single owner.

The redesign replaces shared ownership with one synchronous handoff. It must remain correct when a write, cancellation callback, logger, or drop callback blocks, and it must keep production code smaller than `master` while allowing tests to grow.

## Goals

- Keep connector processing shutdown bounded by one internal five-second deadline.
- Account every accepted batch left unconfirmed at the deadline exactly once.
- Give every batch exactly one owner at every point in its lifetime.
- Prevent admission after terminal close.
- Preserve ordering, batching, retry, partial-failure, and bounded-queue behavior.
- Keep public API unchanged except for the already approved removal of the unreleased per-connector teardown timeout controls.
- End with fewer production C# lines than `master`; tests are excluded from this budget.

## Non-goals

- Guarantee that an arbitrary callback cannot physically be entered after `ProcessAsync` returns when its admission linearized before close. Admission is the write-start boundary.
- Add per-property synchronization state. This redesign makes ownership reliable enough for that future feature but does not expose it.
- Change retry ordering, queue capacity, delivery rules, or connector protocols.

## Admission Contract

Successful atomic admission is the write-start linearization point. If admission wins before close, the batch is in flight and its callback may complete or be entered later. If close wins first, the batch is rejected without invoking the source. This is the strongest guarantee compatible with a hard shutdown bound and handlers that can block synchronously.

## Ownership Model

### Direct and server handlers

`ChangeQueueProcessor` owns a change while filtering, buffering, merging, and delivering it through a direct handler. It atomically admits the merged batch before invocation. Success releases ownership. Failure accounts and releases ownership. Terminal close claims all admitted direct work exactly once, and late completion cannot change that verdict.

### Source handlers

A source processor uses a separate internal handler contract that takes ownership synchronously. `WriteRetryQueue.WriteAsync` registers the batch under the queue lock before returning its `ValueTask`; this is the complete processor-to-queue handoff and must occur before the method's first await.

Once handed off, the processor never reports that batch. The retry queue owns it while it waits behind older writes, is pending, is in flight, or is moved back to pending after a partial failure. Queue retirement atomically claims all queue-owned batches. Registration after retirement reports the batch without invoking the source. Late success, failure, or requeue after retirement does not report it again.

Connect-window writes enter the same retry ownership protocol directly. Retry overflow remains attributed to outbound retry diagnostics.

## Processor Teardown

`ProcessAsync` has a small outer coordinator around the dequeue and periodic-flush core:

1. Run the core under a private processing token.
2. Observe external cancellation through a lightweight signal that does not execute arbitrary processing callbacks on the caller's cancellation path.
3. Request private cancellation without awaiting its callbacks.
4. Wait for the core for at most the fixed five-second deadline.
5. If the deadline expires, atomically close direct delivery, invoke the source queue's idempotent retirement callback when configured, drain locally buffered direct work, update `DropCount`, detach potentially blocking reporting and logging, and return.

Private cancellation sources, merger buffers, and other state used by late work remain owned by detached cleanup until the worker and outstanding asynchronous cancellation callbacks exit. A callback that never returns intentionally retains those sources instead of allowing disposal to race live token users. `Dispose` closes admission but never resets a terminal state or releases data still used by that worker.

The current processing cancellation helper, preparing and timed-out sentinels, repeated in-flight claims, and separately owned retry-flush timeout are removed. A source completion hook may flush retry ownership while the transport is still up, but it uses the processor coordinator's same teardown token and deadline. One terminal state plus a task/deadline race replaces the overlapping timeout machinery.

## Retry Queue Protocol

The retry queue uses its existing lock as the ownership boundary. Its state consists of open or retired status, pending changes, and the count of registered current or in-flight writes.

- `WriteAsync` registers the current batch synchronously, then serializes older retry writes before the current write.
- A successful write releases its owned count.
- A partial failure releases confirmed changes and moves only failed changes to pending ownership.
- A failure after retirement performs no requeue or additional reporting.
- `Retire` atomically closes admission, clears pending state, claims pending and active counts, and reports them once.
- Repeated retirement is idempotent.

The separate `TryAdmitWrite` check is removed because it cannot make admission and ownership atomic.

## Diagnostics and Public API

- `ChangeQueueProcessor.DropCount` is updated before a bounded stop returns.
- Potentially blocking user callbacks and logging run outside ownership locks and do not extend the shutdown bound.
- Outbound processor losses remain attributed to `OutboundChanges`; retry-owned losses remain attributed to `OutboundRetries`.
- `QueueMetrics.CreateDropReporter` becomes internal.
- The unreleased per-connector teardown timeout field, constructor parameters, and connector configuration properties remain removed. The internal five-second default is not configurable.
- The existing `AGENTS.md` comment-policy additions remain in the final PR as explicitly requested.

## Tests

Tests use events, task completion sources, and condition-based waits rather than fixed sleeps. They place barriers at the actual boundaries:

- direct admission versus terminal close;
- source ownership registration versus retry retirement;
- a current source batch waiting behind an older retry batch;
- direct and retry late success, full failure, and partial failure;
- post-retirement registration;
- repeated retirement;
- blocked cancellation, logger, drop callback, filter, and synchronous write paths;
- exact final totals across `OutboundChanges` and `OutboundRetries` after all late continuations settle;
- source stop, detach, restart, and disabled retry capacity.

Each regression test must be observed failing against the implementation that lacks its fix before production code is changed.

## Verification and Size Gate

- Run focused ownership and teardown tests repeatedly.
- Run the complete connector test project.
- Run all non-integration solution tests.
- Run public API snapshot tests and `git diff --check`.
- Compare non-test production C# additions and deletions against the `master` commit from which the redesign branch was created. Net production lines must be negative. If correctness cannot meet this gate, stop and report the reason instead of merging.
- Request an independent read-only review of the complete proposed PR tree.

## Integration into PR 485

Implementation remains on `codex/pr485-ownership-redesign` in its isolated worktree. After verification and review, create a merge commit that records the existing PR 485 head as incorporated while retaining the verified redesign tree. Advance the existing PR branch to that merge commit, so PR 485 is updated without opening another GitHub pull request or force-pushing away its history. Update the existing PR description to describe the ownership contract, API decision, tests, and production size result.
