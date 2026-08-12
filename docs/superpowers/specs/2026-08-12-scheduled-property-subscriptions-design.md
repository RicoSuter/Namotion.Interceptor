# Scheduled per-property change subscriptions

Status: designed, not implemented.

Revision 3.

Revision 1 built the scheduled path on Rx `ObserveOn` and was reviewed twice, once adversarially against the design and once fact-checking every claim against the tree and against decompiled System.Reactive 6.1.0. Between them the reviews found one wrong performance claim that inverted the central build-versus-compose decision, four defects inherent to `ObserveOn`, and six errors or omissions in the semantics. The dispatch mechanism changed as a result; the API shape did not.

Revision 2 introduced a hand-rolled dispatch protocol, which a third review then attacked with probes on arm64. It found four severe defects in that protocol: an unbounded drain that held a scheduler thread until production stopped and starved sibling subscriptions outright, a counter settle outside any `finally` (the exact defect the document convicts `ObserveOn` of), a fault path covering only half the scheduler-failure space, and a fault-versus-dispose race that could double-release the process-wide gate and silently disable per-property delivery for the entire host. It also showed that the allocation argument used to justify hand-rolling does not hold in this API's own motivating shape. The protocol is rewritten below and the performance section now states both regimes honestly; the decision to own the dispatcher stands on the three correctness properties rather than on allocations.

Arguments the reviews made that are answered rather than accepted appear under "Synchronous schedulers are rejected" and "Sequencing", so the next reader does not relitigate them. Measured figures throughout come from probes against System.Reactive 6.1.0 on .NET 9 and osx-arm64.

Line citations were verified against the tree at the time of writing. Freeze the file before implementation, since they drift.

## Problem

Per-property subscriptions (`docs/tracking.md:110`) deliver synchronously, on the writing thread, possibly concurrently, and inside the write. `IPropertyChangeObserver` therefore requires its implementations to be thread-safe, fast, non-blocking, and to never throw (`IPropertyChangeObserver.cs:5-7`), and a throwing observer propagates out of the setter and suppresses later deliveries for that write (`docs/tracking.md:145`).

Those four constraints are correct for the channel but wrong for a large class of consumers. An observer that pushes a value to a device, writes a row, or updates a UI cannot honour them. Today such a consumer has to build its own bridge from the per-property callback to a scheduler.

A real consumer did exactly that, in a separate product repository, and the result shows what the library currently forces people to rediscover. That implementation routed changes through an intermediate `Subject<T>` that it deliberately never disposed (with a `CA2000` suppression to match), documented "handler must not throw, an exception ends delivery permanently and silently" as a caller obligation rather than removing the hazard, named the method `SubscribeOn` when the semantics are `ObserveOn`, and passed the 144 byte `SubjectPropertyChange` by value through four copies.

Two of those are traps the library's own design creates and then leaves in consumer code:

1. **Thread per subscription.** `ObserveOn` dedicates a private thread to every subscription that signals when the scheduler advertises `ISchedulerLongRunning`, which both `Scheduler.Default` and `TaskPoolScheduler` do (verified: `AsLongRunning()` is non-null for both; `ObserveOnObserverLongRunning.Schedule` starts the drain thread on the first signal, which is why "that signals" is the precise wording). Per-property subscriptions exist precisely to be used one per property, so a model with thousands of properties detonates.
2. **Process death on a throwing handler.** An exception reaching an `ObserveOn` sink escapes the scheduler work item, and `UserWorkItem.Run` has no try-catch above a bare `ThreadPool.QueueUserWorkItem`, so on `DefaultScheduler` the process terminates.

## Why this belongs in the library

The library adds no capability here. Everything is reachable from the released `Subscribe(PropertyReference, PropertyChangeCallback)` plus Rx, in roughly forty lines, leak free. What is at stake is which side of the boundary owns the traps.

A narrower option was considered and rejected: ship only the observable primitive and let consumers compose scheduling. It fails on an inversion. The observable is the piece with exactly one correct implementation and no way to get it wrong, while the scheduling and error isolation are the pieces that are easy to get wrong. Shipping only the observable hands the library the safe part and leaves both dangerous parts at every call site, in a library whose design actively encourages many small per-property subscriptions.

Doing nothing and documenting the pattern was also considered. It has the same inversion and additionally requires every consumer to copy the same file.

## Decisions

### Three layers, each a faithful superset of the one below

| Layer | API | Thread | Serialized | Observer may throw |
|---|---|---|---|---|
| 0 (released) | `property.Subscribe(callback)` | writer | no | no, propagates into the write |
| 1 (new) | `property.GetSynchronousChangeObservable()` | writer | no | no, propagates into the write |
| 2 (new) | `Subscribe(callback, scheduler, onError)` | scheduler | per subscription | yes, isolated |

### Layer 1 is a faithful adapter, not a safer one

`GetSynchronousChangeObservable()` has layer 0's contract exactly: synchronous, on the writing thread, possibly concurrent, and a throwing handler propagates back into the setter. It is the same channel wearing an `IObservable<T>`, and that is the point, since an Rx adapter that silently changed delivery semantics would be harder to reason about than one that does not. The hazard is that "it is Rx now, so it must be safe" is the assumption people will make, so the remarks block states the inheritance explicitly.

`PropertyChangeObservable` implements `IObservable<T>` directly rather than deriving from `ObservableBase<T>`. `ObservableBase<T>.Subscribe` unconditionally wraps in `AutoDetachObserver<T>`, whose `OnNextCore` disposes the subscription when the handler throws and then rethrows, which would diverge from layer 0 for no benefit. `ObservableExtensions.Subscribe(source, Action<T>)` passes a bare `AnonymousObserver<T>` through, and `ObserverBase<T>.OnNext` has no try-catch, so a directly implemented observable propagates the throw to the writer and keeps the subscription alive. The same safeguard wrapping also lives in `Producer<T, TSink>.Subscribe`, so layer 1 escapes it specifically by being neither an `ObservableBase<T>` nor a `Producer`.

