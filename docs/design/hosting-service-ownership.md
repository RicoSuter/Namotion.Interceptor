# Hosted Service Ownership: Internal Design

This document describes the internal concurrency model of `Namotion.Interceptor.Hosting`: how
`HostedServiceHandler`, `HostedServiceTarget` and `HostedServiceGate` decide when a subject bound
hosted service starts, stops and is disposed. For user-facing documentation, see the
[Hosting](../hosting.md) documentation.

## Overview

A hosted service runs exactly while its subject is in the graph. The `HostedServiceHandler` on the
subject's context is the only thing that starts or stops it, and it disposes exactly what it created.
Everything below exists to keep that true under concurrency: lifecycle events fire from inside
`LifecycleInterceptor`'s `_attachedSubjects` lock, callers attach and detach from arbitrary threads,
and the host starts and drains underneath both.

The unit of management is a **target**: either a subject that implements `IHostedService`, or one
factory attachment. Each target owns a serialized **transition chain**, so start, stop and dispose for
that one target never interleave, while transitions for unrelated targets run concurrently.

Several decisions below look like they could be simplified. Each was measured, and the obvious
simplification was measured to be wrong. The reason is recorded with the decision, because the reason
is the only thing that stops it being simplified again.

## Data Structures

`HostedServiceTarget`, one per managed thing, stored in the subject's `Data` bag:

```
Factory    Func<IHostedService>?    the factory for an attachment, null for a subject target
Subject    IHostedService?          the subject for a subject target, null for an attachment
_current   IHostedService?          the running instance, or null
_fault     Exception?               the exception from the last failed transition
_owner     HostedServiceHandler?    the handler that claimed this target
_tail      Task                     the transition chain
```

`Current`, `Fault` and `Owner` use `Volatile.Read` and `Volatile.Write`. Awaiting a transition already
gives its awaiter a happens before edge, but a diagnostics poll reads `Current` from an unrelated
thread with no such edge, so this is required rather than decorative.

`IsHandlerOwnedInstance` is simply `Factory is not null`, and it is the whole disposal policy: the
handler created the instance if and only if it invoked a factory to get it, so it disposes attachment
instances and never disposes a subject.

Attachments live under one data key as an `ImmutableArray`, mutated with `AddOrUpdate`. The record is
built outside the update delegate, because `ConcurrentDictionary` may invoke that delegate more than
once without rolling back its side effects, so a record built inside it could register a target that
loses the compare and swap and is never seen again.

`HostedServiceHandler` holds four things and nothing else:

```
_gate            HostedServiceGate                              NotStarted, Running, Draining, Drained
_running         ConcurrentDictionary<HostedServiceTarget, …>   targets this handler may have to stop
_liveSubjects    ConcurrentDictionary<IInterceptorSubject, …>   subjects in the graph for this handler
_inFlightStops   ConcurrentDictionary<Task, …>                  stops appended but not yet finished
```

Records live on subjects rather than in the handler, so nothing in the handler roots a detached
subject and a factory survives a detach. That survival is what lets a subject that leaves the graph
and re-enters it get working services again: the next context attach invokes the surviving factory, so
no restart contract is needed from the service.

