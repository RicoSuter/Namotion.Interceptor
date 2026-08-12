# Hosted Service Ownership: Internal Design

This document describes the internal concurrency model of `Namotion.Interceptor.Hosting`: how `HostedServiceHandler`, `HostedServiceTarget` and `HostedServiceGate` decide when a subject bound hosted service starts, stops and is disposed. For user-facing documentation, see the [Hosting](../hosting.md) documentation.

## Overview

Everything below exists to keep [the rule](../hosting.md#the-rule) true under concurrency: lifecycle events fire from inside `LifecycleInterceptor`'s `_attachedSubjects` lock, callers attach and detach from arbitrary threads, and the host starts and drains underneath both.

The unit of management is a **target**: either a subject that implements `IHostedService`, or one factory attachment. Each target owns a serialized **transition chain**, so start, stop and dispose for that one target never interleave, while transitions for unrelated targets run concurrently.

Several decisions below look like they could be simplified. Each was measured, and the obvious simplification was measured to be wrong. The reason is recorded with the decision, because the reason is the only thing that stops it being simplified again. Where the code already carries the whole argument next to the mechanism, this document names the mechanism and points at it rather than repeating it.

## Data Structures

### `HostedServiceTarget`

One per managed thing, stored in the subject's `Data` bag:

```
Factory               Func<IHostedService>?    the factory for an attachment, null for a subject target
Subject               IHostedService?          the subject for a subject target, null for an attachment
_current              IHostedService?          the running instance, or null
_fault                Exception?               the exception from the last failed transition
_owner                HostedServiceHandler?    the handler that claimed this target
_lastFactoryInstance  IHostedService?          the instance the previous factory call returned, kept for the life of the attachment
_detached             bool                     set by an explicit detach, refuses every later start
_tail                 Task                     the transition chain
_sync                 object                   guards _tail and _detached
TransitionGate        Func<Task>?              test seam awaited at the top of every body, null in production
ChainLockGate         Action?                  test seam invoked inside _sync between the take and the append, null in production
```

The fields are synchronized differently, and each difference is deliberate:

- `Current` and `Fault` use `Volatile.Read` and `Volatile.Write`. Awaiting a transition already gives its awaiter a happens before edge, but a diagnostics poll reads `Current` from an unrelated thread with no such edge, so this is required rather than decorative.
- `Owner` uses `Volatile.Read`, and it is never written with `Volatile.Write`. The only two writers are `TryTakeOwnership` and `ReleaseOwnership`, and both go through `Interlocked.CompareExchange`, which carries the fence itself. A plain write would lose the compare and swap that decides which of two racing handlers claims the target.
- `_lastFactoryInstance` is neither volatile nor locked, and it is never cleared. Both are deliberate, and both reasons are on `HostedServiceTarget.TryRecordFactoryInstance`.
- `_detached` is written and read under `_sync`, which is what pairs it with the append (see [Refusing a start for an attachment a detach already removed](#refusing-a-start-for-an-attachment-a-detach-already-removed)).

`IsHandlerOwnedInstance` is simply `Factory is not null`, and it is the whole disposal policy: the handler created the instance if and only if it invoked a factory to get it, so it disposes attachment instances and never disposes a subject.

### `HostedServiceHandler`

```
_loggerResolver  Func<ILogger?>          resolves the logger on first use
_logger          ILogger?                the resolved logger, cached
_gate            HostedServiceGate       NotStarted, Running, Draining, Drained
_running         ConcurrentDictionary    target -> subject, for targets this handler may have to stop
_liveSubjects    ConcurrentDictionary    subject -> unused, for subjects in the graph for this handler
_inFlightStops   ConcurrentDictionary    stop task -> unused, for stops appended but not yet finished
DrainGate        Func<Task>?             test seam, null in production
OwnershipTakenGate Action?               test seam, null in production
```

The three concurrent dictionaries are the state the rest of this document is about. `_running` maps a target to its subject rather than being a set, because the drain has to group the targets it stops per subject to reproduce the ordering a context detach gives. The two seams are documented where they are declared; each one holds open a window between two statements that are adjacent in production.

`_liveSubjects` holds an entry for **every** subject that attaches, not only the subjects that host something, and that is what it has to be. An attachment can be added to a subject at any time after it entered the graph, and this entry is the only thing that lets that later attach tell a live subject from a detached one, so recording only the subjects hosting something at attach time would refuse every attachment onto every other subject. The cost is one concurrent dictionary entry per subject in the graph, tens of bytes each, plus one write on every attach and one removal on every detach: retained memory linear in the size of the graph rather than in the number of hosted services, and a graph with no hosted services anywhere pays all of it. Bounded by the graph and paid for correctness on the attach path, which is why it stays.

The logger is resolved through a callback rather than injected because `WithHostedServices` constructs the handler while the context is being configured, which is before any service provider exists; the registration it adds to the `IServiceCollection` assigns the logger when the provider builds it.

### Where records live

Records live on subjects rather than in the handler, so nothing in the handler roots a detached subject and a factory survives a detach. That survival is what lets a subject that leaves the graph and re-enters it get working services again: the next context attach invokes the surviving factory, so no restart contract is needed from the service.

Attachments live under one data key as an `ImmutableArray`, and the subject target under another. The correctness and allocation constraints on both paths are on `InterceptorHostingExtensions.AddAttachment` and `InterceptorHostingExtensions.GetOrAddSubjectTarget`; the allocation half is pinned by `HostedServiceHandlerTests.WhenASubjectTargetAlreadyExists_ThenReadingItAllocatesNothing`.

The handler carries `[RunsAfter(typeof(ContextInheritanceHandler))]`, because it resolves startup completion deferrers from `subject.Context` and for a subject entering as a child it is `ContextInheritanceHandler` that installs the parent context as a fallback. Ahead of the descent the child's context would still be its private executor and would resolve nothing. See [Handler Order Around the Descent](tracking-lifecycle.md#handler-order-around-the-descent).

## Why Per Target Chains Rather Than One Queue

An earlier implementation posted every start and stop to a single `BufferBlock` drained by one consumer loop. One queue for a whole handler couples services that have nothing to do with each other, and the coupling was not theoretical:

- **Self deadlock across the whole handler.** The loop awaited each action to completion before taking the next, so any action that awaited another action, which the awaitable attach and detach paths do, wedged the loop for every service in the context. Per target chains do not remove that cycle, they contain it to one chain (see [Residual Hazards](#residual-hazards)).
- **Shutdown dropped queued work.** Shutdown cancelled the loop's token first, so anything still queued never ran and any caller awaiting it waited forever. A host disposed without ever having started returned early and left every queued action and every awaiter hanging.
- **Ordering dependence on dependency injection registration order.** The loop only began draining when the handler's own `StartAsync` ran, and hosted services start in registration order, so a hosted service registered ahead of `WithHostedServices` that awaited an attach hung host startup. `HostedServiceGate` and `EnsureStarted` replace that, pinned by `AddSubjectTests.WhenAddSubjectIsRegisteredBeforeWithHostedServices_ThenStartupDoesNotHang`.
- **Cost proportional to the number of subjects.** Every start paid the 50 ms delay in series, so N subjects cost N times 50 ms of host startup.

Per target serialization keeps the only ordering property that was ever needed, that one target's own transitions never overlap, and buys the one cross target ordering genuinely required with a completion signal rather than with a global executor.

One guarantee is deliberately given up. `LifecycleInterceptor.DetachFromProperty` invokes a parent's handlers before recursing into children, so under one queue a parent hosted subject stopped before hosted descendants; under per target chains they stop concurrently. Hosted services at different depths are independent by construction and nothing here depends on the old order. If a consumer ever needs it, the fix is another completion signal, not a global executor.

## Appending a Transition

Both append paths, `AppendAsync` and `TryTakeOwnershipAndAppendAsync`, end in `HostedServiceTarget.AppendCore`, under the target's `_sync` lock, where the comment records why the lock and `TaskScheduler.Default` are each required. Two appenders race there in ordinary use: a lifecycle event appends while holding `_attachedSubjects`, and a user driven detach appends from a pool thread. Pinned by `HostedServiceTargetTests.WhenTransitionsAreAppendedConcurrently_ThenTheyNeverOverlap`.

`RunAsync` catches everything, so the chain is never faulted. Bodies record failures into `Fault` and log them instead.

### Appending never blocks and never runs the body

A default `ContinueWith` never executes inline on the appending thread, so a lifecycle handler may append while the lifecycle interceptor holds `_attachedSubjects`, and no transition **body** ever runs under that lock. That is structural rather than a rule somebody has to remember, and it is pinned by `HostedServiceTargetTests.WhenATransitionIsAppended_ThenItDoesNotRunOnTheAppendingThread`.

Third party code does run under that lock, though, and it is not the body: taking and releasing the startup completion holds calls into `IStartupCompletionDeferrer` synchronously from the lifecycle event. That is [residual hazard 4](#4-a-deferrer-that-takes-a-lock-of-its-own).

## Appending at Event Time

Every append happens when the lifecycle event fires, never deferred into another transition. This is the rule that makes moving a subject through the graph work, and the obvious alternative was measured to break it.

### Why a composite transition is wrong

The requirement is that a subject's own stop completes before the attachments it uses are disposed. `BackgroundService.StopAsync` awaits its execute task, so a subject's stop is slow, and an attachment disposed underneath it is observed as already disposed by code still unwinding inside `ExecuteAsync`. The tempting fix is one composite transition on the subject's chain that stops the subject and then stops its attachments. A detach immediately followed by an attach shows why it is wrong:

1. The subject leaves the graph. The composite transition is appended to the **subject's** chain. It has not run yet.
2. The subject re-enters the graph. The create and start for the attachment is appended to the **attachment's** chain, which is empty, so it runs. Instance B is now running.
3. The composite transition finally runs. It stops the subject, then reads `Current` on the attachment and finds instance B, which it stops and disposes.

Instance A, the one that was running before the detach, is never disposed. Instance B, the one the graph expects to be running, is stopped and disposed. The subject is in the graph with nothing running and no error anywhere.

### What a context detach appends

So `DetachSubject` appends immediately, under `lock (_attachedSubjects)`, to every affected chain:

- a stop on the subject's chain if the subject is an `IHostedService` this handler owns, which sets a `subjectStopped` completion in a `finally`, so cancellation and failure release it too;
- for each attachment this handler owns, a stop on that attachment's own chain that first awaits `subjectStopped`, then stops, disposes and clears `Current`.

`subjectStopped` is allocated only when the handler is appending the subject's own stop **and** the subject has at least one attachment to order behind it; the reasons are at that allocation and at the null wait below it. It returns before allocating anything at all for a subject that hosts neither, which is essentially every subject in a detaching graph. Pinned by `HostedServiceHandlerTests.WhenASubjectWithoutHostedServicesIsDetached_ThenNothingIsAllocated`.

Ordering holds because both appends happen under the lifecycle lock, so any later re-attach queues behind them on the same chains. The wait is acyclic: an attachment chain waits on the subject's signal and the subject's chain waits on nothing, **provided the subject's stop does not itself wait on an attachment chain**. That proviso is [residual hazard 3](#3-a-subject-that-detaches-its-own-attachment-while-unwinding).

Pinned by `HostedServiceHandlerRaceTests.WhenAReAttachLandsWhileTheSubjectStopIsHeld_ThenAFreshInstanceRunsAndTheOldOneIsDisposed`, which holds the subject's stop on a seam so the re-attach provably lands mid stop, and by `HostedServiceHandlerRaceTests.WhenASubjectLeavesTheGraph_ThenItStopsBeforeItsAttachmentIsDisposed` for the ordering itself. Shutdown builds the same shape from its own code in `StopAsync` rather than calling this path, so it needs its own test and has one, `HostedServiceHandlerRaceTests.WhenTheHostDrains_ThenASubjectStopsBeforeItsAttachmentIsDisposed`. Dropping the wait from one path fails that path's test and leaves the other green.

### What `subjectStopped` actually means

It means "the subject's stop returned", which equals "`ExecuteAsync` unwound" only when the stop is not cancelled: since .NET 8, `BackgroundService.StopAsync` awaits its execute task with `ConfigureAwaitOptions.SuppressThrowing`, so on a cancelled token it returns normally while `ExecuteAsync` is still running. Graph driven detaches pass `CancellationToken.None` and get the strong reading; host shutdown passes the stopping token and gets the weak one. That is documented rather than fixed, because forcing the strong reading at shutdown would mean ignoring `ShutdownTimeout`.

Context attach is the mirror image, also under the lock: a start on the subject's chain if it is an `IHostedService`, and a create and start on each attachment's chain.

### A subject created from inside a lifecycle handler

Giving a container a default child from the container's own context attach is a legitimate pattern, and it is the one place where a subject enters the graph while another subject's attach event is still being dispatched. `ChildCreatingLifecycleHandler` in the test project is the shape, and the remarks on `NestedAttachTests` record why the child's attach is an ordinary one rather than a re-entrant call. Both handler orders reach the same state, pinned by `NestedAttachTests.WhenAnAttachHandlerCreatesTheContainersChild_ThenBothStartOnceAndEachOwnsItsOwnTarget`.

The way back out is the same shape. The child is reached by the detach cascade through the container's property rather than by anything an explicit `AttachHostedService` left behind, and both ownerships are released, which is what lets a re-attach start the same two subjects again rather than finding targets no handler can claim. Pinned by `NestedAttachTests.WhenAContainerWhoseChildAnAttachHandlerCreatedLeavesTheGraph_ThenBothStopAndTheGraphCanRunThemAgain`.

The one caller that really does re-enter `AttachSubject` is an `IStartupCompletionDeferrer`, because `TakeStartupHolds` calls it synchronously from inside `TryTakeOwnershipAndStart`, which is inside the outer `AttachSubject`. A deferrer that assigns a subject typed property therefore runs the whole inner attach before the outer call has taken its own target. Nothing is shared between the two: liveness is per subject, ownership is per target, and the counted holds let the inner attach take and release its own while the outer one is still outstanding. Pinned by `NestedAttachTests.WhenADeferrerCreatesTheChildWhileTheContainersOwnAttachIsStillRunning_ThenBothStartOnceAndEveryHoldIsReleased`, which reads the container's owner from inside the deferrer to prove the inner attach really did run first. This is [residual hazard 4](#4-a-deferrer-that-takes-a-lock-of-its-own) territory rather than a recommendation: what it costs a deferrer is the constraint stated there, not re-entrancy.

## Ownership

A target's `Owner` is taken with `Interlocked.CompareExchange`. Finding this handler already installed counts as success; only losing to a different handler means do nothing. `HostedServiceTarget.TryTakeOwnership` reports which of the two successes the caller got, because a caller that has to undo its own take must leave an earlier one alone.

### Ownership is read on context detach, at append time

`DetachSubject` appends a stop only for the targets it owns, and the comment at that read records both what a handler stopping a stranger's instance costs and why the read cannot move into the transition body. Reading it at append time, under the lifecycle lock, is what pairs it with the release a few statements below. Pinned by `HostedServiceHandlerTests.WhenADrainedHandlerSeesAContextDetach_ThenTheLiveHandlersInstancesKeepRunning` and `HostedServiceHandlerTests.WhenANonOwningHandlerSeesAContextDetach_ThenTheOwnersInstanceKeepsRunning`; deleting both guards fails exactly those two.

### Ownership is released on context detach and on drain

Always after the stops are appended, and never from inside a transition body. Both halves were measured. Releasing from the body clobbers an ownership a re-attach has already retaken, and the consequence is a subject in the graph with nothing running and no error anywhere. Releasing before appending lets a second handler's start land ahead of the first handler's stop on a shared chain.

Release on detach is what lets a subject moved between contexts be picked up by the next handler. Release on drain is what lets a second host run over the same subject instances: without it every target the drained handler owned stays owned by it, and no later handler can ever win the compare and exchange for that target. Deleting the release loop in `StopAsync` fails `HostedServiceHandlerTests.WhenADrainedHandlerSeesAContextDetach_ThenTheLiveHandlersInstancesKeepRunning` and `HostedServiceHandlerTests.WhenADrainedHandlerIsAskedToWaitForAStart_ThenItClaimsNothingAndReportsNothingStarted`.

### Ownership is not what makes two contexts over one subject benign

Reading it that way was measured wrong: a subject reachable from two hosting enabled contexts raises one context attach per context and the **owning** handler sees both, so it appends two starts to the same chain and loses no exchange at any point. What closes it is a one instance guard inside the start body, where the chain serializes the two starts against each other. At append time both would still see an empty target. Pinned by `WithHostedServicesTests.WhenOneSubjectIsReachableFromTwoHostingContexts_ThenItIsStartedOnce`.

## Liveness

**Liveness is a per subject flag, not per target ownership.** It is set on context attach and cleared on context detach, both under `lock (_attachedSubjects)`, and a start consults it before doing anything.

Chain order covers lifecycle driven appends, because every lifecycle event fires under the lifecycle lock and the handler appends inside it. A user driven `AttachHostedService` appends under the target's own lock only and is unordered against them, so a start needs a second check. Target ownership cannot be that check, and the failure was measured: the attaching path takes ownership itself, so an attach racing a detach passes its own check and leaves the attachment running on a detached subject.

The flag is read on both of the paths a start passes through, and each read is discriminated separately. Deleting the read inside the chain lock fails `HostedServiceHandlerRaceTests.WhenASubjectLeavesTheGraphBeforeItsAttachTakesTheTarget_ThenTheNextHandlerStillClaimsIt` and nothing else. Inside the start body the flag is still masked by the ownership read beside it: deleting the flag read alone leaves every test green, and deleting the whole body guard fails three.

### The read inside the chain lock

`TryTakeOwnershipAndAppendAsync` performs the liveness read, the ownership take and the append under one acquisition of the target's lock, and its remarks record what splitting them opens. The damage a test can read is the explicit detach order, where the stop runs first, finds nothing to stop, and leaves the start behind it to create an instance the detach has already made unreachable.

That order is what `HostedServiceHandlerRaceTests.WhenADetachRacesTheAppendInsideTheChainLock_ThenTheStartIsOrderedAheadOfTheStop` reads. It holds the section open on `ChainLockGate` while a real `DetachHostedService` runs on its own thread, and releases the seam only once that thread has provably blocked on the chain lock or run to completion, so both builds decide the same way every time rather than by whichever thread wakes first. Splitting the section fails that test and no other; deleting the liveness read or the detached read inside it leaves it green.

### The read inside the start body

The body re-reads liveness **and** ownership before creating anything, covering a detach that lands after the append and before the body runs. The pair is pinned by `HostedServiceHandlerRaceTests.WhenAQueuedStartRunsAfterTheSubjectDetached_ThenNothingIsStarted`, which fails when both reads are deleted.

Neither read alone is discriminated by the suite, and that is a coverage limit rather than redundancy. The two cover different windows, which the comment on `RunStartAsync` sets out. Forcing a body into the window between the liveness clear and the ownership release would mean holding the lifecycle lock open, which blocks every graph write a test needs to make progress, so no seam can drive it.

The consequence if the liveness read were removed is bounded rather than a leak: the same detach has already appended a stop for that target, chains are first in first out, so the instance the start creates is stopped and disposed by that stop. The cost is a needless create and teardown against a subject that has left the graph, which for a connector means a session opened and closed. Removing the window instead, by releasing ownership before appending the stops, reopens a defect that was measured, so the guard stays and the limit is recorded here.

The subject level flag is what makes an attach onto an already detached subject fail closed: `HostedServiceHandlerRaceTests.WhenAnAttachmentIsAddedAfterTheSubjectDetached_ThenNothingIsStarted` fails when the flag is not consulted on either path.

The documented behaviour that attaching to a subject outside a hosting enabled graph stores the factory and runs nothing has two halves, and the flag is one of them. A subject whose context resolves no handler at all never reaches this code. A subject whose context still resolves the handler but which has left the graph reaches it and is refused here. The flag is also why `WaitForStartAsync` on a handler whose drain has cleared liveness answers immediately instead of queueing behind that drain's stop.

### Refusing a start for an attachment a detach already removed

An explicit `DetachHostedService` clears no liveness, so the flag cannot see it. Both detach overloads therefore call `MarkDetached()` on the target, under the chain lock, after `RemoveAttachment` succeeds and **before** they append their stop. `TryTakeOwnershipAndAppendAsync` reads that mark inside the same lock acquisition that reads liveness. That leaves two orders and no third:

1. The attach wins the lock. The start is appended, the detach's stop lands behind it on that chain, and the stop stops and disposes whatever the start created.
2. The detach's stop wins the lock. The mark is already visible, so nothing is appended, no ownership is taken and no running set entry is made.

Without the mark that start runs after the attachment has already been removed, and the instance it creates is reachable from nothing; the remarks on `TryTakeOwnershipAndAppendAsync` carry that argument.

The two marks are independent, so each needs a test that drives its own overload: deleting the mark from one of them leaves every test that reaches the window through the other green. Pinned by `HostedServiceHandlerRaceTests.WhenAnAttachmentIsDetachedBeforeItsStartIsAppended_ThenNothingIsStarted` and `HostedServiceHandlerRaceTests.WhenAnAwaitedAttachmentIsDetachedBeforeItsStartIsAppended_ThenNothingIsStarted`, which both detach through the synchronous overload, the second against the awaiting attach; and by `HostedServiceHandlerRaceTests.WhenAnAttachmentIsDetachedByTheAwaitingOverloadBeforeItsStartIsAppended_ThenNothingIsStarted` for the awaiting detach.

## The Gate

`HostedServiceGate` has four states and moves forward only: `NotStarted`, `Running`, `Draining`, `Drained`. `EnsureStarted` advances `NotStarted` to `Running` and does nothing in any other state. Written as a plain assignment it would let a detach arriving during shutdown flip `Draining` back to `Running` and reopen the race the fourth state exists to close.

### Where the gate is read

The gate state is read at append time as well as inside transition bodies, and the two reads answer different questions. The property that matters is narrower than "never at append time":

**Stops are never refused at append time, and a start's gating decision is re-read in the body.**

`AppendStop` reads no gate state at all. A stop short circuited at append time would have no body, therefore no `finally`, therefore would never set its `subjectStopped` completion, so the paired attachment stop would park on that signal forever and wedge that chain against every later append.

Starts are refused at append time, by `AttachSubject` and by `TryTakeOwnershipAndStart`, and those refusals are about bookkeeping rather than about work: a draining or drained handler must not install itself as owner of a target it can never start, nor record a subject as live, because a target left owned by a dead handler makes every future handler lose the compare and exchange. The decision about whether the start's **work** runs is taken again in the body, because a start already queued when shutdown begins only becomes a no-op if it re-reads the state when it runs.

A gated out transition therefore still runs its signalling and its bookkeeping, and skips only the user visible work.

| Gate state when the body runs | start | stop |
|---|---|---|
| `NotStarted` | parks until the gate leaves `NotStarted`, then re-reads | parks until the gate leaves `NotStarted` |
| `Running` | runs | runs |
| `Draining` | skips the work, releases its startup holds | runs, signals |
| `Drained` | skips the work, releases its startup holds | runs, signals |

### Why a stop runs at every state, `Drained` included

That row is not a rounding error. Shutdown awaits the stops queued before the drain and the stops it appends itself, but a stop appended after both snapshots, by a graph move racing the drain, is in neither and reaches `Drained` still holding a running instance. A stop that no-oped there would leave that instance never stopped and never disposed, and nothing is lost by letting it run, because the null `Current` check, not the gate state, is what makes a stop idempotent. Pinned by `HostedServiceHandlerRaceTests.WhenAStopIsStillQueuedWhenTheDrainCompletes_ThenItStillStopsAndDisposes`.

`BeginDraining` sets the opened signal even though it never opens the gate for work, which releases anything parked on a gate that was never opened. Without it, a host that aborts startup or is disposed without starting leaves transitions and their awaiters hanging forever. Pinned by `HostedServiceGateTests.WhenDrainingStartsFromNotStarted_ThenParkedWaitersAreReleased`, which fails when the signal is removed from `BeginDraining`.

### Who may open the gate

This is a real decision, because "nothing runs before host start" and "a caller that started before the handler must not hang" pull in opposite directions. `SubjectActivation<T>` and the awaitable attach and detach overloads call `EnsureStarted`, since awaiting is an explicit request for the service to be running. The synchronous overloads and every lifecycle driven append only wait for the gate, which preserves the invariant for `new Car(context)` at configuration time. Pinned by `HostedServiceHandlerTests.WhenAnAwaitedAttachRunsOnAHostThatWasNeverStarted_ThenItStillReturns` and `HostedServiceHandlerTests.WhenAnAwaitedDetachRunsOnAHostThatWasNeverStarted_ThenItStillReturns`.

## Startup Completion Holds

The user facing contract is in [Deferred Starts and Startup Completion](../hosting.md#deferred-starts-and-startup-completion), and the constraint an implementer meets is on [`IStartupCompletionDeferrer`](../../src/Namotion.Interceptor.Tracking/IStartupCompletionDeferrer.cs). What matters here is where the hold is taken and released.

The hold is taken in `TryTakeOwnershipAndStart`, synchronously and **before** the append, so there is no window between the attach and the hold in which completion can fire. Pinned by `HostedServiceHandlerRaceTests.WhenASubjectEntersTheGraph_ThenItsStartupHoldIsTakenBeforeTheGraphWriteReturns`, which asserts the hold is outstanding by the time `parent.Child = child` has returned.

It is released in the start body's `finally`, which is what covers every way out of the body. One test per path the `finally` protects:

- gated out by a drain: `HostedServiceHandlerRaceTests.WhenAQueuedStartIsSkippedByTheDrain_ThenItsStartupHoldIsReleased`;
- the subject is no longer live: `HostedServiceHandlerRaceTests.WhenAQueuedStartFindsItsSubjectDetached_ThenItsStartupHoldIsReleased`;
- skipped by the one instance guard: `HostedServiceHandlerRaceTests.WhenAQueuedStartIsSkippedByTheOneInstanceGuard_ThenItsStartupHoldIsReleased`.

When the append is refused there is no body, so that path releases the holds itself. A leaked hold blocks every synchronization wait on that tree forever, which is a hang rather than a wrong answer and worse than never having taken the hold.

A deferrer that throws is logged and ignored on both paths, for the reason at the `catch` in `TakeStartupHolds` when taking and at the one in `ReleaseStartupHolds` when releasing, where the reason is that one deferrer must not strand the others. A deferrer that blocks is a different matter, and it is a constraint on the implementation rather than an exposure every consumer carries: see [residual hazard 4](#4-a-deferrer-that-takes-a-lock-of-its-own).

## Faults and Failed Starts

`Fault` holds the exception from the last failed transition on that target. Only a start body ever clears it, and it clears it **after its guards, not on entry**: a start that is gated out or skipped must not drop a fault a caller has not read yet (pinned by `HostedServiceHandlerRaceTests.WhenAQueuedStartIsSkippedByTheDrain_ThenAnEarlierFaultSurvives`). Clearing it at all matters because a graph driven start that faulted is deliberately kept so the next context attach can retry; without the clear, the next successful attach would throw a stale exception through `AttachHostedServiceAsync` or `WaitForStartAsync`. Pinned by `HostedServiceHandlerTests.WhenATransitionFaultedEarlier_ThenTheNextSuccessfulOneClearsTheFault`.

A stop records a fault when it fails and never clears one, so a start that faulted followed by a clean stop ends with `Fault` set and nothing running. That is the reading the two members are meant to give together: `Current` null with `Fault` set is "this should be running and is not".

A start that faults after creating the instance disposes it when the handler created it, and leaves `Current` null. Leaving a half started connector undisposed is the ownership gap by another route: a connector can hold a semaphore, a session manager and a lifecycle subscription that only its own dispose releases.

A start whose factory returns the instance it returned last time is refused before `StartAsync` and before `SetCurrent`, with an `InvalidOperationException` recorded on `Fault` and `Current` left null. The rule and what it does not catch are stated for consumers in [The factory must construct](../hosting.md#the-factory-must-construct).

Failing closed was measured both ways. The guard gates on `IsHandlerOwnedInstance`, which is wider than the harm it names: `DisposeInstanceAsync` acts on `IDisposable` and `IAsyncDisposable` only, so a hosted service implementing neither is stopped and never disposed, and handing it back would in fact start it cleanly. With the guard such a service starts once and the fault is set; with the guard disabled it starts twice and works. The rule stated is therefore "a factory attachment constructs on every call", not "the handler would otherwise hand back a disposed instance"; why the wider rule is the better one is argued in the consumer facing section linked above.

The check is one reference comparison against `_lastFactoryInstance`, and it sits ahead of the dispose-on-failed-start path, so nothing is disposed twice. Pinned by `HostedServiceHandlerTests.WhenTheFactoryReturnsTheInstanceItAlreadyProduced_ThenTheStartFaultsInsteadOfUsingItAfterDispose`.

A cancelled stop is caught and **not** recorded as a fault, and the dispose after it still runs; the comment at that `catch` records why, and `HostedServiceHandlerTests.WhenAStopIsCancelled_ThenTheInstanceIsStillDisposed` pins both halves.

Both places that rethrow a recorded fault to a caller, `HostedServiceHandler.WaitForStartAsync` and `InterceptorHostingExtensions.AttachHostedServiceAsync`, use `ExceptionDispatchInfo.Capture(fault).Throw()` rather than `throw fault`, for the reason recorded at the second of them. Pinned by `HostedServiceHandlerTests.WhenAFailedStartIsRethrownToTheAttachingCaller_ThenTheOriginalStackSurvives` and `HostedServiceHandlerTests.WhenAStartFaultedForAWaitingCaller_ThenTheFaultIsRethrown`.

## Shutdown

### The barrier

`StopAsync` is built from the pieces above rather than around them:

1. `BeginDraining`, which stops new targets being taken and releases parked waiters.
2. Snapshot `_inFlightStops` immediately, so it holds exactly the stops queued before the drain.
3. Clear `_liveSubjects`, for the reason recorded there, and because it is also what stops `WaitForStartAsync` appending an empty transition behind the drain's own stop, which is what `HostedServiceHandlerTests.WhenAHandlerIsAskedToWaitWhileItsOwnDrainIsStopping_ThenItAnswersWithoutQueueingBehindTheStop` pins: deleting the clear is the one change that fails it.
4. Snapshot `_running` and append stops in the same per subject shape a context detach uses: a stop carrying a `subjectStopped` signal for every subject target, then a stop for every attachment target that awaits its own subject's signal when that subject was in the snapshot.
5. Await those plus the stops from step 2, bounded by the host's stopping token.
6. Release ownership of every target in the snapshot, clear `_running`, `CompleteDraining`.

A cancelled wait in step 5 is swallowed rather than propagated, and the drain still finishes: rethrowing would abandon step 6, so every target this handler owns would stay owned by a dead handler and a second host over the same subjects would start nothing. That reason is at the `catch`, and why nothing below step 5 observes the token is at the wait above it. Pinned by `HostedServiceHandlerTests.WhenAServiceStopNeverReturns_ThenShutdownDoesNotOutlastTheTimeout` and `HostedServiceHandlerTests.WhenTheShutdownTokenIsAlreadyCancelled_ThenTheInstanceIsStillDisposed`, both of which fail when the cancellation is rethrown instead.

### Joining and leaving the running set

**A target joins `_running` when its start is taken and leaves when its stop is appended**, not when either completes. Joining at completion would let the drain's snapshot miss a queued start, which is the same race the `Draining` state closes and should not depend on two mechanisms agreeing. Joining happens after the ownership take rather than before, for the reason recorded at the join.

Leaving at append time is why step 2 exists: a stop appended just before the drain is in no `_running` snapshot, so without it the drain returns while that stop is still running, and the host disposes the service provider the moment `StopAsync` returns. Each entry removes itself through a continuation, so the set does not grow with the process. Pinned by `HostedServiceHandlerRaceTests.WhenAStopIsInFlightWhenTheHostDrains_ThenTheDrainWaitsForIt`.

### The two gate reads in `TryTakeOwnershipAndStart`

`TryTakeOwnershipAndStart` reads the gate twice, once on entry and once after writing both `_running` and the owner, for the reasons recorded at both reads. `AttachSubject` re-reads the gate after writing `_liveSubjects` for the same reason.

`HostedServiceHandlerRaceTests.WhenAnAttachmentIsAddedDuringTheDrain_ThenTheDrainingHandlerTakesNoOwnership` pins that a draining handler ends up owning nothing, and it fails only when **both** reads are deleted: its attach arrives after `BeginDraining`, so the read on entry already refuses it. Each read also has a test the other does not satisfy, and deleting either one alone fails exactly that one:

- The re-read: `HostedServiceHandlerRaceTests.WhenAnAttachLandsItsTakeAfterTheDrainBegan_ThenTheTakeIsUndone`. Its attach passes the read on entry while the gate is still `Running` and lands both writes after `BeginDraining`. The seam between the two is `TakeStartupHolds`, which is third party code on that path: the deferrer starts the drain and waits for it to reach `DrainGate` before the attach goes on. The ownership is read while the drain is still held, because letting it go releases every target the drain's snapshot covered and hides the difference.
- The read on entry: `HostedServiceHandlerRaceTests.WhenADrainingHandlerSeesAnAttach_ThenItInstallsNoOwnerForALiveHandlerToLoseTo`. It holds the take open on `OwnershipTakenGate` and has a second handler try the compare and exchange from there. The re-read undoes a take, but only after installing it, and a live handler that reaches the target inside that window loses the exchange for good, because nothing retries it. The seam is reached only when the read on entry is gone, so on an intact build the attach simply returns and the second handler wins.

### The two gate reads in `AttachSubject`

What these two protect is the liveness entry rather than the owner: whichever of them is missing, the take itself is still refused by `TryTakeOwnershipAndStart`'s own reads, and the damage that survives is a subject rooted on a dead handler.

- The re-read: `NestedAttachTests.WhenTheDrainBeginsWhileANestedAttachHoldsTheOuterOne_ThenNeitherSubjectStaysLiveOnTheDrainingHandler`. A deferrer creates a child from inside the container's attach, and the hold that nested attach takes starts the drain, so both calls wrote their liveness entries while the gate was still `Running` and both have to notice on the way out. The set is read while the drain is held at `DrainGate`, which is ahead of the liveness clear, because letting the drain go clears both entries for an unrelated reason and hides the difference. Deleting the re-read alone fails it.
- The pair: `NestedAttachTests.WhenAnAttachHandlerCreatesAChildAfterTheDrainClearedLiveness_ThenNeitherSubjectIsLeftLive`, which attaches a container, and the child its attach handler creates, into a drain parked inside a stop body, past the liveness clear. Parking there rather than on `DrainGate` is what makes the damage permanent: an entry written while the drain is held at `DrainGate` is swept up by the clear that follows it. Deleting either read alone leaves it green, because the write and the removal cancel out, so it fails only when both are gone.

The read on entry has no test that fails for it alone, and that is a coverage limit rather than redundancy. The window it covers is a drain beginning between it and the liveness write beside it, and those two statements are adjacent, so no seam can drive anything into the gap.

## Activation and Waiting for a Start

`SubjectActivation<T>` exists because a singleton nobody resolves is never constructed, never attached to its context and never started, and `IHostedService` is the only hook the generic host offers for forcing that construction. Resolving the subject attaches it, which makes the handler append the start. When the resolved context has no `HostedServiceHandler` the activation starts the subject itself and stops exactly that instance, for the reason at the field it records it in. Pinned by `AddSubjectTests.WhenThereIsNoHostingHandler_ThenTheActivationStartsTheSubjectItself`.

`WaitForStartAsync` appends an empty transition to the same chain and awaits it. Appending never runs a body, so that transition completes only once the start ahead of it has run. It then rethrows the recorded fault, which preserves the `AddHostedService` guarantee that a failing subject aborts host startup and that `ApplicationStarted` implies the subject is running.

It reads the target and never creates one, and never takes ownership; the comment there records why a claim taken from a wait would never be released. Its false result is not licence for the caller to start the subject itself, and `SubjectActivation<T>` records that decision at the call it makes. Pinned by `HostedServiceHandlerTests.WhenADrainedHandlerIsAskedToWaitForAStart_ThenItClaimsNothingAndReportsNothingStarted` and `HostedServiceHandlerTests.WhenANonOwningHandlerIsAskedToWaitForAStart_ThenItReportsNothingStarted`.

## The 50 ms Delay

Both the start and the stop body delay 50 ms before touching the instance. It covers a caller side hazard: the generated context constructor attaches the subject last, so `new Car(context) { Name = "x" }`, deserialization, and `AddSubject`'s `configure` on the generated context constructor path all assign after the attach has fired and after the start has been appended.

The delay is a mitigation, not a synchronization, and removing it is a separate problem: it needs a "subject fully constructed" signal, which touches the generator. What protects against the hazard is the gap between a target's own attach event and its own start, and that is 50 ms whether the delay sits in a shared loop or in each target's own transition, so moving it into the transition lost nothing. Both delays pass `CancellationToken.None`, so shutdown waits each one out per target.

### Where the cost is constant, and where it is linear

Per target chains stop the delay serializing across targets, but that only removes the linear cost on the path where nothing waits for the starts one at a time. The two paths differ in shape:

- **Subjects entering the graph.** Each start is appended to its own target's chain and nothing awaits them in turn, so the delays overlap: a set of subjects entering the graph together pays one delay rather than one each, and the cost is constant in how many of them there are. Under the shared loop it was linear, because every start waited out the delay of the start ahead of it. `HostedServiceStartupShapeTests.WhenManySubjectsEnterTheGraphTogether_ThenTheirStartsOverlap` pins it without measuring time at all, and its remarks record the timing based version that was tried first and why it was wrong.
- **`AddSubject<T>`.** Still linear. `AddSubject<T>` registers one `SubjectActivation<T>` per type through `AddHostedService`, the activation awaits `WaitForStartAsync` when `T` is an `IHostedService`, and the generic host starts hosted services one after another by default, so each activation's 50 ms is over before the next one begins. The cost is linear in the number of registered types that implement `IHostedService`, at one delay each. A registered type that is a plain subject awaits no start and adds nothing. Nothing pins this path, because what it measures is the generic host's own sequential start rather than anything this package decides.

`AddSubject` does not fix its own path, and that is deliberate: the switch that fixes it, `HostOptions.ServicesStartConcurrently`, is host wide and belongs to the application author. Stated for consumers in [`AddSubject<T>()`](../hosting.md#addsubjectt).

## Residual Hazards

Per target chains contain the self deadlock that one shared queue spread across every service. They do not remove cycles, and none of the four shapes below is detected.

### 1. An attachment whose own `StopAsync` detaches itself

The detach appends to the chain the running stop occupies.

### 2. Two subjects whose services detach each other's attachments from their own stop paths

The same cycle across two chains rather than one.

### 3. A subject that detaches its own attachment while unwinding

The subject's stop transition waits on the unwind, the unwind waits on the attachment chain, and the attachment chain's head waits on `subjectStopped`, which only the blocked subject transition can set:

1. The subject leaves the graph. `DetachSubject` appends the subject's stop, carrying `subjectStopped`, and appends the attachment's stop, which first awaits `subjectStopped`.
2. The subject's stop runs. `BackgroundService.StopAsync` awaits the execute task, and `ExecuteAsync` unwinds into a helper that awaits `DetachHostedServiceAsync` for the attachment.
3. That call appends its own stop to the attachment's chain, behind the stop from step 1, and awaits it.
4. The attachment's chain head is still awaiting `subjectStopped`, which is set in the `finally` of the subject's stop, which cannot finish because it is still inside step 2.

Shape 3 is the one that occurs in practice, because it is what a `BackgroundService` reaches when `ExecuteAsync` unwinds into a stop helper that detaches. It is the shape both HomeBlaze OPC UA wrappers would have if their unwind detached, which is why each one's unwind only resets its reported state. The rule is stated in [the user documentation](../hosting.md#do-not-detach-from-your-own-stop-path) rather than guarded, and `HostedServiceHandlerTests.WhenASubjectOwningAnAttachmentIsStoppedByTheHost_ThenShutdownCompletesWellInsideTheTimeout` is the regression guard. A wedged chain is unbounded in damage but bounded in blast radius: shutdown gives up on it at `ShutdownTimeout` and every other chain drains normally.

### 4. A deferrer that takes a lock of its own

`TakeStartupHolds` calls `IStartupCompletionDeferrer.DeferCompletion()` synchronously from `HandleLifecycleChange`, and the refused-append path disposes those holds from the same place. Both run under `_attachedSubjects`. A deferrer that takes a lock of its own therefore joins that lock's order, and the resulting cycle has three parties:

1. Thread A takes the deferrer's own lock `L`, then awaits a hosted service transition `T`, for example through `AttachHostedServiceAsync`.
2. `T`'s body writes a subject typed property, so it needs `_attachedSubjects`.
3. Thread B holds `_attachedSubjects` for an unrelated graph write, reaches `HandleLifecycleChange`, and calls `DeferCompletion()` on that same deferrer. It blocks on `L`.

`A` waits for `T`, `T` waits for `_attachedSubjects`, `B` holds it and waits for `L`, and `A` holds `L`. Nothing resolves it, and unlike the three chain wedges above the blast radius is the whole process rather than one chain: `B` is holding `_attachedSubjects`, so every structural property write anywhere in the graph queues behind it.

**The call site is accepted rather than fixed, and it is not by itself the deadlock.** The hold must exist before the append completes, or the window it closes reopens: a subsystem that treats "the graph has finished starting" as a completion point would pass that point with a queued start still on its way in. On the lifecycle driven path the event that appends arrives already inside `_attachedSubjects`, so there is no earlier point at which to take it. Every alternative that keeps the guarantee either calls `DeferCompletion` from the same place, or needs a new cross-package protocol between Hosting and Connectors, which is a design change rather than a defect fix. Deferring only the release off the lock was considered and rejected: it costs an allocation and a thread hop on a rare path and leaves the take, which is the main exposure, exactly where it was.

What the call site does is put a constraint on the implementer, and an implementation that follows it cannot supply the step the cycle needs. The constraint is stated on [`IStartupCompletionDeferrer`](../../src/Namotion.Interceptor.Tracking/IStartupCompletionDeferrer.cs), where an implementer meets it. Step 3 of the cycle is the only step a deferrer supplies, so a deferrer that never blocks there leaves nothing for `A` and `T` to close a cycle against. The exposure is therefore per implementation, not per consumer: an application whose deferrers all follow the rule is not exposed to this hazard at all.

`SourceMonitor`, the only implementation in this repository, follows it: its take is an `Interlocked.Increment` that acquires nothing, and its release takes the monitor's `_lock` in an order that type already fixes for itself, which its `DeferCompletion` remarks set out. What holds that order is that nothing under `_lock` ever waits on anything that needs `_attachedSubjects`: the graph walk in `IsBranchSynchronized` reads parent sets, and completing a wait uses `RunContinuationsAsynchronously`, so no continuation runs on the releasing thread.

### Disposal from a handler transition

The handler disposing what it created puts a constraint on connectors that nothing enforces and no test covers: it disposes from a transition that can run while a detach cascade still holds `_attachedSubjects`, so a connector's dispose path and that lock interleave.

The concrete collision is `SourceOwnershipManager`. Both its `Dispose` and its `SubjectDetaching` handler take its own lock and invoke `onReleasing` from inside it, and the `SubjectDetaching` handler runs from inside `_attachedSubjects`. That fixes one lock order, `_attachedSubjects` then the manager's lock, and a dispose that runs from a handler transition takes the manager's lock without holding `_attachedSubjects`. The order reverses the moment anything on that dispose path enters `_attachedSubjects`, which is what an `onReleasing` callback that writes a subject typed property or attaches or detaches a subject does.

The constraint that follows is stated in [the user documentation](../hosting.md#keep-the-dispose-path-out-of-the-lifecycle-lock). Note that `LifecycleInterceptor.WriteProperty` takes the lock only when the property type can contain subjects, which is why writing a scalar from a dispose path is harmless and writing a subject typed or collection typed property is not, and why attaching or detaching a subject enters the same lock without being a property write at all.

## Invariants

Once lifecycle events and user driven attach and detach calls have settled:

1. **One instance per target.** A target holds at most one non null `Current`, and only a start body ever sets it.
2. **In the graph implies running, out of the graph implies stopped.** Each chain drains in append order, so every target ends in the state its last event demanded. Execution across targets is concurrent, so this is quiescent consistency rather than a moment by moment guarantee.
3. **Created implies disposed by the same owner.** The handler disposes exactly the instances it created through a factory, once, and never disposes a subject.
4. **A subject's stop precedes the disposal of its own attachments**, on both the context detach path and the shutdown path, whenever that stop is not cancelled.
5. **No transition body runs under `_attachedSubjects`.** Every lifecycle driven action is an append, and an append never runs a body. This is not the same as "no user code runs under that lock": `DeferCompletion` and the hold disposal on the refused append path do, which is [residual hazard 4](#4-a-deferrer-that-takes-a-lock-of-its-own).
6. **A drained handler roots nothing.** It releases every target it owned, clears its running and liveness sets, and leaves the records on the subjects for the next handler.