It stays `internal`; only the extension method is public. Each call returns a fresh instance, so two calls on the same property are not reference-equal and nothing may key observables by identity.

### Naming: `GetSynchronousChangeObservable`

The qualifier separates two APIs that return `IObservable<SubjectPropertyChange>` from similar-looking calls with opposite defaults: the context-level `GetPropertyChangeObservable` (`InterceptorSubjectContextExtensions.cs:109`) reschedules onto `Scheduler.Default` unless told otherwise, while this one never leaves the writer. A name that differed arbitrarily (`GetChangeObservable`, considered in revision 1) would signal nothing about the difference, and the difference is the trap.

Accepted cost: the qualifier is locally informative rather than a systematic taxonomy. The context-level observable is also synchronous when passed `ImmediateScheduler.Instance`, which the benchmarks do, and layer 0's `Subscribe(callback)` is equally synchronous while carrying no qualifier. `Subscribe` needs none, because its scheduled sibling is separated by a required extra argument rather than by being the same call with a different default.

### The dispatcher is hand-rolled, not `ObserveOn`

Revision 1 composed `ObserveOn(scheduler.DisableOptimizations(typeof(ISchedulerLongRunning)))`. That is rejected. `DisableOptimizations` is required to avoid trap 1, and forcing the short-running sink is what makes the rest fail: `ObserveOnObserverNew.DrainShortRunning` emits exactly one item per scheduled work item and reschedules itself for each remaining item, as its own XML doc states. Measured at 1000 items, 1000 scheduler calls, saturated or not.

Allocation measured over 20 000 items with `GC.GetTotalAllocatedBytes(precise: true)`:

| configuration | bytes per change |
|---|---|
| `Scheduler.Default.DisableOptimizations(ISchedulerLongRunning)`, revision 1's exact config | 293.5 |
| `EventLoopScheduler`, no wrapper | 109.3 |
| `Scheduler.Default` long-running path, the one revision 1 disables | 13.3 |

The cost comes from `DefaultScheduler.Schedule` allocating a `UserWorkItem` plus a concurrency-abstraction-layer work item on every change, and from `SchedulerWrapper.Wrap` allocating a fresh closure and delegate on every `Schedule` call. Revision 1 claimed this "amortizes to near zero under sustained load", which is the opposite of what it costs.

**This table is not on its own a reason to hand-roll, and revision 2 over-read it.** Measured in the shape this API actually targets, one change per burst, the hand-rolled dispatcher costs 120 to 200 bytes per change, which is parity with `ObserveOn(EventLoopScheduler)`. The honest reading is narrower: the `DisableOptimizations` path is the worst of the three and the long-running path that beats everything is unavailable, because it dedicates a thread per signalling subscription. See Performance for the full two-regime accounting.

Three further defects are inherent to `ObserveOn` and not fixable from outside it:

- **A throwing scheduler propagates into the writer and then wedges the subscription permanently.** `ObserveOnObserverNew.OnNext` calls `Schedule` unguarded, so a caller-owned `EventLoopScheduler` disposed before the subscription throws `ObjectDisposedException` on the writer thread inside the setter. That escapes `PropertyChangeSubscription.Dispatch` (`:133-140`) and `PropertyChangeInterceptor.WriteProperty` (`:203`), suppressing every later per-property listener on that write. Worse, `_wip` was already incremented with no drain scheduled, and its `Interlocked.Decrement` is not in a `finally`, so the counter stays pinned above zero, no drain is ever scheduled again, and the queue grows forever. `onError` never fires.
- **Four by-value copies** of the 144 byte struct per delivery: into the queue, out of the queue, through `ForwardOnNext`, and into the `AnonymousObserver` lambda. `in` is only restored at the final `OnChange`. Revision 1 claimed one, while opening by criticising the consumer implementation for four.
- **The returned `IDisposable` is a live Rx internal.** It is an `ObserveOnObserverNew<T>`, which casts to `IObserver<SubjectPropertyChange>`, letting a consumer inject changes the model never produced or call `OnCompleted` to kill the subscription without disposing the handle. An Rx version bump can change its behaviour with no signature change and no snapshot diff.

Owning the dispatcher is justified by those three, not by the table: failure handling we control instead of a permanent wedge, the change handed to the observer by reference, and our own handle type. The precedent in this codebase is real but weaker than "same shape": `PropertyChangeQueueSubscription` is a pull model with a `ConcurrentQueue` and a non-counting reset-and-recheck signal, no work-in-progress counter, no drain ownership, and no scheduling. What carries over is the practice of writing the lost-wakeup argument down, not the protocol.

The cost of owning it is a concurrency protocol the library must keep correct forever. The first draft of that protocol had four defects, all found by review and all fixed below, which is the argument for the verification plan rather than against the decision.

### The dispatch protocol

A counting work-in-progress field rather than a flag, which removes the classic empty-check-then-release lost wakeup by construction:

```csharp
// Enqueue: writer thread, inside dispatch, outside the subject lock
// _state is Live | Disposed | Faulted, one-shot out of Live; both non-Live states stop acceptance,
// so a faulted subscription cannot keep incrementing a counter no drain will service.
if (Volatile.Read(ref _state) != Live) return;
_queue.Enqueue(change);
if (Interlocked.Increment(ref _wip) == 1)
    ScheduleDrain();

// Drain: pool or scheduler thread, at most one active, bounded then handed off
var processed = 0;
try
{
    var pending = Volatile.Read(ref _wip);
    while (processed < pending && processed < MaxBatch)
    {
        if (Volatile.Read(ref _state) != Live) return;
        if (!_queue.TryDequeue(out var change)) break;
        processed++;                 // counts the dequeue, not the delivery
        Deliver(in change);
    }
}
finally
{
    if (Interlocked.Add(ref _wip, -processed) != 0 && Volatile.Read(ref _state) == Live)
        ScheduleDrain();             // budget spent, or something escaped Deliver
}
```