The handler carries `[RunsAfter(typeof(ContextInheritanceHandler))]`, because it resolves startup
completion deferrers from `subject.Context` and for a subject entering as a child it is
`ContextInheritanceHandler` that installs the parent context as a fallback. Ahead of the descent the
child's context would still be its private executor and would resolve nothing. See
[Handler Order Around the Descent](tracking-lifecycle.md#handler-order-around-the-descent).

## Why Per Target Chains Rather Than One Queue

An earlier implementation posted every start and stop to a single `BufferBlock` drained by one
consumer loop. One queue for a whole handler couples services that have nothing to do with each other,
and the coupling was not theoretical:

- **Self deadlock across the whole handler.** The loop awaited each action to completion before taking
  the next, so any action that awaited another action, which the awaitable attach and detach paths do,
  wedged the loop for every service in the context. Per target chains do not remove that cycle, they
  contain it to one chain (see [Residual Hazards](#residual-hazards)).
- **Shutdown dropped queued work.** Shutdown cancelled the loop's token first, so anything still
  queued never ran and any caller awaiting it waited forever. A host disposed without ever having
  started returned early and left every queued action and every awaiter hanging.
- **Ordering dependence on dependency injection registration order.** The loop only began draining
  when the handler's own `StartAsync` ran, and hosted services start in registration order, so a
  hosted service registered ahead of `WithHostedServices` that awaited an attach hung host startup.
  `HostedServiceGate` and `EnsureStarted` replace that, pinned by
  `AddSubjectTests.WhenAddSubjectIsRegisteredBeforeWithHostedServices_ThenStartupDoesNotHang`.
- **Cost proportional to the number of subjects.** Every start paid the 50 ms delay in series, so N
  subjects cost N times 50 ms of host startup.

Per target serialization keeps the only ordering property that was ever needed, that one target's own
transitions never overlap, and buys the one cross target ordering genuinely required with a completion
signal rather than with a global executor.

One guarantee is deliberately given up. `LifecycleInterceptor.DetachFromProperty` invokes a parent's
handlers before recursing into children, so under one queue a parent hosted subject stopped before
hosted descendants; under per target chains they stop concurrently. Hosted services at different
depths are independent by construction and nothing here depends on the old order. If a consumer ever
needs it, the fix is another completion signal, not a global executor.

## Appending a Transition

```csharp
lock (_sync)
{
    _tail = _tail
        .ContinueWith(_ => RunAsync(body, cancellationToken), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default)
        .Unwrap();

    return _tail;
}
```

**The lock is required.** `_tail = _tail.ContinueWith(...)` is a read modify write; two racing
appenders lose an assignment and both transitions then run concurrently on the same target, which is
exactly what the chain exists to prevent. Two appenders race here in ordinary use: a lifecycle event
appends while holding `_attachedSubjects`, and a user driven detach appends from a pool thread. Pinned
by `HostedServiceTargetTests.WhenTransitionsAreAppendedConcurrently_ThenTheyNeverOverlap`.

**`TaskScheduler.Default` is required.** `ContinueWith` otherwise captures `TaskScheduler.Current`,
which can be a scheduler the appending task is itself occupying, in which case the continuation is
queued to a scheduler that never gets around to running it.

**Appending never blocks and never runs the body.** A default `ContinueWith` never executes inline on
the appending thread, so a lifecycle handler may append while the lifecycle interceptor holds
`_attachedSubjects`, and no user code ever runs under that lock. That is structural rather than a rule
somebody has to remember, and it is pinned by
`HostedServiceTargetTests.WhenATransitionIsAppended_ThenItDoesNotRunOnTheAppendingThread`.

**Bodies never throw.** `RunAsync` catches everything, so the chain is never faulted. A faulted `_tail`
is retained until the target transitions again, and every dropped fire and forget transition would
raise `UnobservedTaskException`. Bodies record failures into `Fault` and log them instead.

## Appending at Event Time

Every append happens when the lifecycle event fires, never deferred into another transition. This is
the rule that makes moving a subject through the graph work, and the obvious alternative was measured
to break it.

The requirement is that a subject's own stop completes before the attachments it uses are disposed.
`BackgroundService.StopAsync` awaits its execute task, so a subject's stop is slow, and an attachment
disposed underneath it is observed as already disposed by code still unwinding inside `ExecuteAsync`.
The tempting fix is one composite transition on the subject's chain that stops the subject and then
stops its attachments. That is wrong: on a detach immediately followed by an attach, the re-attach's
create and start lands on the attachment's own chain and runs first, and the composite's stop is then
issued against the **new** instance. The pre-detach instance is never disposed and the post-re-attach
instance is stopped and disposed, leaving the subject in the graph with nothing running and no error
anywhere.

So `DetachSubject` appends immediately, under `lock (_attachedSubjects)`, to every affected chain:

- a stop on the subject's chain if the subject is an `IHostedService`, which sets a `subjectStopped`
  completion in a `finally`, so cancellation and failure release it too;
- for each attachment, a stop on that attachment's own chain that first awaits `subjectStopped`, then
  stops, disposes and clears `Current`.

When the subject is not an `IHostedService` the completion is already set, so a plain subject with
attachments needs no chain of its own. Ordering holds because both appends happen under the lifecycle
lock, so any later re-attach queues behind them on the same chains. The wait is acyclic: an attachment
chain waits on the subject's signal and the subject's chain waits on nothing, **provided the subject's
stop does not itself wait on an attachment chain**. That proviso is
[residual hazard 3](#residual-hazards).

Pinned by `HostedServiceHandlerRaceTests.WhenAReAttachLandsWhileTheSubjectStopIsHeld_ThenAFreshInstanceRunsAndTheOldOneIsDisposed`,
which holds the subject's stop on a seam so the re-attach provably lands mid stop, and by
`WhenTheHostDrains_ThenASubjectStopsBeforeItsAttachmentIsDisposed` for the ordering itself.

`subjectStopped` means "the subject's stop returned", which equals "`ExecuteAsync` unwound" only when
the stop is not cancelled: since .NET 8, `BackgroundService.StopAsync` awaits its execute task with
`ConfigureAwaitOptions.SuppressThrowing`, so on a cancelled token it returns normally while
`ExecuteAsync` is still running. Graph driven detaches pass `CancellationToken.None` and get the
strong reading; host shutdown passes the stopping token and gets the weak one. That is documented
rather than fixed, because forcing the strong reading at shutdown would mean ignoring `ShutdownTimeout`.

Context attach is the mirror image, also under the lock: a start on the subject's chain if it is an
`IHostedService`, and a create and start on each attachment's chain.

## Ownership

A target's `Owner` is taken with `Interlocked.CompareExchange`. Finding this handler already installed
counts as success; only losing to a different handler means do nothing. The caller is told which of the
two successes it got, because a caller that has to undo its own take must leave an earlier one alone:
that earlier take belongs to an attach whose instance may already be running.

**Ownership is read on context detach, at append time.** A handler appends a stop only for the targets
it owns, so a detach reaching a drained handler, or a sibling handler that lost the exchange, cannot
stop and dispose an instance the owning handler created and is still running. The read cannot move into
the transition body, because ownership is released a few statements after the appends, so a body would
always see a stranger and skip its own stop. Reading it at append time, under the lifecycle lock, is
what pairs the read with the release. Pinned by
`HostedServiceHandlerTests.WhenADrainedHandlerSeesAContextDetach_ThenTheLiveHandlersInstancesKeepRunning`
and `WhenANonOwningHandlerSeesAContextDetach_ThenTheOwnersInstanceKeepsRunning`.

**Ownership is released on context detach and on drain, always after the stops are appended, and never
from inside a transition body.** Both halves were measured. Releasing at the end of the stop transition
lets that transition clobber ownership a re-attach has already retaken, after which the re-attach's
start finds a stranger owning the target and no-ops itself, leaving a subject in the graph with nothing
running and no error anywhere. Releasing before appending lets a second handler's start land ahead of
the first handler's stop on a shared chain. Release on detach is what lets a subject moved between
contexts be picked up by the next handler; release on drain is what lets a second host run over the
same subject instances, without which every record stays owned by a dead handler and nothing ever
starts again (pinned by
`HostedServiceHandlerTests.WhenAServiceIsAttachedAfterItsHandlerDrained_ThenTheNextHandlerStartsIt`).

**Ownership is not what makes two contexts over one subject benign.** Reading it that way was measured
wrong: a subject reachable from two hosting enabled contexts raises one context attach per context and
the **owning** handler sees both, so it appends two starts to the same chain and loses no exchange at
any point. What closes it is a one instance guard inside the start body, where the chain serializes the
two starts against each other. At append time both would still see an empty target. Pinned by
`WithHostedServicesTests.WhenOneSubjectIsReachableFromTwoHostingContexts_ThenItIsStartedOnce`.

## Liveness

**Liveness is a per subject flag, not per target ownership.** It is set on context attach and cleared
on context detach, both under `lock (_attachedSubjects)`, and a start consults it before doing
anything.

Chain order covers lifecycle driven appends, because every lifecycle event fires under the lifecycle
lock and the handler appends inside it. A user driven `AttachHostedService` appends under the target's
own lock only and is unordered against them, so a start needs a second check. Target ownership cannot
be that check, and the failure was measured: the attaching path takes ownership itself, so an attach
racing a detach passes its own check and leaves the attachment running on a detached subject. The
subject level flag covers both interleavings. If the detach's enumeration of
`GetHostedServiceAttachments` missed a record being published concurrently, that record's start finds
the subject already cleared; if the enumeration saw it, the stop is already ordered ahead of the start
on that chain. Pinned by
`HostedServiceHandlerRaceTests.WhenAnAttachmentIsAddedAfterTheSubjectDetached_ThenNothingIsStarted`.

The flag is read twice, and the first read is the one that is easy to get wrong.

**Inside the chain lock.** `TryTakeOwnershipAndAppendAsync` performs the liveness read, the ownership
take and the append under one acquisition of the target's lock. A context detach clears liveness before
it appends its stops, and appends each stop under that same lock, which leaves only two orders: this
call first, so the detach's stop lands behind a start that then finds the subject dead and no-ops; or
the detach's stop first, so this call reads cleared liveness and appends nothing. Splitting the three
steps lets a start land behind an attachment stop that is waiting for the subject's own stop, which is
waiting for the caller awaiting this start, and that cycle never resolves.

**Inside the start body.** The body re-reads liveness and ownership before creating anything, covering
a detach that lands after the append and before the body runs. Pinned by
`HostedServiceHandlerRaceTests.WhenAQueuedStartRunsAfterTheSubjectDetached_ThenNothingIsStarted`.

The same flag gives the documented behaviour that attaching to a subject outside a hosting enabled
context stores the factory and runs nothing, and it is why `WaitForStartAsync` on a draining or drained
handler answers immediately instead of queueing behind a stop.

## The Gate

`HostedServiceGate` has four states and moves forward only: `NotStarted`, `Running`, `Draining`,
`Drained`. `EnsureStarted` advances `NotStarted` to `Running` and does nothing in any other state.
Written as a plain assignment it would let a detach arriving during shutdown flip `Draining` back to
`Running` and reopen the race the fourth state exists to close.

**The gate is read inside the transition body, never at append time.** Two consequences make this load
bearing. A transition short circuited at append time would have no body, therefore no `finally`,
therefore would never set its `subjectStopped` completion, so the paired attachment stop would park on
that signal forever and wedge that chain against every later append. And a start already queued when
shutdown begins only becomes a no-op if it re-reads the state when it runs; evaluated at append time it
would still start. A gated out transition therefore still runs its signalling and its bookkeeping, and
skips only the user visible work.

| Gate state when the body runs | start | stop |
|---|---|---|
| `NotStarted` | parks until the gate leaves `NotStarted`, then re-reads | parks until the gate leaves `NotStarted` |
| `Running` | runs | runs |
| `Draining` | skips the work, releases its startup holds | runs, signals |
| `Drained` | skips the work, releases its startup holds | runs, signals |

**A stop runs at every state, `Drained` included, and that row is not a rounding error.** Shutdown
awaits the stops queued before the drain and the stops it appends itself, but a stop appended after
both snapshots, by a graph move racing the drain, is in neither and reaches `Drained` still holding a
running instance. A stop that no-oped there would leave that instance never stopped and never disposed.
Nothing is lost by letting it run, because a target the drain already stopped has a null `Current` and
the stop returns immediately. It is that null check, not the gate state, that makes a stop idempotent.
Pinned by
`HostedServiceHandlerRaceTests.WhenAStopIsStillQueuedWhenTheDrainCompletes_ThenItStillStopsAndDisposes`.

`BeginDraining` sets the opened signal even though it never opens the gate for work, which releases
anything parked on a gate that was never opened. Without it, a host that aborts startup or is disposed
without starting leaves transitions and their awaiters hanging forever. Pinned by
`HostedServiceGateTests.WhenDrainingStartsFromNotStarted_ThenParkedWaitersAreReleased`.

Who may open the gate is a real decision, because "nothing runs before host start" and "a caller that
started before the handler must not hang" pull in opposite directions. `SubjectActivation<T>` and the
awaitable attach and detach overloads call `EnsureStarted`, since awaiting is an explicit request for
the service to be running. The synchronous overloads and every lifecycle driven append only wait for
the gate, which preserves the invariant for `new Car(context)` at configuration time.

## Startup Completion Holds

A start is queued rather than run inline, so the service is not running when the attach returns.
Anything treating "the graph has finished starting" as a completion point would otherwise pass that
point while a queued start is still on its way in. A subsystem declares that it cares by implementing
`IStartupCompletionDeferrer` on the context; `SourceMonitor` is the only implementation here.

The hold is taken in `TryTakeOwnershipAndStart`, synchronously and **before** the append, so there is
no window between the attach and the hold in which completion can fire. It is released in the start
body's `finally`, which is what covers every way out of the body: gated out by a drain, not live,
skipped by the one instance guard, or a start that threw. When the append is refused there is no body,
so that path releases the holds itself. A leaked hold blocks every synchronization wait on that tree
forever, which is a hang rather than a wrong answer and worse than never having taken the hold.

Holds are counted, so nested attaches compose: a service that attaches children during its own
`StartAsync` takes their holds before its own is released. A deferrer that throws while taking or
releasing is logged and ignored, because the take runs under the lifecycle lock inside a property write
and an exception would surface at an unrelated assignment.

## Faults and Failed Starts

`Fault` holds the exception from the last failed transition on that target. A start clears it **after
its guards, not on entry**: a start that is gated out or skipped must not drop a fault a caller has not
read yet (pinned by
`HostedServiceHandlerRaceTests.WhenAQueuedStartIsSkippedByTheDrain_ThenAnEarlierFaultSurvives`).
Clearing it at all matters because a graph driven start that faulted is deliberately kept so the next
context attach can retry; without the clear, the next successful attach would throw a stale exception
through `AttachHostedServiceAsync` or `WaitForStartAsync`.

A start that faults after creating the instance disposes it when the handler created it, and leaves
`Current` null. Leaving a half started connector undisposed is the ownership gap by another route: a
connector can hold a semaphore, a session manager and a lifecycle subscription that only its own
dispose releases.

A cancelled stop is caught and **not** recorded as a fault, because it is the caller's token expiring
rather than a failure, and the dispose after it still has to run. `Current` is already cleared by then,
so an instance that escaped there would be unreachable and never disposed, which is the ordinary
`ShutdownTimeout` path.

## Shutdown

`StopAsync` is a barrier built from the pieces above rather than around them:

1. `BeginDraining`, which stops new targets being taken and releases parked waiters.
2. Snapshot `_inFlightStops` immediately, so it holds exactly the stops queued before the drain.
3. Clear `_liveSubjects`. A handler still reporting a subject as live would claim ownership of every
   attachment added afterwards, append a start that no-ops, and never release it, because the release
   loop in step 6 only covers the drain's own snapshot.
4. Snapshot `_running` and append stops in the same per subject shape a context detach uses: a stop
   carrying a `subjectStopped` signal for every subject target, then a stop for every attachment target
   that awaits its own subject's signal when that subject was in the snapshot.
5. Await those plus the stops from step 2, bounded by the host's stopping token.
6. Release ownership of every target in the snapshot, clear `_running`, `CompleteDraining`.

**A target joins `_running` when its start is taken and leaves when its stop is appended**, not when
either completes. Joining at completion would let the drain's snapshot miss a queued start, which is
the same race the `Draining` state closes and should not depend on two mechanisms agreeing. Joining
happens after the ownership take rather than before, because an entry for a target this handler failed
to take would make the drain stop and dispose an instance another handler created.

Leaving at append time is why step 2 exists: a stop appended just before the drain is in no `_running`
snapshot, so without it the drain returns while that stop is still running, and the host disposes the
service provider the moment `StopAsync` returns. Each entry removes itself through a continuation, so
the set does not grow with the process. Pinned by
`HostedServiceHandlerRaceTests.WhenAStopIsInFlightWhenTheHostDrains_ThenTheDrainWaitsForIt`.

A cancelled wait in step 5 is swallowed rather than propagated, and the drain still finishes.
Rethrowing would abandon step 6, so every target this handler owns would stay owned by a dead handler
and a second host over the same subjects would start nothing. The host treats an exception here as a
failed shutdown and disposes the provider anyway, so there is nothing to gain and the whole cleanup to
lose. Nothing below observes the token: the chain waits inside a stop body are untokened by design, and
a stop wedged behind one of them would otherwise hold the process open forever.

`TryTakeOwnershipAndStart` reads the gate twice, once on entry and once after writing both `_running`
and the owner. The second read is what turns the first from a narrowing into a guard: reading `Running`
after both writes proves the drain had not begun when they landed, so the drain's own snapshot covers
this target. Reading anything later means the drain may already have swept past, so the take is undone
there rather than left to a release loop that will never see it. Only an ownership this call installed
is undone. `AttachSubject` re-reads the gate after writing `_liveSubjects` for the same reason. Pinned
by
`HostedServiceHandlerRaceTests.WhenAnAttachmentIsAddedDuringTheDrain_ThenTheDrainingHandlerTakesNoOwnership`.

## Activation and Waiting for a Start

`SubjectActivation<T>` exists because a singleton nobody resolves is never constructed, never attached
to its context and never started, and `IHostedService` is the only hook the generic host offers for
forcing that construction. Resolving the subject attaches it, which makes the handler append the start.

`WaitForStartAsync` appends an empty transition to the same chain and awaits it. Appending never runs a
body, so that transition completes only once the start ahead of it has run. It then rethrows the
recorded fault, which preserves the `AddHostedService` guarantee that a failing subject aborts host
startup and that `ApplicationStarted` implies the subject is running.

It reads the target and never creates one, and never takes ownership: a claim taken there would never
be released, because a drained handler releases only what its own drain snapshotted, so the next
handler over the same subject would lose the compare and exchange forever. It returns false when it
started nothing, which is not licence for the caller to start the subject itself: false means either
another handler owns the target, where a start would be a second instance, or that this handler is
draining, where a start would be something nothing stops. The activation records what it started
itself instead, so its `StopAsync` cannot hand a stop to a handler that never started the subject.

## The 50 ms Delay

Both the start and the stop body delay 50 ms before touching the instance. It covers a caller side
hazard: the generated context constructor attaches the subject last, so `new Car(context) { Name = "x" }`,
deserialization, and `AddSubject`'s `configure` on the context constructor path all assign after the
attach has fired and after the start has been appended.

The delay is a mitigation, not a synchronization, and removing it is a separate problem: it needs a
"subject fully constructed" signal, which touches the generator. It lives inside each target's
transition rather than in a shared loop, so it no longer serializes across targets and N subjects cost
50 ms rather than N times 50 ms. The old loop's staggering across services was a side effect of
serialization rather than a guarantee, so nothing is lost by the move: what protects against the hazard
is the gap between a target's own attach event and its own start, and that is 50 ms either way. Both
delays pass `CancellationToken.None`, so shutdown waits each one out per target.

## Residual Hazards

Per target chains contain the self deadlock that one shared queue spread across every service. They do
not remove cycles, and none of the three shapes is detected:

1. An attachment whose own `StopAsync` detaches itself. The detach appends to the chain the running
   stop occupies.
2. Two subjects whose services detach each other's attachments from their own stop paths.
3. A subject that detaches its own attachment while unwinding inside its own stop. The subject's stop
   transition waits on the unwind, the unwind waits on the attachment chain, and the attachment chain's
   head waits on `subjectStopped`, which only the blocked subject transition can set.

Shape 3 is the one that occurs in practice, because it is what a `BackgroundService` reaches when
`ExecuteAsync` unwinds into a stop helper that detaches. Both HomeBlaze OPC UA wrappers had exactly
this shape and were changed so the unwind resets status and nothing else. The rule is stated in
[the user documentation](../hosting.md#do-not-detach-from-your-own-stop-path) rather than guarded, and
`HostedServiceHandlerTests.WhenASubjectOwningAnAttachmentIsStoppedByTheHost_ThenShutdownCompletesWellInsideTheTimeout`
is the regression guard. A wedged chain is unbounded in damage but bounded in blast radius: shutdown
gives up on it at `ShutdownTimeout` and every other chain drains normally.

Moving disposal from the caller to the handler puts a constraint on connectors that nothing enforces
and no test covers. The handler disposes from a transition that can run while a detach cascade still
holds `_attachedSubjects`, where previously the caller disposed and the two never interleaved. The
concrete collision is `SourceOwnershipManager`, which takes its own lock in `Dispose` and then invokes
`onReleasing`, while its `SubjectDetaching` handler runs from inside `_attachedSubjects` and invokes
the same callback before taking that lock. The constraint that follows is stated in
[the user documentation](../hosting.md#keep-the-dispose-path-out-of-the-lifecycle-lock). Note that
`LifecycleInterceptor.WriteProperty` takes the lock only when the property type can contain subjects,
which is why writing a scalar from a dispose path is harmless and writing a subject typed or collection
typed property is not, and why attaching or detaching a subject enters the same lock without being a
property write at all.

## Invariants

Once lifecycle events and user driven attach and detach calls have settled:

1. **One instance per target.** A target holds at most one non null `Current`, and only a start body
   ever sets it.
2. **In the graph implies running, out of the graph implies stopped.** Each chain drains in append
   order, so every target ends in the state its last event demanded. Execution across targets is
   concurrent, so this is quiescent consistency rather than a moment by moment guarantee.
3. **Created implies disposed by the same owner.** The handler disposes exactly the instances it
   created through a factory, once, and never disposes a subject.
4. **A subject's stop precedes the disposal of its own attachments**, on both the context detach path
   and the shutdown path, whenever that stop is not cancelled.
5. **No user code runs under `_attachedSubjects`.** Every lifecycle driven action is an append, and an
   append never runs a body.
6. **A drained handler roots nothing.** It releases every target it owned, clears its running and
   liveness sets, and leaves the records on the subjects for the next handler.
