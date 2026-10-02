# Hosted Service Ownership: Internal Design

This document describes the internal concurrency model of `Namotion.Interceptor.Hosting`: how `HostedServiceHandler`, `HostedServiceSlot` and `HostedServiceGate` decide when a subject bound hosted service starts, stops and is disposed. Consumer rules live in the [Hosting](../hosting.md) documentation and are linked rather than repeated here.

## Overview

Everything below exists to keep [the rule](../hosting.md#the-rule) true under concurrency: lifecycle events fire from inside `LifecycleInterceptor`'s `_attachedSubjects` lock, callers attach and detach from arbitrary threads, and the host starts and drains underneath both.

The unit of management is a **slot**: either a subject that implements `IHostedService`, or one factory attachment. Each slot owns a serialized **transition queue**, so start, stop and dispose for that one slot never interleave, while transitions for unrelated slots run concurrently.

Several mechanisms below look like they could be simplified. The reason recorded with each is what stops that. Where the code already carries the argument next to the mechanism, this document names the mechanism and points at it.

## Data Structures

### `HostedServiceSlot`

One per managed thing, stored in the subject's `Data` bag:

```
Factory               Func<IHostedService>?    the factory for an attachment, null for a subject slot
Subject               IHostedService?          the subject for a subject slot, null for an attachment
IsActivation          bool                     true for the activation of an ISubjectHostedServiceFactory subject, fixed at construction
_snapshot             SlotSnapshot           the running instance and the transition phase as one immutable pair, replaced by a single write per transition
_fault                Exception?               the exception from the last failed transition
_startFault           Exception?               the exception from the last failed start, cleared with _fault and written by no later transition
_owner                HostedServiceHandler?    the handler that claimed this slot
_lastFactoryInstance  IHostedService?          the instance the previous factory call returned, kept for the life of the attachment
_detached             bool                     set by an explicit detach, refuses every start enqueued after it and none already queued
_stopSignal           TaskCompletionSource?    subject and activation slots only: the completion of the stop that ends the current ownership, reset on every install
_stopSignalOwner      HostedServiceHandler?    the handler _stopSignal was created for
_tail                 Task                     the transition queue
_queueLock            Lock                     guards _tail, _detached, _stopSignal, and the owner exchange together with the handler's drain tracking of it
TransitionTestHook    Func<Task>?              test seam awaited at the top of every body, null in production
QueueLockTestHook     Action?                  test seam invoked inside _queueLock between the take and the enqueue, null in production
```

The fields are synchronized differently, and each difference is deliberate:

- `_snapshot`, `_fault` and `_startFault` use `Volatile.Read` and `Volatile.Write` and are written by transition bodies only, which the queue serializes. A diagnostics poll reads them from an unrelated thread with no happens before edge to the writing body, so the qualifiers are required rather than decorative. `_snapshot` carries the instance and the phase together so every transition is one write and every read one load (see [The state a consumer polls](#the-state-a-consumer-polls)).
- `Owner` uses `Volatile.Read` and is never written with `Volatile.Write`. Its only writers, `TryTakeOwnership` and `ReleaseOwnership`, run under `_queueLock` and go through `Interlocked.CompareExchange`, which carries the fence and decides which of two racing handlers claims the slot. See [Ownership](#ownership).
- `_lastFactoryInstance` is neither volatile nor locked and never cleared; both reasons are on `HostedServiceSlot.TryRecordFactoryInstance`.
- `_detached` is written and read under `_queueLock`, which pairs it with the enqueue (see [Refusing a start for an attachment a detach already removed](#refusing-a-start-for-an-attachment-a-detach-already-removed)). `GetState` reads it outside the lock, deliberately, so polling never queues behind an enqueue; that read is why the field is volatile.
- `_stopSignal` is created, handed out and reset under `_queueLock` only, in the same acquisition as the enqueue or the install it belongs with (see [The stop signal](#the-stop-signal)).

`IsFactoryAttachment` is simply `Factory is not null`, and it is the whole disposal policy: the handler created the instance if and only if it invoked a factory to get it, so it disposes attachment instances and never disposes a subject.

### `HostedServiceHandler`

```
_logger          ILogger?                set once by the service provider factory, volatile
_serviceProvider IServiceProvider        what an automatic activation creates services with, EmptyServiceProvider until the same factory sets it, volatile
_activateSubjectHostedServices
                 bool                    whether a context attach activates an ISubjectHostedServiceFactory subject
_gate            HostedServiceGate       Closed, Open, Draining
_stopOnDrain           ConcurrentDictionary    slot -> subject, for slots whose ownership this handler installed
_liveSubjects    ConcurrentDictionary    subject -> unused, for subjects in the graph that host something
_inFlight        int                     transitions this handler enqueued that have not finished
_startDeferral   AsyncLocal              the ambient start deferral an enqueuing flow is inside, cleared in every transition body a handler attributed
DrainTestHook, OwnershipTakenTestHook, LivenessWriteTestHook, DrainEnqueueTestHook, DrainReleaseTestHook
                                         test seams, null in production
```

`_stopOnDrain` and `_inFlight` are the shutdown state, and they are two facts rather than one because they have different lifetimes: **what to stop and release** lives as long as an ownership, **what to wait for** lives as long as a transition. Conflating them makes correctness depend on the order of those writes against the drain's unlocked reads. `_stopOnDrain` maps a slot to its subject because the drain groups the stops it enqueues per subject to reproduce the ordering a context detach gives.

The seams are documented where they are declared; each holds open a window between two statements that are adjacent in production.

`_liveSubjects` holds an entry only for subjects that host something. Every reader of liveness holds a `HostedServiceSlot`, so a subject with no slot has no reader, and recording the whole graph would cost a dictionary write per subject on attach. Two facts keep that safe:

- **A subject gaining its first slot after it entered the graph records its own liveness**, in `MarkLiveIfAttached`, with the write *inside* `LifecycleInterceptor.TryRunWhileAttached`'s callback. Reading membership and then writing releases the graph lock in between, and a graph move in that gap makes the write land on the opposite answer.
- **A context detach clears liveness for every subject that has *ever* hosted something.** `RemoveAttachment` stores null rather than removing its data key, so `TryGetHostedServiceAttachments` reports "has ever hosted" at the cost of one lookup. Keying the fast path off the current attachments leaves an entry behind, and a start already queued against a detached attachment then re-reads liveness in its body and starts an instance for a subject that has left the graph.

Retained memory is therefore linear in the number of hosted services rather than in the size of the graph. `_stopOnDrain` is bounded the same way, because a slot only enters it through a take.

The logger and the service provider are assigned after construction because `WithHostedServices` constructs the handler while the context is being configured, before any service provider exists; the registration it adds calls `SetLogger` and `SetServiceProvider` when the provider builds it. An automatic activation's factory reads the provider when it creates the service, not when the subject attaches, so a subject attached during configuration gets the host's provider once its start runs.

### Where slots live

Slots live on subjects rather than in the handler, so nothing in the handler roots a detached subject and a factory survives a detach. That is what lets a subject that leaves the graph and re-enters it get working services again without a restart contract from the service.

Attachments live under one data key as an `ImmutableArray`, and the subject slot under another. The correctness and allocation constraints on both paths are on `InterceptorHostingExtensions.AddAttachment` and `InterceptorHostingExtensions.GetOrAddSubjectSlot`. A third key holds the activation entry of an `ISubjectHostedServiceFactory` subject: the one attachment explicit and automatic activation share, reserved with a compare and swap before it is published to the attachments, for the reasons on `InterceptorHostingExtensions.GetOrAddActivation`.

The handler carries `[RunsAfter(typeof(ContextInheritanceHandler))]`, because it resolves startup completions from `subject.Context`, and for a subject entering as a child it is `ContextInheritanceHandler` that installs the parent context as a fallback. See [Handler Order Around the Descent](tracking-lifecycle.md#handler-order-around-the-descent).

## Why Per Slot Queues Rather Than One Queue

One queue for a whole handler couples services that have nothing to do with each other: an action that awaits another action wedges every service in the context, shutdown has to decide what happens to work still queued, and draining can only begin when the handler's own `StartAsync` runs, which makes startup depend on dependency injection registration order. Per slot serialization keeps the only ordering property that is needed, that one slot's own transitions never overlap, and buys the one cross slot ordering genuinely required with a completion signal rather than a global executor. Self deadlock is contained to one queue rather than removed (see [Deadlock Shapes Not Guarded Against](#deadlock-shapes-not-guarded-against)). `HostedServiceGate` and `OpenGate` remove the registration order dependence.

One guarantee is deliberately given up. `LifecycleInterceptor.DetachFromProperty` invokes a parent's handlers before recursing into children, so a single queue stopped a parent hosted subject before its hosted descendants; per slot queues stop them concurrently. Nothing here depends on that order, and if a consumer ever needs it, the fix is another completion signal, not a global executor.

## Enqueuing a Transition

All three enqueue paths, `EnqueueAsync`, `EnqueueIfOwnedAsync` and `TryTakeOwnershipAndEnqueueAsync`, end in `HostedServiceSlot.EnqueueCore` under the slot's `_queueLock`, where the comment records why the lock and `TaskScheduler.Default` are each required. The second and third add a decision inside the same lock acquisition: ownership for [the drain's and a context detach's enqueues](#why-the-ownership-decision-is-inside-the-queue-lock), liveness and ownership together for [a take](#the-read-inside-the-queue-lock).

**`_queueLock` is a leaf lock.** Nothing held under it blocks or takes another lock: what runs there is the detach mark, a liveness read, the ownership exchange, the handler's drain tracking of it, the in flight increment and a `ContinueWith` that queues to the pool. The lifecycle lock is held on the way in by every lifecycle driven enqueue and by the context detach's releases, so **the one lock order is lifecycle lock, then queue lock**, and no path holds two queue locks at once. The only thing that can block inside it is the `QueueLockTestHook` test seam, so a test whose detach must enqueue or release on a slot it holds at that seam deadlocks itself.

`RunAsync` catches everything, so the queue is never faulted. Bodies record failures into `Fault` and log them instead.

### Enqueuing never blocks and never runs the body

A default `ContinueWith` never executes inline on the enqueuing thread, so a lifecycle handler may enqueue while the lifecycle interceptor holds `_attachedSubjects`, and no transition **body** ever runs under that lock.

Third party code does run under that lock, though, and it is not the body: deferring startup completion and releasing those completion deferrals calls into `IStartupCompletion` synchronously from the lifecycle event. That is [deadlock shape 4](#4-a-startup-completion-that-takes-a-lock-of-its-own).

## Enqueuing at Event Time

Every enqueue happens when the lifecycle event fires, never deferred into another transition. This is what makes moving a subject through the graph work.

### Why a composite transition is wrong

The requirement is that a subject's own stop completes before the attachments it uses are disposed. `BackgroundService.StopAsync` awaits its execute task, so a subject's stop is slow, and an attachment disposed underneath it is observed as already disposed by code still unwinding inside `ExecuteAsync`. The tempting fix is one composite transition on the subject's queue that stops the subject and then stops its attachments. A detach immediately followed by an attach shows why it is wrong:

1. The subject leaves the graph. The composite transition is enqueued on the **subject's** queue. It has not run yet.
2. The subject re-enters the graph. The create and start for the attachment is enqueued on the **attachment's** queue, which is empty, so it runs. Instance B is now running.
3. The composite transition finally runs. It stops the subject, then reads `Current` on the attachment and finds instance B, which it stops and disposes.

Instance A, the one running before the detach, is never disposed. Instance B, the one the graph expects to be running, is stopped and disposed, with no error anywhere.

### What a context detach enqueues

So `DetachSubject` enqueues immediately, under `lock (_attachedSubjects)`, on every affected queue:

- a stop on the subject's queue if the subject is an `IHostedService` this handler owns, which sets the subject slot's stop signal in a `finally`, so cancellation and failure release it too;
- for each attachment this handler owns, a stop on that attachment's own queue that first awaits that signal, then takes the instance out of `Current`, stops it and disposes it.

The subject's activation is ordered like the subject's own slot one level down, because the activated service runs the subject and may use what it attached: the activation slot carries a stop signal of its own, its stop awaits the subject's signal and sets its own, and the stops of the subject's other attachments await the activation's signal, or the subject's when this handler has no stop pending on the activation. One helper, `HostedServiceHandler.GetStopToAwait`, answers what a stop waits for at all three places that enqueue ordered stops, this method, the drain and the gate re-read's undo. This method takes the activation from the attachments array it read, so a stop it orders behind the activation is behind a stop it enqueues itself; the drain and the undo, which hold no array, take the subject's live activation. Either way an activation that was explicitly detached orders nothing from then on. The context attach is the mirror image here too: it takes and starts the activation ahead of the other attachments, whatever its position in the array, so a sibling's undo racing the drain never asks an activation this handler does not own yet.

The signal lives on the slot rather than in this method, because the shutdown path and the gate re-read's undo need the same ordering from code that does not run inside one method call (see [The stop signal](#the-stop-signal)). Nothing is allocated for a subject that hosts nothing, which is essentially every subject in a detaching graph, and a subject without an activation reads nothing for one.

Ordering holds because both enqueues happen under the lifecycle lock, so any later re-attach queues behind them on the same queues. The wait is acyclic, **provided neither the subject's stop nor its activated service's stop waits on an attachment queue**; that proviso is [deadlock shape 3](#3-a-subject-or-its-activated-service-detaching-an-attachment-while-unwinding). Shutdown builds the same shape from its own code in `StopAsync` rather than calling this path, so a change to one does not reach the other.

Context attach is the mirror image, also under the lock: a start on the subject's queue if it is an `IHostedService`, and a create and start on each attachment's queue.

### The stop signal

`HostedServiceSlot._stopSignal` is the completion of the stop that ends the current ownership of a slot that carries one, a subject slot or an activation slot (`CarriesStopSignal`), and every stop that must run behind that stop awaits it. It is created by whichever of two sides needs it first, under one acquisition of that slot's `_queueLock` with the operation that needs it:

- **A stop enqueue on such a slot** (`EnqueueStopWithSignalIfOwnedAsync`, only while the enqueuing handler owns the slot, which `HostedServiceHandler.EnqueueStopIfOwned` routes to by `CarriesStopSignal`) takes the current signal or creates it, and the stop body sets it in its `finally`. The choice and the enqueue are one lock acquisition, so a stop asking in between is handed the signal that stop will set. A refused enqueue leaves the signal alone.
- **A stop being enqueued behind it** asks through `GetStopToAwait(handler)`. It is handed the current signal when the asking handler is the one it was created for and it is not yet set; otherwise, while the asking handler owns the slot and has enqueued no stop for this ownership, a fresh one that handler's coming stop will take; otherwise null, and the asking stop waits for nothing.

A signal created on the asking side is set because **every ownership of such a slot ends with a stop enqueued by its owner ahead of the release**: a context detach enqueues and then releases, the drain enqueues and releases after its wait, and the gate re-read's undo enqueues while it still owns and then releases. The owner is the only handler the signal is ever created for or handed to, so no handler waits on a stop enqueued by another, whose gate it does not control. A fault stop run from the execution observer sets nothing, because it ends no ownership; the owner's eventual stop finds `Current` null, returns at once and sets the signal then.

An activation slot has two more stops a subject slot never gets, the explicit detach's and the release of a faulted awaited activation. Both go through `MarkDetachedAndEnqueueStop`, which for a slot that carries a signal enqueues through `EnqueueStopIfOwned` first, so the stop takes the signal while the detaching handler owns the slot, and falls back to the plain `EnqueueAttachmentStop` only when the enqueue was refused. The fallback is safe because refused means another handler, or none, owns the slot: the signal is only ever created for the owner, so none created for this handler can be left unset, and the slot is already marked detached. The flag is fixed at construction rather than read from the subject's activation entry, because an explicit detach marks the slot detached before it enqueues its stop, and a stop routed past the signal on a slot this handler still owns would leave a signal created on the asking side in that window set by nothing, and every stop ordered behind it parked until the shutdown deadline. `WhenTheActivationWasDetachedExplicitly_ThenAContextDetachStopsTheOtherAttachmentsWithoutWaitingForIt` and its shutdown sibling pin that a detached activation holds nothing up.

The signal is reset on every install, under the same lock. The stop that ends the previous ownership captured its signal at its own enqueue, which is queued ahead of the start the install enqueues, so the attachments ordered behind that stop still release when it runs, and a stop of the earlier ownership can never release the attachments of the later one. Two stops enqueued for one ownership, a drain's and a context detach's, share the signal: the first to run is the one that finds the instance, and the second returns at once and sets it again, which `TrySetResult` makes harmless.

### What the signal actually means

It means "the subject's stop returned", which equals "`ExecuteAsync` unwound" only when the stop is not cancelled: `BackgroundService.StopAsync` awaits its execute task with `ConfigureAwaitOptions.SuppressThrowing`, so on a cancelled token it returns while `ExecuteAsync` is still running. Graph driven detaches pass `CancellationToken.None` and get the strong reading; host shutdown passes the stopping token and gets the weak one. Forcing the strong reading at shutdown would mean ignoring `ShutdownTimeout`.

### A subject created from inside a lifecycle handler

A container that creates its default child from its own context attach is the one place a subject enters the graph while another subject's attach event is still being dispatched. The child's attach is an ordinary one rather than a re-entrant call (the remarks on `NestedAttachTests` record why), both handler orders reach the same state, and the detach cascade releases both ownerships so a re-attach can start both again.

The one caller that really does re-enter `AttachSubject` is an `IStartupCompletion`, because `DeferStartupCompletion` calls it synchronously from inside `TryTakeOwnershipAndStart`. A startup completion that assigns a subject typed property runs the whole inner attach before the outer call has taken its own slot. Nothing is shared between the two: liveness is per subject, ownership is per slot, and completion deferrals are counted, so the inner attach takes and releases its own while the outer attach's are outstanding. What this costs a startup completion is the constraint in [deadlock shape 4](#4-a-startup-completion-that-takes-a-lock-of-its-own), not re-entrancy.

## Ownership

A slot's `Owner` is taken with `Interlocked.CompareExchange`. Finding this handler already installed counts as success; only losing to a different handler means do nothing. `HostedServiceSlot.TryTakeOwnership` reports which of the two successes the caller got, because a caller that undoes its own take must leave an earlier one alone.

The take tracks the slot for the drain, in the handler's `_stopOnDrain`, **only on the install**, never on the repeat, and `ReleaseOwnership` untracks it only when its compare and exchange matched. So no call can gain membership without also owning the undo for it: every install is followed by the gate re-read, which releases when the handler is draining. Untracking unconditionally instead would leave the mirror state, a running slot the handler owns that no drain snapshot contains.

**The exchange and the drain tracking happen under `_queueLock`.** They are one fact: a release landing between an install and its tracking nulls the owner, finds nothing to untrack, and leaves a drain entry no later release can match.

**The release takes the same lock as the take, which makes a take's liveness read and its exchange one step against every release.** A context detach clears liveness first and releases last, so a take that acquires the lock after the release reads the cleared flag and refuses, and a take that acquired it before is the install the release then matches. The drain clears liveness under no lock, so a take can still pass its liveness read as the drain begins; that one is caught by [the gate re-read](#the-gate-reads) instead. Releasing under the queue lock cannot deadlock because the lock is a leaf (see [Enqueuing a Transition](#enqueuing-a-transition)).

### Ownership is decided on context detach, with the enqueue

`DetachSubject` enqueues a stop only for the slots it owns, through `EnqueueStopIfOwned`, so the read and the enqueue are one acquisition of the queue lock like [the drain's](#why-the-ownership-decision-is-inside-the-queue-lock). The comment at that call records what a handler stopping a stranger's instance costs and why the read cannot move into the body. Read a statement ahead of the enqueue instead, a detach preempted between the two could enqueue its stop after the drain's second wait read the count as zero, so the stop runs against a provider the host is disposing, or reaches an instance a second host started after the release. A stop refused because ownership moved is a stop something else already enqueued: every release is preceded by the releaser's own stop on the same queue.

### Ownership is released on context detach and on drain

Always after the stops are enqueued, and never from inside a transition body. Releasing from the body clobbers an ownership a re-attach has already retaken, leaving a subject in the graph with nothing running. Releasing before enqueuing lets a second handler's start land ahead of the first handler's stop on a shared queue.

Release on detach is what lets a subject moved between contexts be picked up by the next handler. Release on drain is what lets a second host run over the same subject instances; without it no later handler can ever win the exchange. A slot whose start faulted is released the same way, because drain tracking follows the take rather than the instance.

### An explicit detach untracks the slot for the drain without releasing ownership

`DetachHostedService` and `DetachHostedServiceAsync` stop the slot and untrack it for the drain, and deliberately leave `Owner` installed: a start queued ahead of the detach would otherwise read `Owner` as null, refuse, and leave the stop with no instance to dispose. That ordering is the guard `MarkDetached` exists for. Untracking it is not optional, or every attach and detach cycle keeps the slot and its subject on the handler for its whole life. The awaiting overload untracks it before it awaits the stop, so a cancelled wait cannot skip it. Any future site that stops a slot without releasing it inherits the same rule.

### A faulted awaited attach releases instead

`AttachHostedServiceAsync` is transactional: a start that faults has its attachment removed before the exception propagates. The removal puts the slot out of reach, so the ownership that call took has to be undone there or a host that retries failed attaches leaks a subject per failure.

It marks the slot detached, enqueues a stop, then **releases**. The attachment is published before the start is awaited, so a second start can be queued against the slot while the first is in flight, and when the first faults the same completion releases both the caller's cleanup and that queued body with nothing ordering them. The cleanup therefore does not depend on the order: `EnqueueAttachmentStop` does not read `_detached`, the queue is first in first out so the stop lands behind the queued start and disposes what it created, the stop body reads `Current` and never `Owner` so releasing ahead of it is safe, and the mark refuses every later start. The mark goes first, so a context attach that snapshotted the attachment before the removal cannot take the slot in the gap. Reaching the queued start needs a second `LifecycleInterceptor` over the subject, the same precondition as [A handler can be marked live for a graph it does not serve](#a-handler-can-be-marked-live-for-a-graph-it-does-not-serve). The stop is not awaited, because the queued start may be parked on a start deferral this caller cannot close ([deadlock shape 5](#5-awaiting-a-captured-start-or-its-detach-inside-its-own-start-deferral)); it is attributed, so shutdown waits for it.

The awaited activation of a subject's own hosted service, `ActivateHostedServiceAsync`, uses the same release through the one helper both share, `ReleaseFaultedAttachmentAndThrow`, after waiting for the start the owning handler enqueued. The activation it removes is marked detached and dropped from the subject's activation entry, so the next activation reserves a fresh one rather than hand back the removed one.

### Ownership is not what makes two contexts over one subject benign

A subject reachable from two hosting enabled contexts raises one context attach per context and the **owning** handler sees both, so it enqueues two starts on the same queue and loses no exchange. What closes it is the one instance guard inside the start body, where the queue serializes the two starts; at enqueue time both would still see an empty slot.

## Liveness

**Liveness is a per subject flag, not per slot ownership.** It is set when a subject that hosts something enters the graph and cleared when a subject that has ever hosted something leaves it, both under `lock (_attachedSubjects)`, and a start consults it before doing anything. A subject that gains its first slot while already in the graph sets its own flag through `MarkLiveIfAttached`.

Queue order covers lifecycle driven enqueues, because every lifecycle event fires under the lifecycle lock and the handler enqueues inside it. A user driven `AttachHostedService` enqueues under the slot's own lock only and is unordered against them, so a start needs a second check. Slot ownership cannot be that check: the attaching path takes ownership itself, so an attach racing a detach would pass its own check and leave the attachment running on a detached subject.

### The read inside the queue lock

`TryTakeOwnershipAndEnqueueAsync` performs the liveness read, the detach mark read, the ownership take and the enqueue under one acquisition of the slot's lock. One liveness read is enough there because [the release takes the same lock](#ownership). Split apart, an explicit detach can land between them: its stop runs first, finds nothing to stop, and the start behind it creates an instance the detach has already made unreachable.

### The read inside the start body

The body re-reads liveness **and** ownership before creating anything, covering a detach that lands after the enqueue and before the body runs. Neither half masks the other:

- The flag is the only refusal after an explicit detach, which untracks the slot for the drain without releasing ownership, so the body still reads this handler as the owner.
- Ownership is the only refusal for a queued body whose liveness a later context attach rebuilt: a context detach clears liveness and releases, an explicit detach of one of two attachments keeps the re-attach from re-taking that slot, and the context attach behind it writes liveness again.

Without the liveness read the damage would be bounded rather than a leak: the same detach has already enqueued a stop for that slot, so the instance the start creates is stopped and disposed behind it. The cost is a needless create and teardown against a subject that has left the graph, which for a connector means a session opened and closed. Removing the window instead, by releasing ownership before enqueuing the stops, reopens the defect in [Ownership is released on context detach and on drain](#ownership-is-released-on-context-detach-and-on-drain).

The flag is also what makes an attach onto an already detached subject fail closed, and why `WaitForStartAsync` on a handler whose drain has cleared liveness answers immediately instead of queueing behind that drain's stop. Attaching to a subject outside a hosting enabled graph stores the factory and runs nothing: a subject whose context resolves no handler never reaches this code, and one whose context still resolves the handler but which has left the graph is refused here.

### Refusing a start for an attachment a detach already removed

An explicit `DetachHostedService` clears no liveness, so the flag cannot see it. Both detach overloads therefore call `MarkDetached()` on the slot, under the queue lock, after `RemoveAttachment` succeeds and **before** they enqueue their stop, through the one `MarkDetachedAndEnqueueStop` that the faulted path of `AttachHostedServiceAsync` shares. `TryTakeOwnershipAndEnqueueAsync` reads the mark inside the same lock acquisition that reads liveness, which leaves two orders and no third:

1. The attach wins the lock. The start is enqueued, the detach's stop lands behind it on that queue, and the stop stops and disposes whatever the start created.
2. The detach's stop wins the lock. The mark is already visible, so nothing is enqueued, no ownership is taken and nothing is tracked for the drain.

Without the mark that start runs after the attachment has been removed, and the instance it creates is reachable from nothing.

### A handler can be marked live for a graph it does not serve

`MarkLiveIfAttached` asks every `LifecycleInterceptor` reachable from the subject and records if any reports the subject attached, because a subject in two hosting enabled contexts is live for both handlers and one interceptor not holding it says nothing about the other. A handler can therefore be marked live on the strength of a graph it does not serve, then take ownership and start the service, and no context detach on the other interceptor's side ever reaches it, so nothing stops that instance before host shutdown drains what the handler owns.

The reachable shape is one hosting enabled context plus one lifecycle only context, because `TryGetService<HostedServiceHandler>()` throws when two hosting contexts are reachable, and it needs a manual `AddFallbackContext`: `ContextInheritanceHandler` fires only at reference count one, so a subject already in one graph never gains a second context through ordinary assignment. Recorded rather than defended against, because the guard would have to know which interceptor's graph a handler serves, which the handler does not.

### A handler that loses the exchange to a departing handler never retries

`DetachSubject` clears liveness, enqueues its stops and releases ownership last. A *different* handler attaching the same subject concurrently, whose exchange lands between the clear and the release, finds the departing handler still installed, fails the take and enqueues nothing; the departing handler then nulls the owner, leaving the subject live for the second handler, the slot owned by nobody, nothing running and no error recorded. A sequential move is safe, because a returned detach has already released, and so is a re-attach to the same context, because a repeat take by the same handler succeeds.

This is a known defect, reachable only with two hosting enabled contexts over one subject (a manual `AddFallbackContext`) plus concurrent graph mutation on both. A fix is new design rather than a reordering, because the losing handler has to learn that ownership became free, and releasing before enqueuing the stops reopens a defect.

## Resolving the Handler on the Public Paths

### The lookup precedes the mutation, on all five entry points

`AttachHostedService`, `AttachHostedServiceAsync`, `DetachHostedService`, `DetachHostedServiceAsync` and `ActivateHostedService` resolve the handler before they touch the subject. `TryGetService<HostedServiceHandler>` throws when the subject is reachable from two hosting contexts, so a lookup made after the mutation hands the caller an exception with the subject already changed: an attach leaves a stored attachment the next context attach starts and the caller has no handle to, and a detach leaves the instance running with its attachment already gone and nothing that will ever stop it. For an activation the mutation is the entry reservation, which the next context attach acts on. The automatic activation inside `HostedServiceHandler.AttachSubject` deliberately makes no lookup: it is the handler, it runs under the lifecycle lock, and the loop behind it starts what it published.

### An attach and a context entry are the same two facts in opposite orders

An attach reads the handler and then stores the attachment. A subject entering a graph publishes the context and then reads the subject's attachments. Neither single order is safe:

- **Lookup, then add.** A context published between the two is missed by both sides, and the subject sits inside a hosting graph holding a factory nothing will ever invoke, with nothing logged.
- **Add, then lookup.** The lookup can throw after the subject has been mutated.

The attach therefore does both, and the second read counts rather than throwing. `TryResolveHandlerAfterPublish` runs only when the first lookup found none, and counts the reachable handlers instead of going through `TryGetService`, so two of them are declined rather than thrown on, which is the shape every other path on that subject throws on anyway. Its `Interlocked.MemoryBarrier` is the reading side's fence: the add is a release store under a data bucket lock, which does not order that store against this later load, and the publishing side already fences through `InterceptorSubjectContext.PublishState`'s `Interlocked.Exchange`.

The tests for this window gate the subject's first `Data` read through `Models/DataGatedSubject`, so nothing on either attach overload's path ahead of the add may read `subject.Data`, or the window they drive moves. An explicit activation has the same two facts around its reservation and its publish, and `Models/DataGatedFactorySubject` gates a counted `Data` read to drive a context entry into that window, so the number of `Data` reads on `GetOrAddActivation`'s publishing path is part of what those tests pin.

## The Gate

`HostedServiceGate` has three states and moves forward only: `Closed`, `Open`, `Draining`. `Draining` is final, so it also covers a drain that has returned. `OpenGate` advances `Closed` to `Open` and does nothing in any other state; a plain assignment would let a detach arriving during shutdown flip `Draining` back to `Open`.

### Where the gate is read

**Stops are never refused by the gate at enqueue time, and a start's gating decision is re-read in the body.**

No stop enqueue reads gate state at all. A stop short circuited at enqueue time would have no body, therefore no `finally`, therefore would never set its stop signal, so the paired attachment stop would park forever and wedge that queue.

Starts are refused at enqueue time, by `AttachSubject` and by `TryTakeOwnershipAndStart`, and those refusals are about bookkeeping rather than work: a draining handler must not install itself as owner of a slot it can never start, nor record a subject as live, because a slot left owned by a dead handler makes every future handler lose the exchange. Whether the start's **work** runs is decided again in the body, because a start already queued when shutdown begins only becomes a no-op if it re-reads the state when it runs. A gated out transition still runs its signalling and bookkeeping and skips only the user visible work.

| Gate state when the body runs | start | stop |
|---|---|---|
| `Closed` | parks until the gate leaves `Closed`, then re-reads | parks until the gate leaves `Closed` |
| `Open` | runs | runs |
| `Draining` | skips the work, releases its completion deferrals | runs, signals |

### Why a stop runs at every state, after the drain included

A stop enqueued after the drain's second wait returned, by a graph move racing the drain, was counted by nobody still watching and runs after the drain returned, still holding a running instance. A stop that no-oped there would leave that instance never stopped and never disposed. Nothing is lost by letting it run, because the null `Current` check, not the gate state, is what makes a stop idempotent.

`BeginDraining` sets the opened signal even though it never opens the gate for work, which releases anything parked on a gate that was never opened. Without it, a host that aborts startup or is disposed without starting leaves transitions and their awaiters hanging.

### Who may open the gate

"Nothing runs before host start" and "a caller that started before the handler must not hang" pull in opposite directions. `SubjectActivation<T>` and the awaitable attach and detach overloads call `OpenGate`, since awaiting is an explicit request for the service to be running. The synchronous overloads and every lifecycle driven enqueue only wait for the gate, which preserves the invariant for `new Car(context)` at configuration time.

## Startup Completion

The user facing contract is in [Queued Starts and Startup Completion](../hosting.md#queued-starts-and-startup-completion), and the implementer's constraint is on [`IStartupCompletion`](../../src/Namotion.Interceptor.Tracking/IStartupCompletion.cs). Startup completion is deferred in `TryTakeOwnershipAndStart`, synchronously and **before** the enqueue, so there is no window between the attach and the completion deferral in which completion can fire. A completion deferral covers `StartAsync` returning and nothing after it, which is why children are attached in `StartAsync`.

The completion deferrals are released in the start body's `finally`, which covers every way out of the body, including a start gated out by a drain, one whose subject is no longer live and one skipped by the one instance guard. When the enqueue is refused there is no body, so that path releases the completion deferrals itself. A completion deferral that is never released blocks every synchronization wait on that tree forever.

A startup completion that throws is logged and ignored on both paths, so one startup completion cannot strand the others (reasons at the `catch` in `DeferStartupCompletion` and `ReleaseCompletionDeferrals`). A startup completion that blocks is a constraint on the implementation: see [deadlock shape 4](#4-a-startup-completion-that-takes-a-lock-of-its-own).

## Faults and Failed Starts

`Fault` holds the exception from the last failed transition on that slot. Only a start body clears it, and **after its guards, not on entry**, so a start that is gated out or skipped does not drop a fault a caller has not read. Clearing it at all matters because a graph driven start that faulted is kept so the next context attach can retry; without the clear, the next successful attach would throw a stale exception. A stop records a fault when it fails and never clears one, so `Current` null with `Fault` set reads "this should be running and is not".

**An execution fault is observed through the execute task, never through the start.** `BackgroundService.StartAsync` schedules `ExecuteAsync` and returns, so the start covers nothing the execution does. Once a start has recorded a `BackgroundService`, the handler attaches a continuation to its `ExecuteTask`:

- It runs on a fault or a cancellation, since an `OperationCanceledException` escaping `ExecuteAsync` leaves the task `Canceled` whether or not a stop asked for it, and a connector killed by a timeout would otherwise read `Running`. It runs on the default scheduler, because it takes the queue lock and the completing thread may hold anything.
- It enqueues through `EnqueueIfOwnedAsync`, so it is serialized with the queue and counted by the drain, and a release ahead of it refuses it.
- The body acts only while that run is still the one recorded (`Current` is the instance and `ExecuteTask` the observed task), because a subject is restarted in place on the same instance with a new execute task. The same check tells a stop's own cancellation apart, because every stop takes the instance out of `Current` before it calls `StopAsync`.
- The body records the fault, logs, and runs the stop body, so the slot settles to `Current` null with `Fault` set and the next context attach retries it.
- A fault the check or a refused enqueue skips is logged at debug level, which also observes it. That log is contained, because the continuation runs outside the queue's catch-all.

**The awaited paths read the start's own outcome.** `AttachHostedServiceAsync` and `WaitForStartAsync` rethrow `StartFault`, which only the start's catch writes, rather than `Fault`. The fault transition lands behind the start and can be recorded before or after the caller resumes, so reading `Fault` would make one immediate execution fault throw on one run and settle to `Faulted` on the next. The generic host stops the application on such a fault; the handler stops the instance and leaves the retry to the graph.

**The caller's token never reaches the start.** A cancelled wait must not abort a start under way (remarks on `TryTakeOwnershipAndStart`), and a cancelled token handed to `BackgroundService.StartAsync` leaves the execution never entered while the start still records the instance.

A start that faults after creating the instance stops it, disposes it when the handler created it, and leaves `Current` null: a half started connector can hold a semaphore, a session manager and a lifecycle subscription that only its own dispose releases. That cleanup runs inside a `catch` that rethrows the start's own exception, so the `catch` inside `DisposeInstanceAsync` is load bearing: an escape would hand the caller the cleanup failure instead of the start's reason.

**A factory that returns the instance it returned last time is refused**, before `StartAsync` and before the instance is recorded, with an `InvalidOperationException` on `Fault` and `Current` left null. The consumer rule is in [The factory must construct](../hosting.md#the-factory-must-construct). The guard fails closed and is wider than the harm it names: it gates on `IsFactoryAttachment`, while `DisposeInstanceAsync` acts only on `IDisposable` and `IAsyncDisposable`, so a service implementing neither would in fact restart cleanly. The rule is therefore "a factory attachment constructs on every call", not "the handler would otherwise hand back a disposed instance". The check is one reference comparison against `_lastFactoryInstance` ahead of the dispose-on-failed-start path, so nothing is disposed twice.

A cancelled stop is caught and **not** recorded as a fault, and the dispose after it still runs; the comment at that `catch` records why.

A faulted awaited attach enqueues a stop for a start that may be queued behind it; see [A faulted awaited attach releases instead](#a-faulted-awaited-attach-releases-instead).

Both places that rethrow a recorded fault, `HostedServiceHandler.WaitForStartAsync` and `InterceptorHostingExtensions.AttachHostedServiceAsync`, use `ExceptionDispatchInfo.Throw(fault)` so the original stack survives (reason at the second of them).

### The state a consumer polls

`Current` and `Fault` leave four situations indistinguishable: nothing has started yet, a start is in flight, a re-attach disposed the previous instance and has not created the next, and a start was refused. `HostedServiceAttachmentState` separates a start in flight, a stop in flight and a terminal attachment from the rest. `HostedServiceSlot.State` derives it from the snapshot the transitions already write, so `Running` is structurally identical to the snapshot holding an instance and can never disagree with `Current`. The contract per state, including that a refused start leaves the slot reading whatever it read before and that `Stopped` does not mean "nothing is queued", is on `HostedServiceAttachmentState`. Both follow from where the transitions write, below.

**The instance and the phase are one field, and every transition is one write.** `HostedServiceSlot` holds them as an immutable `(instance, phase)` pair in a single reference field:

- A start enters `(null, Starting)` past every guard and immediately before the factory, so no refused start reports `Starting`, and records `(instance, None)` with the same write that leaves the window.
- A stop enters `(null, Stopping)` with the write that takes the instance out of `Current`, so the slot never holds no instance while raising no phase. That reading is the one a poll must never see: it looks settled while the instance still holds its sessions and subscriptions.
- Each `finally` settles to `(null, None)` only while its own phase is still the one in flight. The start's condition is load bearing, because `RecordStarted` has already replaced the pair and an unconditional settle would null an instance that just started. The stop's decides nothing for the body as it stands and is kept so that a guard added above the instance read cannot turn it into a settle over a live instance.
- Both are plain read modify writes, because the queue serializes the bodies for one slot. The pairs that hold no instance are cached statics, so a settled slot allocates nothing.

The one write rule is stated on the transition methods themselves, because splitting a transition into two writes reopens the settled-while-live reading with no test able to catch it (see [What Has No Test Behind It](#what-has-no-test-behind-it)).

`GetState` and `Current` each take the pair in one load. `GetState` goes on to read `_detached` and then `Fault` only when the snapshot came back settled, and those later loads can only choose among `Removed`, `Faulted` and `Stopped`, none of which claims a transition is in flight, so the worst a stale one does is report a settled slot a moment late. `IHostedServiceAttachment<T>.GetState(out T? current)` hands the state and the instance out of the same load, so a consumer cannot hold a state that disagrees with the instance it acts on. How to act on a reading (read the fault first, take the reading immediately before the decision) is in [Reading the outcome](../hosting.md#reading-the-outcome).

The precedence has two deliberate rankings. `Faulted` is not terminal, because a start clears the fault after its guards so the next attach can retry; `Removed` is the terminal one, and that distinction is what a consumer acts on. `Removed` is not "nothing runs after this point": a start enqueued before the mark still runs, because no guard in `RunStartAsync` reads `_detached`, and the stop the detach enqueues behind it disposes what it created. The mark is terminal for every start enqueued after it.

## Shutdown

### The barrier

`StopAsync`:

1. `BeginDraining`, which stops new slots being taken and releases parked waiters.
2. Clear `_liveSubjects`, which also stops `WaitForStartAsync` enqueuing an empty transition behind the drain's own stop.
3. Snapshot `_stopOnDrain`.
4. Enqueue stops for that snapshot in the same per subject shape a context detach uses: a stop for every subject slot first, then a stop for every attachment slot that awaits [the stop signal](#the-stop-signal) of what it is ordered behind, the subject's for an activation and the activation's or the subject's for any other attachment, when this handler has one pending there. Each enqueue is refused unless this handler still owns the slot, decided inside the queue lock.
5. Wait for `_inFlight` to reach zero, bounded by the host's stopping token.
6. Release ownership of every slot in the snapshot.
7. Wait for `_inFlight` to reach zero again.

An expired token in step 5 does not throw and does not skip step 6: rethrowing would leave every slot owned by a dead handler, so a second host over the same subjects would start nothing. The count it gave up at is logged, and why nothing below step 5 observes the token is at the wait. Step 7 is skipped when step 5 gave up, because the deadline has passed.

**`_stopOnDrain` is never cleared at the end.** After step 6 the only entries left are installs whose own gate re-read releases them, and clearing would make the set and the `_owner` field disagree for anything still in flight.

### What the drain waits for

`_inFlight` is incremented in `HostedServiceSlot.EnqueueCore`, attributed to the enqueuing handler, and decremented in `RunAsync`'s `finally`:

- **The increment is before the `ContinueWith`, not after.** On an already completed tail the continuation can run and decrement before the next statement on the enqueuing thread, taking the count negative, and a later increment then brings it back to zero while a transition is still running. Nothing between the increment and the enqueue may throw, because a leaked increment never comes back.
- **The decrement is in the `finally`**, so the transition test hook above the body is inside the same count.

The count is per handler, not per slot, so the drain waits for every transition this handler enqueued, including those on queues its snapshot never covered (see [Cost](#cost)).

### Why the count is re-read rather than signalled

A completion source has to cope with the count already being zero when the drain starts, with a transient zero before the drain's own stops land, and with a store-load reordering on both sides that `Volatile.Write` does not close. Re-reading has none of those cases. The cost is one poll interval per round, once per process, on a path that already spends 50 ms inside every stop it waits for.

**The second wait is why the barrier holds.** The count is read, not held, so an enqueue landing after the count first reached zero would otherwise be outside the barrier. A context detach decides its enqueues under the queue lock against step 6's releases, so each of its stops either landed ahead of the release for that slot, with its increment visible to the second read, or was refused; one more round is therefore the last that path can need. `DetachHostedService`, which enqueues without an ownership check, is not covered by this (see [What Has No Test Behind It](#what-has-no-test-behind-it)).

### Why the ownership decision is inside the queue lock

The drain snapshots `_stopOnDrain` holding nothing and enqueues afterwards, so ownership can move in between. `HostedServiceSlot.EnqueueIfOwnedAsync` therefore reads `Owner` and enqueues under one acquisition of the queue lock. Without it, a subject that leaves host 1's graph and joins host 2's before host 1's drain reaches its enqueue has host 2's instance stopped and disposed by host 1.

Because the enqueue can be refused, **a refused subject stop touches no signal, and an attachment stop is only ever handed a signal of its own handler's**. A refused enqueue has no body and no `finally`, so an attachment stop handed a signal nothing sets would park on it, stay counted, and burn the whole shutdown deadline. What guarantees every handed out signal is set is in [The stop signal](#the-stop-signal).

The same lock orders the drain against a take in flight: a drain that snapshots while a take holds the queue lock queues behind it and reads the ownership the take installed, so its stop lands behind the start. What that protects is the ownership: a repeat take that lands as the drain begins must leave the earlier install alone, or the drain's own enqueue reads a stranger, refuses, and the running instance survives shutdown. That is the `ownershipTaken` guard on the gate re-read's undo.

**The gate re-read's undo enqueues a stop rather than only untracking the slot.** The re-read fires after the start has been enqueued, and that start's body can have read the gate as `Open` a moment before `BeginDraining` and be past every guard. Untracking alone hides the instance it creates from any later snapshot, with ownership released so no detach can reach it either. The enqueued stop lands behind the committed start, and the count carries it. It is enqueued only while this handler still owns the slot: the undo runs on the attaching thread, so the whole drain can finish and another handler take the slot first, and an unconditional stop would then stop that handler's instance and create a stop signal for the wrong owner. Refusing loses nothing, because every release that can end this ownership first, a drain, a context detach, a faulted awaited attach or a faulted awaited activation, enqueues a stop behind the start before it releases. `WhenTheGateReReadUndoesATakeAnotherHostHasSinceTaken_ThenThatHostsInstanceSurvives` pins it.

**The undo of an attachment take waits for its subject's stop like every other attachment stop.** The undo releases the slot, so the drain's own ordered enqueue for it is refused, and the undo's stop is the only one that queue gets. It can be enqueued before the drain has enqueued the subject's stop, which is why the subject slot's signal exists on the asking side: the undo asks the subject slot, is handed the signal the subject's coming stop will take, and its stop waits there. Left unordered, an attachment attached to a running subject as the drain begins is disposed while that subject is still inside its own stop. `WhenTheGateReReadUndoesAnAttachmentTakeOnARunningSubject_ThenItsStopWaitsForTheSubjectsStop` pins it. The undo asks through the same `GetStopToAwait` helper as the context detach and the drain, so on a subject whose service is an activation it asks the activation slot instead, and `WhenTheGateReReadUndoesAnAttachmentTakeOnAnActivatedSubject_ThenItsStopWaitsForTheActivationsStop` pins that. The undo of a subject take or of an activation take needs nothing extra: its stop takes the signal like any stop on that slot, and the stops enqueued behind it afterwards, by the same context attach or by the drain, wait on it.

### Cost

A subject that hosts nothing never gets a slot and never reaches `EnqueueCore`, so it pays nothing. Two shapes get slower, and the second is a correctness gain being paid for:

- **A wedged queue the drain's snapshot never covered holds the count non zero**, so shutdown takes the full timeout instead of returning while an unrelated queue is about to touch a service provider the host is disposing.
- **A hosted service whose own stop mutates the graph** enqueues stops the snapshot never saw, and those are now waited for.

### The gate reads

Three places read the gate twice, once on entry and once after their write, for the reasons recorded at each read. What they protect is that a draining handler ends up owning nothing and rooting nothing.

- **`TryTakeOwnershipAndStart`** reads it on entry and re-reads after the take installed the owner and tracked the slot for the drain. The re-read undoes a take that landed after `BeginDraining`. The read on entry keeps a draining handler from installing an owner at all, because a live handler that reaches the slot between the install and the undo loses the exchange for good.
- **`AttachSubject`** reads it on entry and re-reads after writing `_liveSubjects`. These protect the liveness entry rather than the owner: an entry written after the drain cleared the set is one nothing removes, so the subject stays rooted on a dead handler.
- **`MarkLiveIfAttached`** is the same pair on the path of a subject that gains its first slot while already in the graph, protecting the same thing.

## Activation and Waiting for a Start

`SubjectActivation<T>` exists because a singleton nobody resolves is never constructed, never attached and never started, and `IHostedService` is the only hook the generic host offers for forcing that construction. Resolving the subject attaches it, to the private context of a `PrivateContextHost` without a context resolver or to the resolved context with one. The activation then starts that `PrivateContextHost`, or, in shared mode, opens the resolved context's handler, waits for the subject's own start and activates it. It never starts a subject outside a handler: shared mode without a handler throws, and without a context resolver a caller registered instance in no hosting graph gets a `PrivateContextHost` of its own. `PrivateContextHost` refuses a subject that is already in a tracked graph, and it checks only the subject itself: a child that the constructor or `configure` assigns and that is already in another tracked graph is pulled into the private context's lifecycle as well, without a throw. The activation resolves the handler before it looks at whether the subject is a hosted service or has one, so a plain subject reachable from two hosting enabled contexts fails host start on the same `TryGetService` throw as [the public paths](#the-lookup-precedes-the-mutation-on-all-five-entry-points). An execution fault is recorded by the handler and does not stop the host, because the host's `BackgroundServiceExceptionBehavior` lives on `HostOptions`, which Hosting does not reference.

`WaitForStartAsync` enqueues an empty transition on the same queue and awaits it; enqueuing never runs a body, so it completes only once the start ahead of it has run. It then rethrows the start's recorded fault, which preserves the `AddHostedService` guarantee that a failing subject aborts host startup. It reads the slot and never creates one, and never takes ownership, because a claim taken from a wait would never be released. Its false result is not licence for the caller to start the subject itself, which `SubjectActivation<T>` records at its call.

## The 50 ms Delay

The stop body delays 50 ms before touching the instance. The start body no longer does: it waits for a start deferral instead, which answers the same hazard rather than guessing at it.

The hazard is caller side. The generated context constructor attaches the subject last, so `new Car(context) { Name = "x" }`, deserialization, and `AddSubject`'s `configure` on that constructor path all assign after the attach fired and the start was enqueued. A start deferral is the constructing flow saying when it has finished. `AddSubject` opens one in shared mode, and so should an application's own configuration loading; a consumer constructing by hand gets no protection unless they open one too, which [the hosting documentation](../hosting.md#configuration-before-startup) states.

What remains on the stop side is a mitigation with no mechanism behind it, kept because removing it is a behaviour change of its own. It observes the stop's token, so once the shutdown deadline has passed a drain's stops skip it, while graph driven detaches, which pass `CancellationToken.None`, always wait it out.

## Start Deferrals

`HostedServiceStartDeferral` is ambient per context, held in an `AsyncLocal` on the handler and handed out by `IInterceptorSubjectContext.DeferHostedServiceStarts()`, which returns null when the context has no handler. The contract:

- **Capture is per execution flow, at enqueue time.** `TryTakeOwnershipAndStart` reads the ambient deferral in the enqueuing flow, beside the completion deferrals: the body runs later, and on the fire and forget paths in a flow that has already moved on.
- **A captured start waits for its own deferral and every deferral enclosing it**, which `HostedServiceStartDeferral.WaitAsync` walks. Disposal releases; there is nothing to call on success and no way to fail through the deferral.
- **The wait sits after every guard in the start body and before the fault is cleared**, and each guard is re-read after it, because the subject can leave the graph, ownership can move, and a competing start can install an instance while the deferral is open. A start that finds any of that declines and creates nothing.
- **The drain releases the wait too**, through `HostedServiceGate.WaitForDrainingAsync`. A parked start is already counted in flight, so a deferral nobody disposes would otherwise hold the barrier for the whole shutdown deadline.
- **Disposal order is the caller's discipline, not enforced.** Repeated, cross flow and out of order disposal do not throw and do not strand a start; what is undefined afterwards is only which later attaches the deferral still covers.
- **Nesting reaches into dependency injection in shared mode, deliberately.** An `AddSubject<T>` registration with a context resolver opens its own deferral on the resolved context with that context's ambient deferral as its parent, so a subject built while another deferral is open waits for both. An application loading a whole object graph can depend on that: one deferral held across the load, with a nested one per subject it deserializes, keeps any subject from starting before the rest of the graph exists. The cost is that a deferral held across `host.StartAsync()` reaches every such factory, which is [deadlock shape 5](#5-awaiting-a-captured-start-or-its-detach-inside-its-own-start-deferral). A registration without a resolver opens no deferral: it configures its subject before the subject joins its `PrivateContextHost`'s private context, whose handler no application deferral reaches, so a deferral the application holds neither defers nor deadlocks it.
- **The queue is shared between handlers and the deferral is not.** Two handlers over one subject enqueue on one queue while each has its own ambient deferral, so a start parked on one host's deferral silently delays the other host's start of that subject. Reachable only by moving a subject between two hosting enabled hosts in one process while one holds a deferral open; separating the queues is a larger change than the hazard is worth.
- **A parked start holds its slot's queue.** Anything ordered behind it waits for the deferral, including `WaitForStartAsync`'s empty transition and a detach's stop, so `DetachHostedServiceAsync` awaited while the deferral is open waits for the deferral. Bounded unless the waiting flow is the one holding the deferral, which is [deadlock shape 5](#5-awaiting-a-captured-start-or-its-detach-inside-its-own-start-deferral).
- **Every attributed transition body drops the ambient start deferral first**, through `HostedServiceHandler.ClearAmbientStartDeferral`. A body inherits the execution context of the flow that enqueued it, and a stop body does not wait for that flow's deferral, so an attach made from a stop path would otherwise be captured by an unrelated caller's deferral and park, holding the stop's queue while the stop awaits it.

## Deadlock Shapes Not Guarded Against

Per slot queues contain a self deadlock to one queue rather than removing it, and none of the five shapes below is detected at runtime. Each is a constraint on consumer code rather than a defect here, so each is stated rather than fixed: detecting them would mean tracking waits across queues. Shape 3 is the only one that occurs in practice, and it is complied with in tree and has a regression test. Shape 5 is an accepted limitation rather than a rule a consumer can simply follow, because the host reaches it without the consumer writing anything unusual, and `HostOptions.StartupTimeout` is what turns the hang into a failure.

[A handler that loses the exchange to a departing handler never retries](#a-handler-that-loses-the-exchange-to-a-departing-handler-never-retries) is not listed here because it is an internal window and a known defect, not a consumer shape.

### 1. An attachment whose own `StopAsync` detaches itself

The detach enqueues its stop on the queue the running stop occupies.

### 2. Two subjects whose services detach each other's attachments from their own stop paths

The same cycle across two queues rather than one.

### 3. A subject or its activated service detaching an attachment while unwinding

The subject's stop transition waits on the unwind, the unwind waits on the attachment queue, and the attachment queue's head waits on the subject's stop signal, which only the blocked subject transition can set:

1. The subject leaves the graph. `DetachSubject` enqueues the subject's stop, which takes the signal, and enqueues the attachment's stop, which first awaits it.
2. The subject's stop runs. `BackgroundService.StopAsync` awaits the execute task, and `ExecuteAsync` unwinds into a helper that awaits `DetachHostedServiceAsync` for the attachment.
3. That call enqueues its own stop on the attachment's queue, behind the stop from step 1, and awaits it.
4. The attachment's queue head is still awaiting the signal, which is set in the `finally` of the subject's stop, which cannot finish because it is still inside step 2.

The activated service of an `ISubjectHostedServiceFactory` subject forms the same cycle one level down: its stop takes the activation slot's signal, the stops of the subject's other attachments await it, and a detach of one of them from the service's own stop path or `ExecuteAsync` unwind queues behind a stop that is waiting for the service.

It is the shape any `BackgroundService` subject that owns a restartable attachment reaches for when it releases that attachment as its run loop unwinds, which is why such a subject's own stop should only cancel what it started, wait for its run loop and report itself stopped. The rule is stated in [the user documentation](../hosting.md#do-not-detach-from-your-own-stop-path), and `HostedServiceHandlerTests.WhenASubjectOwningAnAttachmentIsStoppedByTheHost_ThenShutdownCompletesWellInsideTheTimeout` is the regression guard. A wedged queue is unbounded in damage but bounded in blast radius: shutdown gives up on it at `ShutdownTimeout` and every other queue drains normally.

### 4. A startup completion that takes a lock of its own

`DeferStartupCompletion` calls `IStartupCompletion.Defer()` synchronously from `HandleLifecycleChange`, and the refused-enqueue path releases that completion deferral from the same place, both under `_attachedSubjects`. A startup completion that takes a lock of its own therefore joins that lock's order:

1. Thread A takes the startup completion's own lock `L`, then attaches a hosted service. `AttachHostedService` and `AttachHostedServiceAsync` take `_attachedSubjects` themselves, through `MarkLiveIfAttached`, and awaiting a transition whose body writes a subject typed property needs it too.
2. Thread B holds `_attachedSubjects` for an unrelated graph write, reaches `HandleLifecycleChange`, and calls `Defer()` on that same startup completion. It blocks on `L`.

Nothing resolves it, and the blast radius is the whole process rather than one queue: `B` holds `_attachedSubjects`, so every structural property write in the graph queues behind it.

**The call site is accepted rather than fixed, and it is not by itself the deadlock.** The completion deferral must be taken before the enqueue completes, and on the lifecycle driven path the event arrives already inside `_attachedSubjects`, so there is no earlier point to take it. Any alternative that keeps the guarantee either calls `Defer` from the same place or needs a new cross package protocol between Hosting and Connectors.

Step 2 is the only step a startup completion supplies, so a startup completion that never blocks there cannot take part. The constraint is stated on [`IStartupCompletion`](../../src/Namotion.Interceptor.Tracking/IStartupCompletion.cs), where an implementer meets it, and the exposure is per implementation rather than per consumer. `SourceMonitor`, the only implementation in this repository, follows it: deferring is an `Interlocked.Increment`, and releasing the completion deferral takes the monitor's `_lock` in an order its `DeferWaitCompletion` remarks fix, held because nothing under `_lock` waits on anything that needs `_attachedSubjects` (the graph walk in `IsBranchSynchronized` reads parent sets, and completing a wait uses `RunContinuationsAsynchronously`).

### 5. Awaiting a captured start, or its detach, inside its own start deferral

The flow holds the deferral open, the start is parked on it, and the flow waits for something ordered behind that start: the start itself, or a detach whose stop is enqueued on the same queue. Only that flow can dispose the deferral, and it cannot get there.

`host.StartAsync()` is the instance that does not look like one, in shared mode only. A subject registered with `AddSubject<T>` and a context resolver has its start captured by the enclosing deferral through the factory's nested deferral, `SubjectActivation<T>` waits for that start, and for an `ISubjectHostedServiceFactory` subject `ActivateHostedServiceAsync` is one more waiter on a start the same deferral captured. The host waits for the activation, so the scope is disposed only after the call it is blocking returns. Nothing bounds it unless the application sets `HostOptions.StartupTimeout`. A self-contained registration is outside the shape, because it opens no scope and no application scope reaches its private handler. Stated for consumers in [Configuration Before Startup](../hosting.md#configuration-before-startup).

### Disposal from a handler transition

The handler disposes what it created from a transition that can run while a detach cascade still holds `_attachedSubjects`, so a connector's dispose path and that lock interleave. Nothing enforces the resulting constraint and no test covers it.

The concrete case is `SourceOwnershipManager`, and every consumer of it inherits it, the OPC UA client source included; whether any of them carries a second lock with the same shape has not been established. Its `Dispose` and its `SubjectDetaching` handler both take its own lock and invoke `onReleasing` inside it, and `SubjectDetaching` runs inside `_attachedSubjects`. That fixes the order `_attachedSubjects` then the manager's lock. A dispose from a handler transition takes the manager's lock without `_attachedSubjects`, and the order reverses the moment anything on that path enters `_attachedSubjects`: an `onReleasing` callback that writes a subject typed property, or attaches or detaches a subject.

The consumer rule is in [the user documentation](../hosting.md#keep-the-dispose-path-out-of-the-lifecycle-lock). `LifecycleInterceptor.WriteProperty` takes the lock only when the property type can contain subjects, which is why a scalar write from a dispose path is harmless and a subject typed or collection typed one is not.

## What Has No Test Behind It

Each of these leaves the suite green when removed or changed. They are listed so that silence is not mistaken for coverage.

- **`MarkDetached` landing before its own `EnqueueAttachmentStop`.** The interleaving where a mark lands after its stop, letting an attach that already snapshotted the attachments enqueue a start behind that stop, needs a seam that fires after the removal returns; `Models/DataGatedSubject` fires on the removal's own read and cannot reach it. The faulted path of `AttachHostedServiceAsync` shares the gap.
- **Every transition on `HostedServiceSlot` staying one write.** A shape rather than a guard; the pair is one load, so no seam sits between the reads, and a seam in a body drives the writer while the reader is the side that must observe the gap.
- **`GetState` taking one snapshot load.** A second load of the same field is only observable with a seam inside the method, which is the surface the single field design removed.
- **The consumer reading rules in `SingleAttachmentHost.UpdateFromAttachment`**: the fault read before the state, each drop taking its reading immediately before dropping, and the drop guard on the fault branch. The windows they close need a retry start to publish between two adjacent reads, which the suite does not drive. The not running branch's guard is pinned by `OpcUaClientTests.WhenAReconciliationLandsInsideAReAttach_ThenTheReAttachStillProducesARunningClient`.
- **Dropping on `Stopping` in `UpdateFromAttachment`.** Holding the drop back for a stop in flight as well would also leave the suite green. Dropping is a decision: an instance that has left `Current` is being stopped and disposed, so what it published is the wrapper's to drop, where a start in flight may be publishing into the tree.
- **The stop's `finally` condition.** Decides nothing for the body as it stands; kept for a future guard above the instance read.
- **The `Volatile` qualifiers on `_snapshot`, `_fault`, `_startFault` and `_detached`.** x64 already orders the loads and stores; what they prevent is the JIT keeping a polled read in a register and reordering on weaker memory models.
- **The `Interlocked.MemoryBarrier` in `TryResolveHandlerAfterPublish`.** The reordering needs two threads and a machine that takes it; the gated tests drive both sides on one thread.
- **The in flight increment for a stop on a slot an explicit detach untracked for the drain.** The drain's snapshot does not contain that slot, so only the enqueue's own increment holds the stop inside the barrier.
- **A stop enqueued for a slot this handler never took**, which only `DetachHostedService` can produce, landing after the drain's second wait. Benign today because two reachable hosting contexts make the lookup throw, so the enqueuing handler is the draining one. Reasoned, not demonstrated.
- **Not clearing `_stopOnDrain` at the end of the drain.** An equivalent mutant as far as anything can observe.
- **`AttachHostedServiceAsync` reading `StartFault` rather than `Fault`.** Nothing parks the caller between its start and the fault transition. The `WaitForStartAsync` half is pinned by `BackgroundServiceExecutionTests.WhenAHostedSubjectsRunFaulted_ThenWaitingForItsStartReturnsFalseWithoutThrowing`.
- **The gate read on entry in `AttachSubject` and in `MarkLiveIfAttached`.** A narrowing the re-read absorbs; the window is between two adjacent statements.
- **Two overlapping drains.** Legal through the gate's ratchet, sequential in practice.
- **A loser of the activation entry waiting for the winner's publish.** The wait in `GetOrAddActivation` needs a second thread parked between its reservation and its publish; the gated activation tests drive the context entry into that window on one thread, where the handler's read-only lookup finds the reservation and never waits.
- **The remove-to-mark gap on an explicit detach of the activation.** An activation reading the slot between `RemoveAttachment` and `MarkDetached` returns the attachment being detached. Acceptable rather than closed: the two calls linearize as activate-then-detach, and the next activation reserves a fresh one.

Two mutations are **equivalent** rather than untested, because the difference is unreachable:

- **Tracking for the drain on a repeat take as well as on the install.** A repeat take differs only where the slot is untracked while the owner is this handler, which only the two explicit detach overloads produce, and both mark the slot detached first, so every later take refuses before the exchange.
- **Moving the decrement from `RunAsync`'s `finally` to the end of its `try`.** The unfiltered `catch` makes the two positions reachable under identical conditions; the `finally` is defence in depth against a body that stops obeying "bodies never throw".

## Invariants

Once lifecycle events and user driven attach and detach calls have settled:

1. **One instance per slot.** A slot holds at most one non null `Current`, and only a start body ever sets it.
2. **In the graph implies running, out of the graph implies stopped.** Each queue drains in enqueue order, so every slot ends in the state its last event demanded. A run fault is the one exception: the slot is stopped and reads `Faulted` while its subject is still in the graph, until the next context attach retries it. Queue order alone does not carry this, because an attach racing the subject's context entry can leave a slot neither side enqueued anything for; [the attach resolving the handler a second time](#an-attach-and-a-context-entry-are-the-same-two-facts-in-opposite-orders) closes that. Execution across slots is concurrent, so this is quiescent consistency rather than a moment by moment guarantee.
3. **Created implies disposed by the same owner.** The handler disposes exactly the instances it created through a factory, once, and never disposes a subject.
4. **A subject's stop precedes the disposal of its own attachments**, on both the context detach path and the shutdown path, whenever that stop is not cancelled.
5. **No transition body runs under `_attachedSubjects`.** Every lifecycle driven action is an enqueue, and an enqueue never runs a body. User code still does: `Defer` and releasing that completion deferral on the refused enqueue path, which is [deadlock shape 4](#4-a-startup-completion-that-takes-a-lock-of-its-own).
6. **A drained handler roots nothing.** It clears its liveness set and releases every slot its snapshot held, which untracks that slot for the drain. Any slot tracked after the snapshot belongs to an install whose own gate re-read releases it, so `_stopOnDrain` is empty once things have settled rather than at the moment `StopAsync` returns. The slots on the subjects are left for the next handler.