Three properties carry the design, and each replaces a defect found in the unbounded, `finally`-less draft this section used to contain.

**At most one drain is active.** Only the zero-to-one transition in enqueue schedules, and only a settling drain that observed a non-zero result reschedules. Those cannot both fire: if the settle returned non-zero the counter never reached zero, so no enqueue can see the zero-to-one transition; if it returned zero the drain does not reschedule and the next enqueue does.

**The drain always yields.** It processes at most `MaxBatch` items and then hands off through a fresh work item rather than looping until the counter empties. Without this, a subscription whose writer outruns its observer holds its scheduler thread for as long as production continues. Measured on the unbounded draft with the pool capped at four workers and eight sustained writers: two of eight subscriptions delivered nothing at all in three seconds while their queues grew past a hundred million entries, and unrelated thread pool work saw start latencies up to 79 ms. That is the trap this design exists to avoid, in a worse form than `ObserveOn` has it, because a held pool thread starves siblings while a dedicated thread does not. `MaxBatch` is 1024: measured over 20 000 saturated changes it costs 20 scheduler calls against 312 for a budget of 64, at the same bytes per change.

**The counter always settles.** The subtraction is in a `finally`, and `processed` counts the dequeue rather than the delivery, so an escape from `Deliver` leaves the counter consistent with the queue and the handoff picks the remainder up. `Deliver` wraps the observer call and routes a throw to `onError`, which is itself wrapped and swallowed, so an escape should be unreachable. The `finally` is there because the draft relied on that reasoning instead, which is the same "it cannot throw" assumption this document refuses to make when it declines to log.

The `_queue.Enqueue` before `Interlocked.Increment` ordering is load-bearing for liveness rather than correctness. With it, `TryDequeue` never failed while the counter was positive across 400 000 items and eight writers, because `ConcurrentQueueSegment.TryDequeue` spins on a reserved-but-unpublished slot instead of reporting empty. Reversed, the same run produced 7 622 spurious inner-loop exits, each becoming a handoff that finds nothing. Nothing is lost either way; the `break` above is defensive and self-correcting, since an under-subtracted counter simply reschedules.

`Volatile.Read(ref _wip)` at drain entry needs no acquire pairing for item visibility, which `ConcurrentQueue` provides. It is a count hint, and the settling `Interlocked.Add` is what makes a stale read safe. Every cross-thread transition rides an interlocked read-modify-write on that single field, which .NET emits as sequentially consistent on arm64; the protocol was probed on Apple silicon.

`_wip` and `processed` are `int`. Two billion undelivered changes is around 300 GB, so `long` buys nothing and would inherit the 64-bit atomicity argument `PropertyChangeSubscriptions.cs:16-21` had to write a paragraph for.

### Faulting is one-shot and shares its flag with disposal

`ScheduleDrain` must not let an exception from the scheduler reach the writer. It catches, reports to `onError`, and moves `_state` to `Faulted`, which stops acceptance and releases the upstream subscription by calling `PropertyChangeSubscription.Dispose()`. That method is already one-shot (`PropertyChangeSubscription.cs:76`), and the fault path never touches `PropertyChangeSubscriptions` directly.

This matters more than it looks. `_state` is a single field with one-shot transitions out of `Live`, because a writer parked between its own state check and its increment can still enqueue and still schedule after `Dispose` has returned. Delivery is correctly suppressed by the in-loop check, so no change escapes, but that late `ScheduleDrain` can throw and run the fault path after `Dispose` already released. If fault and dispose each decremented the process-wide gate, the count would reach zero with live subscriptions elsewhere, `PropertyChangeInterceptor.ResolveListeners` (`:274`) would return null for every per-property subscription in the process, and all per-property delivery in the host would silently stop. Routing both through the one-shot upstream `Dispose` makes double release unreachable. The same race class is already acknowledged in this codebase, where `PropertyChangeQueueSubscription.cs:145` deliberately leaks its signal because "a concurrent producer may still call `_signal.Set()` after its `_completed` check".

The counter is left unsettled on the disposal return path. That is safe not because nothing enqueues afterwards, which is false, but because nothing ever reads it again once `_state` leaves `Live`.

**Faulting covers only half the scheduler-failure space, and the spec says so rather than claiming otherwise.** `ScheduleDrain` runs only on the zero-to-one transition and on handoff, so it reports when a `Schedule` call throws. If a `Schedule` call instead succeeds and its work item never runs, which is what happens when an `EventLoopScheduler` is disposed while a drain is already queued behind a parked work item, the counter never returns to zero, no further `ScheduleDrain` is reached, and nothing is reported. Probed: zero deliveries, zero `onError`, unbounded growth. There is no cheap liveness escape that does not add a timer per subscription, so this is documented as a caller obligation instead: dispose subscriptions before the scheduler they run on.

### `ExecutionContext` flow is suppressed

Every scheduler Rx ships flows the writer's `ExecutionContext` to the delivery callback, verified for `Scheduler.Default`, `TaskPoolScheduler`, and `EventLoopScheduler`, which are the ones this overload accepts. Because scheduling happens from inside the write, the observer would then see the writer's ambient `AsyncLocal` state, and a single drain batch would run every queued change under whichever writer enqueued first.

This library has two ambient `AsyncLocal`s that this corrupts:

- `SubjectTransaction.CurrentTransaction` (`Transactions/SubjectTransaction.cs:13`). A commit replays writes, the drain inherits the committing transaction, the commit finishes and `Dispose` returns `_pendingChanges` to a shared pool. An observer that writes a property on the drain thread then takes the capture branch and mutates a dictionary already returned to the pool and possibly rented by a different live transaction. The observer's write is lost and can surface inside somebody else's commit.
- `ReadPropertyRecorder._activeScopes` (`Recorder/ReadPropertyRecorder.cs:14`). Property reads made by the observer, including `change.GetCurrentValue<T>()` which this design recommends as the staleness remedy, get recorded into a foreign render scope.

Derived-property tracking is unaffected: `DerivedPropertyChangeHandler`'s recorder and `SubjectChangeContext` are `[ThreadStatic]`, not `AsyncLocal`.

`ScheduleDrain` therefore wraps its `scheduler.Schedule` call in `using (ExecutionContext.SuppressFlow())`. Since `scheduler` is required there is no second dispatch path to keep in step.

Suppression costs `Activity.Current`, logger scopes, and consumer-owned `AsyncLocal`s, which do not reach observers. That is a forced choice rather than a preference, because the obvious alternative is worse than absence. A single drain batch delivers changes from many writers under whichever writer enqueued first, so letting the context flow attributes a device-write span to an unrelated property write and stamps one request's correlation ID onto another's changes. Absent context is uninformative; flowed context is wrong, and wrong is harder to debug. Suppression is also consistent with the queue channel, where the consumer already runs under its own context and never sees the writer's.

The only correct way to preserve ambient context is to capture it per change rather than per batch, which is recorded under Scope boundaries rather than built now.

**Suppression is not complete, and the limit is documented.** It governs the context a work item carries, not the context a scheduler's worker thread was born with. `EventLoopScheduler` creates its thread on the first `Schedule` call and `Thread.Start` flows the caller's `ExecutionContext` for that thread's whole lifetime, so a scheduler warmed up by someone else, or shared between subscriptions, exposes observers to whatever ambient state existed at creation, frozen and therefore wrong. Probed: a thread created under an ambient value still sees it from a suppressed later schedule. If that thread was created inside a `SubjectTransaction`, the pooled `_pendingChanges` corruption above is reachable despite suppression. The XML docs say to give a subscription its own scheduler, or to use `Scheduler.Default`, which captures per work item and where suppression is complete.

Suppression costs about 80 bytes per delivery whenever the writer thread carries any `AsyncLocal` at all, from two shallow `ExecutionContext` clones, and nothing when it carries none. `SubjectTransaction.CurrentTransaction` is an `AsyncLocal` and an ASP.NET or `Activity` host sets several, so the industrial case is the paying one. Batching reduces the count of paying schedules, since handoffs issued from the drain thread run clean.

The same defect exists today on `GetPropertyChangeObservable(scheduler)` (`InterceptorSubjectContextExtensions.cs:124`). Fixing it there is out of scope and becomes a follow-up issue, because it changes behaviour for an already-released API and deserves its own decision.

### Synchronous schedulers are rejected

`ImmediateScheduler.Instance` and `CurrentThreadScheduler.Instance` are rejected with an `ArgumentException` naming layer 0 as the synchronous option.

A review argued for documenting instead. Measured behaviour is worse than revision 1 described and worse than documentation can carry. With two writer threads and 5 000 pushes each, all 10 000 deliveries ran on one thread: the thread that wins the counter race stays inside its own setter draining the other writer's production for as long as production continues, so its setter latency is unbounded and proportional to every writer's throughput. Separately, under `WithTransactions` the commit replay dispatches while holding the exclusive transaction lock, so a synchronous drain runs consumer code under that lock and an observer that starts a transaction on the same context deadlocks. An overload whose entire purpose is off-writer delivery should not silently do the opposite.

Nothing in the repository breaks. A sweep of `src/`, including tests, benchmarks, samples and HomeBlaze, found the only `IScheduler` in product code is `GetPropertyChangeObservable` itself, and every synchronous-scheduler call site (around sixty `ImmediateScheduler.Instance` in `Namotion.Interceptor.Connectors.Tests`, four `Scheduler.Immediate` in `SubjectTransactionPropertyTests`) targets that unchanged API. `Scheduler.Immediate` and `Scheduler.CurrentThread` are reference-equal to the singletons, so both spellings are caught.

Accepted limit, stated in the XML docs: only the two singletons are detectable. Any scheduler that runs actions inline has the same hazard, and that includes wrappers over the singletons, since `Scheduler.Immediate.DisableOptimizations()` still runs inline and is no longer reference-equal. `DisableOptimizations` is a call this very document teaches, so a reader following its own narrative can reach an undetected inline scheduler. The guard catches the realistic mistake without claiming to be complete.

This deliberately diverges from the sibling API, where `docs/tracking.md:140` teaches `ImmediateScheduler.Instance` as the way to get synchronous delivery and around sixty tests do exactly that. Recorded here for the same reason as the `scheduler` optionality divergence, so a later reviewer does not harmonise them.

The deterministic-test argument for allowing them does not apply, because the test plan introduces a controllable scheduler of its own.

### `onError` is optional and swallows by default

Making it required was considered and rejected on two grounds. Mandatory ceremony on the safe path pushes people onto the unsafe one, since raw Rx composition next to it demands nothing and lands back on both traps. And nothing else in this library requires an error handler: the queue channel leaves consumer exceptions to the consumer's own thread, and `GetPropertyChangeObservable` lets a throwing subscriber take out the scheduler work item.

The default swallows rather than logs. `Namotion.Interceptor.Tracking` references only System.Reactive and the core project, with no `ILogger` anywhere in it or in the core, so logging would mean a new dependency to service one error path. Independently, a handler that throws on every change of a hot property would emit thousands of entries per second, turning one broken handler into an outage of the logging pipeline. Swallowing keeps the blast radius at the handler and lets callers opt into logging.

The `onError` contract is stated on four axes that revision 1 left open:

- **It must not throw.** An exception from it is caught and swallowed, because the alternative is process death caused by the failure of the thing added to observe failures.
- **It can run after `Dispose` returns**, because an in-flight delivery finishes. A handler writing into caller-owned disposable state must tolerate a late call.
- **It is serialized per subscription, not per delegate.** A delegate shared across subscriptions is invoked concurrently.
- **It can run synchronously on the writer thread, inside the setter, under the transaction commit lock.** The scheduler-failure path reports from `ScheduleDrain`, which runs inside dispatch. So `onError` must not write properties, start a transaction, or block. The obvious handler, marking a device unhealthy by writing a property, deadlocks under `WithTransactions`, which is the same hazard used above to reject synchronous schedulers.

The diagnostic consequence of the default is documented: with no handler, a permanently throwing observer is invisible, because it keeps firing and keeps failing rather than stopping.

### Serialization is per subscription, not per observer

Revision 1 said the observer "is never invoked concurrently with itself and needs no synchronization of its own". That is false for the shape this design exists to serve. The counter is per subscription, and one adapter object subscribed across many properties overlaps freely; a probe measured 14 overlapping invocations across two subscriptions sharing an observer.

The guarantee is therefore stated as: within a single subscription the observer is never re-entered; an observer instance, callback closure, or `onError` delegate shared across several subscriptions is still invoked concurrently and must synchronize.

### The handle is a public named type

The scheduled overloads return a `public sealed class ScheduledPropertySubscription : IDisposable` rather than a bare `IDisposable`. This follows the queue channel, where `CreatePropertyChangeQueueSubscription` already returns the concrete public `PropertyChangeQueueSubscription` carrying a public `Count`.

Three reasons. It stops Rx internals escaping through a public API, which `ObserveOn` did by handing back a sink castable to `IObserver<SubjectPropertyChange>` that a consumer could use to inject changes the model never produced or to complete the sequence without disposing. It gives the faulted state somewhere to live. And it leaves room to evolve without a new overload, which a bare `IDisposable` does not.

It carries `PendingCount`, the number of accepted changes not yet delivered, exact only when read from a quiescent state for the same reason `PropertyChangeQueueSubscription.Count` is. The queue is unbounded by decision, so this is what makes growth observable instead of silent; a consumer on a hot property can watch it rather than discovering the backlog through memory pressure. Adding a bound with a drop policy stays deferred alongside conflation, and `PendingCount` is what a consumer would drive that decision from.

### `scheduler` stays required, and evolution is by overload only

Owning the dispatcher makes a no-scheduler asynchronous overload natural, and it is deliberately not added: `Subscribe(cb)` and `Subscribe(cb, onError)` would look near-identical at a call site while having opposite delivery semantics. Requiring `scheduler` keeps the two-argument and three-argument overload sets disjoint, which is also what removes any overload ambiguity.

This diverges from `GetPropertyChangeObservable`, where `scheduler` is optional. The divergence is deliberate and recorded here so a later reviewer does not "fix" it.

Because the package ships on NuGet, adding any further parameter later, even an optional one, would break already-compiled callers. These members evolve by new overload only.

## API surface

All in `PropertyChangeSubscriptionExtensions`.

```csharp
IObservable<SubjectPropertyChange> GetSynchronousChangeObservable(this PropertyReference property);

ScheduledPropertySubscription Subscribe(this PropertyReference property,
    IPropertyChangeObserver observer, IScheduler scheduler, Action<Exception>? onError = null);

ScheduledPropertySubscription Subscribe(this PropertyReference property,
    PropertyChangeCallback callback, IScheduler scheduler, Action<Exception>? onError = null);

ScheduledPropertySubscription SubscribeToProperty<TSubject, TValue>(this TSubject subject,
    Expression<Func<TSubject, TValue>> propertySelector,
    IPropertyChangeObserver observer, IScheduler scheduler, Action<Exception>? onError = null)
    where TSubject : IInterceptorSubject;

ScheduledPropertySubscription SubscribeToProperty<TSubject, TValue>(this TSubject subject,
    Expression<Func<TSubject, TValue>> propertySelector,
    PropertyChangeCallback callback, IScheduler scheduler, Action<Exception>? onError = null)
    where TSubject : IInterceptorSubject;

public sealed class ScheduledPropertySubscription : IDisposable
{
    public int PendingCount { get; }
    public void Dispose();
}
```

The `SubscribeToProperty` overloads exist because that is the entry point the docs lead with (`docs/tracking.md:114`). Without them, a caller who wants scheduled delivery has to abandon the typed selector and construct a `PropertyReference` by hand.

Overload resolution was checked and is clean: the new members have minimum arity 3 and 4 against the existing 2 and 3, and a lambda of the form `(in SubjectPropertyChange c) => ...` converts to `PropertyChangeCallback` and not to `IPropertyChangeObserver`. One pre-existing wrinkle carries over: a bare `null` first argument is ambiguous between the observer and callback overloads, which is why `PerPropertySubscriptionTests.cs:89-92` casts, and the null-argument tests here need the same casts.

Argument validation matches the existing overloads. Null observer, callback, or scheduler throws `ArgumentNullException`, `ImmediateScheduler.Instance` and `CurrentThreadScheduler.Instance` throw `ArgumentException`, and the underlying `Subscribe` still rejects properties that are neither intercepted nor derived, and selectors that are not a direct property access on the lambda parameter. All validation happens before anything is installed.

The public API snapshot (`src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`) is updated by accepting the received file.

## Delivery semantics

Guaranteed by the scheduled overloads:

- **No re-entrancy within a subscription.** Deliberately not phrased as "the observer needs no synchronization of its own", which reads as a promise about the observer instance when it is only a promise about one subscription. Shared observers, closures, and `onError` delegates across subscriptions are invoked concurrently. Delivery N+1 does see delivery N's writes even across scheduler threads, because the drain's settling `Interlocked.Add`, the writer's increment on the same field, and the schedule form a happens-before chain.
- **Dispatch order.** Deliveries arrive in the order the interceptor pushed them. Dispatch order is not commit order under concurrent writes to the same property, exactly as for unscheduled delivery, because dispatch runs outside the subject lock.
- **Isolated from the writer.** Neither an observer exception, nor an `onError` exception, nor a scheduler exception can propagate into the write, suppress other channels' deliveries for that write, or tear the subscription down silently.
- **The post-subscribe delivery guarantee** is inherited: a write that commits after the subscribing call returns is accepted while the subscription stays live. Delivered means accepted by the channel, not that the callback has already run (`docs/tracking.md:145`).

Explicitly not guaranteed:

- **Dormancy is not symmetric with disposal.** Detaching the subject stops acceptance, not the drain, so a change accepted before the detach is still delivered after it. A lifecycle handler that tears down on `SubjectDetaching` can therefore receive callbacks after teardown. This is the opposite of the disposal behaviour below and is the one place the scheduled path does not inherit layer 0's semantics.
- **No backpressure.** The queue is unbounded. A writer faster than the observer grows it without limit, and every buffered change keeps its subject and boxed values alive. `PendingCount` makes the growth observable. For hot properties, compose `Sample` or `Throttle` on `GetSynchronousChangeObservable()` instead of using the overload.
- **An observer that writes the property it observes never drains.** Each delivery enqueues its own successor, so the counter never reaches zero, the subscription's work items continue indefinitely, and `onError` never fires. Bounded batching makes this a fair, yielding loop rather than a held thread, so it degrades rather than starves, but it does not stop. Under layer 0 the same mistake is a loud `StackOverflowException`; here it is quiet, which is worth one sentence in the docs.
- **Staleness is widened, not introduced.** Deferred delivery means `GetNewValue<T>()` can be arbitrarily old by the time the callback runs, turning the sync path's microsecond window into a queue-length window. `GetCurrentValue<T>()` re-reads the property and `Revision` orders changes within one subject.
- **No completion.** The observable never completes and never signals `OnError`, so `ToTask` and `LastAsync` never return.
- **Thread pool saturation delays delivery.**

## Lifetime

Disposing the returned handle moves `_state` out of `Live`, releases the upstream per-property subscription, and through it decrements the process-wide gate (`PropertyChangeSubscription.cs:33` and `:93`, gating `PropertyChangeInterceptor.cs:156`). Four consequences are documented:

- Changes accepted but not yet drained are **dropped and released**. `Dispose` clears the queue, because otherwise each retained 144-byte change keeps pinning its subject and boxed values for as long as the handle is held, and these are the subscriptions that get parked in a DI container. `PropertyChangeQueueSubscription.cs:14-16` documents the retaining behaviour honestly for the sibling channel; this one does not retain.
- A delivery already running when `Dispose` is called can finish after `Dispose` returns, so a handler touching caller-owned disposable state must tolerate a late call. The same applies to `onError`. `Deliver` reads the observer and error handler into locals and null-checks them, the way `PropertyChangeSubscription.Dispatch` does (`:133-140`), so a disposal that clears them cannot null-reference a delivery in flight.
- There is no finalizer, so a dropped handle keeps every write in the process on the slower listener-check path for the process lifetime.
- A writer already past its state check can still enqueue and still schedule after `Dispose` returns. Nothing is delivered, because the drain re-checks, and the resulting unsettled counter is never read again.

The caller owns the scheduler's lifetime and must dispose subscriptions before the scheduler they run on. A `Schedule` call that throws because the scheduler is already disposed is reported through `onError` and faults the subscription. A `Schedule` call that succeeds and whose work item then never runs, which is what a scheduler disposed mid-queue produces, is not recoverable and not reported, as set out under the dispatch protocol.

## Concurrency protocol and its verification

Testing cannot prove the absence of races. What it can do is keep the protocol small enough to argue exhaustively, write the argument down, and back it with deterministic rather than probabilistic tests. The protocol above is about twenty-five lines and rests on the three properties stated with it.

The implementation carries a written argument for each hazardous interleaving: enqueue against enqueue, enqueue against a settling drain, dispose against enqueue, dispose against a mid-flight delivery, fault against dispose, a throwing `ScheduleDrain` against the counter, a drain exit against the next drain entry (which is what carries the observer's own writes forward), and subject detach against queued changes. Each argument names the field, the ordering primitive, and the property it preserves, in the style of the existing Dekker-pairing comments in `PropertyChangeSubscription.cs:56-60` and `PropertyChangeSubscriptions.cs:16-21`.

Verification has four parts:

1. A controllable test scheduler that lets a test decide when the drain runs, so the interesting interleavings are chosen rather than raced for. It also covers the two failure shapes no real scheduler reproduces on demand: throwing from `Schedule`, and accepting a work item that never runs.
2. An instrumented re-entrancy counter, incremented and decremented **around the `Deliver` call site rather than around the drain method**. Around the method it both misses regressions and flakes: the window between a settling `Interlocked.Add` returning zero and the method's own decrement is a legitimate overlap, so a writer taking the zero-to-one transition there produces a spurious failure.
3. Exact delivery counts under concurrent writers, plus a post-quiescence assertion that the counter is zero **and** the queue is empty. That pairing is what actually pins the settle, and the re-entrancy counter alone does not: probes confirmed that reversing the two enqueue lines, replacing the settling `Interlocked.Add` with an `Exchange` to zero, dropping either `Volatile.Read`, moving the settle out of the `finally`, and removing the batch handoff each leave the re-entrancy counter at zero while breaking the protocol.
4. An adversarial review pass pointed specifically at the protocol, in the shape of the one that found the unbounded drain, the missing `finally`, the half-covered fault path, and the gate double-release in the previous draft.

## Performance

**This design is not chosen for its allocation profile, and the earlier draft that said otherwise was wrong.** It is chosen for three correctness properties `ObserveOn` cannot give: two struct copies instead of four, a wedge class we can actually close, and a handle that is not a live foreign sink. The allocation story is a wash, and stating it honestly here stops the benchmark surprising someone later.

There are two regimes, and the one this API targets is the expensive one:

- **One change per burst**, which is what a device-rate or human-rate property produces and what a per-property subscription is for. The drain empties between changes, so every change pays a scheduler work item: about 120 bytes on `Scheduler.Default`, or about 200 once the writer thread carries any `AsyncLocal`, which in an ASP.NET or `Activity` host it always does. `ObserveOn(EventLoopScheduler)` measures 120 in the same shape, so there is no advantage here, only parity without the dedicated thread.
- **Sustained backlog.** Scheduling amortises to one work item per 1024 changes, and the cost becomes `ConcurrentQueue` segment turnover at roughly 124 bytes per change, since consumed segments are abandoned rather than pooled. `ConcurrentQueue` itself is free in the one-in-flight shape, where a single segment is reused.

For contrast, the `ObserveOn` long-running path costs 0.03 bytes per change and is still rejected, because it dedicates a thread to every subscription that signals and this API is designed to be used per property. That trade, threads against allocations, is the real one; the 293.5 bytes of the `DisableOptimizations` path is what made the earlier draft look like a straightforward win.

Cold and allocation free until subscribed, with no intermediate subject. No existing path changes cost: the process-wide gate and the dispatch loop are untouched, and the new overloads install the same `PropertyChangeSubscription` the sync path installs.

Benchmarking is split, because `PropertyChangeSubscriptionsBenchmark` measures the write hot path with `[MemoryDiagnoser]` at one write per operation. A live scheduled subscription there would build an unbounded backlog across the millions of operations BenchmarkDotNet runs, and the allocations-per-operation column would measure the backlog rather than the write. So:

- The existing benchmark gains a **write-side** case only: the cost a live scheduled subscription adds to a write, with the drain running and an explicit drain-to-completion in `IterationCleanup`.
- Per-change **delivery** cost is measured by a separate benchmark with a bounded producer that drains to completion within the operation. It is not folded into the throughput benchmark.

Run locally with `-LocalOnly` rather than gating the pull request on a base-branch comparison, since nothing existing regresses.

## Documentation obligations

`docs/tracking.md`:

- A scheduled-delivery subsection under Per-Property Subscriptions (`:110`) covering the guarantees, the rejection of synchronous schedulers, the unbounded queue, the detach asymmetry, and the disposal semantics.
- A **fifth** row in the channel table (`:155-164`, which already has four data rows and two footnotes) for the scheduled per-property callback. Use the existing vocabulary: the other rows say "arrival" for order, so this one does too rather than introducing "dispatch".
- An amendment to "Per-property observers are not serialized" (`:144`) noting the per-subscription exception.
- An amendment to "Throwing synchronous observers suppress later deliveries" (`:145`) noting that the scheduled path is isolated.
- A "Composing with Rx" note introducing `GetSynchronousChangeObservable` and carrying both traps, so a reader who composes by hand meets them before they bite.

XML documentation, which is what IntelliSense shows at the moment someone implements the interface and which no snapshot test covers:

- `IPropertyChangeObserver` (`:3-8`) currently says implementations "MUST be thread-safe (they may be invoked concurrently) ... and MUST NOT throw". That is now conditional and must say so: unscheduled means must not throw; scheduled means may throw and is reported to `onError`; scheduled with a shared instance means still concurrent.
- `PropertyChangeCallback` gets the same treatment.

## Testing

All new tests join `PerPropertySubscriptionCollection`, and new test classes need the `PropertyChangeSubscriptions.ResetForTests()` constructor call that `PerPropertySubscriptionTests.cs:10` has.

**The enforcement gate has a hole this work must close first.** `PerPropertySubscriptionConventionsTests` scans every test file for markers and fails any that lacks the `[Collection]` attribute, but its `SensitiveMarkers` list contains none of `GetSynchronousChangeObservable`, `ScheduledPropertySubscription`, or `PropertyChangeObservable`. The planned `GetSynchronousChangeObservable` tests subscribe in Rx form (`.Subscribe(change => ...)`), which matches no existing marker, so a whole file installing real `PropertyChangeSubscription`s would run outside the serialized collection and corrupt the process-wide count for every test running in parallel. Add the three markers as part of this change, not after.

Naming follows `When<Condition>_Then<ExpectedBehavior>` with explicit Arrange, Act, and Assert comments, and no hardcoded `Task.Delay` or `Thread.Sleep`.

**The three promises with the highest regression risk**, none of which revision 1 covered:

- `WhenObserverThrows_ThenTheSetterReturnsNormally`.
- `WhenScheduledObserverThrows_ThenAnUnscheduledListenerOnTheSamePropertyStillFires`.
- `WhenWriteCommitsAfterSubscribeReturns_ThenItIsDelivered`, the scheduled twin of the `BlockingWriteInterceptor` test at `PerPropertySubscriptionTests.cs:114`, since the guarantee currently holds only because the subscribe chain installs the upstream synchronously.

**Protocol**
- Deliveries from a single writer arrive in dispatch order.
- A slow observer does not block the writer.
- Concurrent writers never re-enter one subscription's observer, asserted through the re-entrancy counter around `Deliver` with exact delivery counts.
- **After quiescence the counter is zero and the queue is empty.** This is the assertion that pins the settle, and the one the re-entrancy counter cannot substitute for.
- **The drain yields under sustained production**, asserted by the scheduler work-item count growing rather than staying at one while a writer outruns the observer. This is a direct test of the batch handoff and needs no wall-clock wait.
- **Concurrent thread occupancy stays bounded** across many saturated subscriptions, which the distinct-thread-id count does not catch.
- Dispose racing enqueue, and dispose racing a mid-flight delivery, leave no exception escaping and no count drift.
- No `Schedule` call happens after `Dispose` returns for a quiescent subscription, and a fault racing a dispose leaves `ReadSubscriptionCount()` at exactly zero.
- An observer shared across two subscriptions is allowed to overlap, pinning the per-subscription wording rather than a per-observer promise.
- Delivery N+1 observes state written by delivery N when the two land on different scheduler threads.

**Errors**
- A null `onError` swallows and delivery continues.
- A throwing `onError` is swallowed and delivery continues.
- A scheduler that throws on `Schedule` reports through `onError`, does not reach the writer, and releases the subscription exactly once.
- A scheduler that accepts a work item and never runs it leaves the subscription quiet, which pins the documented limit rather than a promise. Deterministic through the controllable test scheduler.
- `onError` invoked from the fault path runs on the writer thread, pinning the fourth contract axis.
- Nothing escapes to the scheduler, asserted through a recording scheduler wrapper that catches and records escapes. This cannot be asserted directly, because an escape on `Scheduler.Default` kills the test host rather than failing the test.

**Ambient state**
- An `AsyncLocal` set on the writer is not visible in the observer.
- A delivery made during a transaction commit does not see `SubjectTransaction.CurrentTransaction`.

**Lifecycle**
- Dormant before first attach, and revives after detach and reattach.
- A change accepted before detach is still delivered after it, pinning the documented asymmetry.
- Dispose returns the process-wide subscription count to zero.

**Guards**
- Null observer, null callback, and null scheduler each throw with the count staying zero, using the casts `PerPropertySubscriptionTests.cs:89-92` uses.
- `ImmediateScheduler.Instance` and `CurrentThreadScheduler.Instance` throw `ArgumentException`.

**`GetSynchronousChangeObservable`**, which has no coverage today
- Cold: no subscription is installed until an observer subscribes.
- Two observers each install their own subscription and both receive.
- Disposing the Rx handle removes the underlying subscription.
- A throwing handler propagates to the writer and leaves the subscription live, pinning the layer 0 inheritance and the decision not to derive from `ObservableBase<T>`.
- `Take(1)` disposes the underlying subscription.
- A null observer throws.

**`SubscribeToProperty` scheduled overloads**
- Delivery happens on the scheduler, asserted against an `EventLoopScheduler`'s own thread id rather than `Assert.NotEqual` against the test thread, which is itself a pool thread and can legitimately be reused by a drain.
- An invalid selector still throws.

**Thread economy**, replacing the coarse process-thread count
- One hundred subscriptions across one hundred properties deliver on a handful of distinct managed thread ids, collected from inside the callbacks. The thread-per-subscription regression produces exactly one hundred distinct ids, so the assertion is direct and needs no margin. Note this test does **not** catch the unbounded-drain regression, which occupies threads concurrently without increasing the distinct count; the occupancy assertion under Protocol is what covers that.

## Scope boundaries

Five capabilities were considered and deferred, each because no consumer needs it yet:

- **A bounded queue with a drop policy.** `maxQueueDepth` with a drop counter has in-repo precedent in `ChangeQueueProcessor`. Deferred in favour of `PendingCount`, which makes growth observable without the library taking a position on which changes to discard, and which is what a consumer would use to justify a bound. Revisit together with conflation, since they answer the same problem differently.

- **Demand-driven conflation** (keep the latest undelivered change, drop the rest). Rx offers only the time-based `Sample` and `Throttle`, so this is the one gap Rx cannot express, and it is the reason the unbounded queue is documented rather than fixed. Owning the dispatcher makes it straightforward to add later.
- **Current value at subscribe time.** The current value is publicly reachable in one hop through `PropertyReference.Metadata.GetValue`, which is what `SubjectPropertyChange.GetCurrentValue<TValue>()` already uses (`SubjectPropertyChange.cs:129`), so reading it is not the blocker. The blocker is that a raw read carries no `Revision`, so an incoming older change cannot be reconciled against it.
- **Async serialized handlers** (`Func<SubjectPropertyChange, CancellationToken, Task>`). An `async void` lambda passed to the scheduled overload silently loses serialization after the first await, which the XML docs must mention while this stays deferred.
- **Per-change ambient context**, for consumers who need `Activity.Current` or a correlation `AsyncLocal` to reach the observer. The shape is known: `ExecutionContext.Capture()` at enqueue stored alongside the change, and `ExecutionContext.Run` per delivery. `Capture` itself is free, returning the existing immutable instance, but the feature is not. Restoring the writer's context also restores `SubjectTransaction.CurrentTransaction` and `ReadPropertyRecorder`'s scopes, so it needs new internal API on both to scrub them inside the delivery, which is what makes it not purely additive; the scrub copy-on-writes the value map at about 72 bytes per delivery, the callback must be a static `ContextCallback` over a reusable per-subscription state object to avoid 112 bytes of closure, and the queue element grows to a change plus a context reference. Suppression stays the default even then, since it is the correct behaviour for observers that do unrelated work.

### Sequencing

A review suggested landing `GetSynchronousChangeObservable` first and the scheduled overloads second, to shrink the diff of the risky part. Answered rather than accepted: the observable alone is release-safe and would be a legitimate PR, but the scheduled path is where all the review findings live and splitting it out does not make the protocol easier to verify, only later. Both land together, and the protocol carries its own review pass instead.

Everything added is public, reachable, and tested, so the rule against public API whose callers land in a later PR holds.

## Follow-ups

- `GetPropertyChangeObservable(scheduler)` (`InterceptorSubjectContextExtensions.cs:124`) flows the writer's `ExecutionContext` to subscribers for the same reason and with the same consequences. It is a released API, so changing it needs its own decision. File an issue.
