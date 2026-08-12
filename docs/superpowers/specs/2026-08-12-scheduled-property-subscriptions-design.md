# Scheduled per-property change subscriptions

Status: designed, not implemented.

Revision 2. Revision 1 built the scheduled path on Rx `ObserveOn` and was reviewed twice, once adversarially against the design and once fact-checking every claim against the tree and against decompiled System.Reactive 6.1.0. Between them the reviews found one wrong performance claim that inverted the central build-versus-compose decision, four defects inherent to `ObserveOn`, and six errors or omissions in the semantics. The dispatch mechanism changed as a result; the API shape did not. Arguments the reviews made that are answered rather than accepted appear under "Synchronous schedulers are rejected" and "Sequencing", so the next reader does not relitigate them.

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

The cost comes from `DefaultScheduler.Schedule` allocating a `UserWorkItem` plus a concurrency-abstraction-layer work item on every change, and from `SchedulerWrapper.Wrap` allocating a fresh closure and delegate on every `Schedule` call. Revision 1 claimed this "amortizes to near zero under sustained load", which is the opposite of what it costs. In a library whose stated priority order puts allocations second only to correctness, and whose motivating consumer runs thousands of properties in an industrial host, 293 bytes per change is not a defensible baseline for the recommended path.

Three further defects are inherent to `ObserveOn` and not fixable from outside it:

- **A throwing scheduler propagates into the writer and then wedges the subscription permanently.** `ObserveOnObserverNew.OnNext` calls `Schedule` unguarded, so a caller-owned `EventLoopScheduler` disposed before the subscription throws `ObjectDisposedException` on the writer thread inside the setter. That escapes `PropertyChangeSubscription.Dispatch` (`:133-140`) and `PropertyChangeInterceptor.WriteProperty` (`:203`), suppressing every later per-property listener on that write. Worse, `_wip` was already incremented with no drain scheduled, and its `Interlocked.Decrement` is not in a `finally`, so the counter stays pinned above zero, no drain is ever scheduled again, and the queue grows forever. `onError` never fires.
- **Four by-value copies** of the 144 byte struct per delivery: into the queue, out of the queue, through `ForwardOnNext`, and into the `AnonymousObserver` lambda. `in` is only restored at the final `OnChange`. Revision 1 claimed one, while opening by criticising the consumer implementation for four.
- **The returned `IDisposable` is a live Rx internal.** It is an `ObserveOnObserverNew<T>`, which casts to `IObserver<SubjectPropertyChange>`, letting a consumer inject changes the model never produced or call `OnCompleted` to kill the subscription without disposing the handle. An Rx version bump can change its behaviour with no signature change and no snapshot diff.

Owning the dispatcher fixes all four: no per-change scheduling allocation, failure handling we control, the change handed to the observer by reference, and our own handle type. It is also in idiom, since `PropertyChangeQueueSubscription` already hand-rolls the same shape with explicit lost-wakeup reasoning.

### The dispatch protocol

A counting work-in-progress field rather than a flag, which removes the classic empty-check-then-release lost wakeup by construction:

```csharp
// Enqueue: writer thread, inside dispatch, outside the subject lock
if (Volatile.Read(ref _disposed) != 0) return;
_queue.Enqueue(change);
if (Interlocked.Increment(ref _wip) == 1)
    ScheduleDrain();

// Drain: pool or scheduler thread
var pending = Volatile.Read(ref _wip);
do
{
    long processed = 0;
    while (processed < pending && _queue.TryDequeue(out var change))
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Deliver(in change);
        processed++;
    }
    pending = Interlocked.Add(ref _wip, -processed);
} while (pending != 0);
```

Two invariants carry the design. At most one drain is active, because only the zero-to-one transition schedules. No drain exits with work outstanding, because it subtracts exactly what it consumed and loops while the counter is non-zero. The disposal check returns without settling the counter, which is deliberate: once disposed, nothing enqueues and nothing schedules, so a non-zero counter is unreachable state rather than a wedge.

`ScheduleDrain` must not let an exception from the scheduler reach the writer. It catches, reports to `onError`, and transitions the subscription to faulted, which stops acceptance and releases the upstream per-property subscription exactly as `Dispose` does, including the process-wide gate decrement. Faulting is therefore observable through `onError` and leaks nothing, unlike the `ObserveOn` wedge, which is silent and holds the gate open forever. A caller's disposal-ordering mistake becomes a reported error rather than a dead subscription.

`Deliver` wraps the observer call and routes a throw to `onError`, which is itself wrapped and swallowed.

### `ExecutionContext` flow is suppressed

Every scheduler Rx ships flows the writer's `ExecutionContext` to the delivery callback, verified for `Scheduler.Default`, `TaskPoolScheduler`, `EventLoopScheduler`, `ImmediateScheduler`, and `CurrentThreadScheduler`. Because scheduling happens from inside the write, the observer would then see the writer's ambient `AsyncLocal` state, and a single drain batch would run every queued change under whichever writer enqueued first.

This library has two ambient `AsyncLocal`s that this corrupts:

- `SubjectTransaction.CurrentTransaction` (`Transactions/SubjectTransaction.cs:13`). A commit replays writes, the drain inherits the committing transaction, the commit finishes and `Dispose` returns `_pendingChanges` to a shared pool. An observer that writes a property on the drain thread then takes the capture branch and mutates a dictionary already returned to the pool and possibly rented by a different live transaction. The observer's write is lost and can surface inside somebody else's commit.
- `ReadPropertyRecorder._activeScopes` (`Recorder/ReadPropertyRecorder.cs:14`). Property reads made by the observer, including `change.GetCurrentValue<T>()` which this design recommends as the staleness remedy, get recorded into a foreign render scope.

Derived-property tracking is unaffected: `DerivedPropertyChangeHandler`'s recorder and `SubjectChangeContext` are `[ThreadStatic]`, not `AsyncLocal`.

`ScheduleDrain` therefore wraps its `scheduler.Schedule` call in `using (ExecutionContext.SuppressFlow())`. Since `scheduler` is required there is no second dispatch path to keep in step. Accepted cost, documented: `Activity.Current` and logger scopes stop flowing to observers too. That is the correct trade, and it matches what a queue consumer on its own thread already sees.

The same defect exists today on `GetPropertyChangeObservable(scheduler)` (`InterceptorSubjectContextExtensions.cs:124`). Fixing it there is out of scope and becomes a follow-up issue, because it changes behaviour for an already-released API and deserves its own decision.

### Synchronous schedulers are rejected

`ImmediateScheduler.Instance` and `CurrentThreadScheduler.Instance` are rejected with an `ArgumentException` naming layer 0 as the synchronous option.

A review argued for documenting instead. Measured behaviour is worse than revision 1 described and worse than documentation can carry. With two writer threads and 5 000 pushes each, all 10 000 deliveries ran on one thread: the thread that wins the counter race stays inside its own setter draining the other writer's production for as long as production continues, so its setter latency is unbounded and proportional to every writer's throughput. Separately, under `WithTransactions` the commit replay dispatches while holding the exclusive transaction lock, so a synchronous drain runs consumer code under that lock and an observer that starts a transaction on the same context deadlocks. An overload whose entire purpose is off-writer delivery should not silently do the opposite.

Accepted limit, stated in the XML docs: only the two known singletons are detectable. A custom scheduler that runs actions inline has the same hazard and cannot be rejected. The guard catches the realistic mistake without claiming to be complete.

The deterministic-test argument for allowing them does not apply, because the test plan introduces a controllable scheduler of its own.

### `onError` is optional and swallows by default

Making it required was considered and rejected on two grounds. Mandatory ceremony on the safe path pushes people onto the unsafe one, since raw Rx composition next to it demands nothing and lands back on both traps. And nothing else in this library requires an error handler: the queue channel leaves consumer exceptions to the consumer's own thread, and `GetPropertyChangeObservable` lets a throwing subscriber take out the scheduler work item.

The default swallows rather than logs. `Namotion.Interceptor.Tracking` references only System.Reactive and the core project, with no `ILogger` anywhere in it or in the core, so logging would mean a new dependency to service one error path. Independently, a handler that throws on every change of a hot property would emit thousands of entries per second, turning one broken handler into an outage of the logging pipeline. Swallowing keeps the blast radius at the handler and lets callers opt into logging.

The `onError` contract is stated on three axes that revision 1 left open:

- **It must not throw.** An exception from it is caught and swallowed, because the alternative is process death caused by the failure of the thing added to observe failures.
- **It can run after `Dispose` returns**, because an in-flight delivery finishes. A handler writing into caller-owned disposable state must tolerate a late call.
- **It is serialized per subscription, not per delegate.** A delegate shared across subscriptions is invoked concurrently.

The diagnostic consequence of the default is documented: with no handler, a permanently throwing observer is invisible, because it keeps firing and keeps failing rather than stopping.

### Serialization is per subscription, not per observer

Revision 1 said the observer "is never invoked concurrently with itself and needs no synchronization of its own". That is false for the shape this design exists to serve. The counter is per subscription, and one adapter object subscribed across many properties overlaps freely; a probe measured 14 overlapping invocations across two subscriptions sharing an observer.

The guarantee is therefore stated as: within a single subscription the observer is never re-entered; an observer instance, callback closure, or `onError` delegate shared across several subscriptions is still invoked concurrently and must synchronize.

### The handle is a named type

`Subscribe` returns `IDisposable`, but the instance is a sealed internal `ScheduledPropertySubscription`, not a foreign sink. One allocation per subscription, none per change. It is where the faulted flag from the scheduler-throw case lives, and it stops Rx internals escaping through a public API.

### `scheduler` stays required, and evolution is by overload only

Owning the dispatcher makes a no-scheduler asynchronous overload natural, and it is deliberately not added: `Subscribe(cb)` and `Subscribe(cb, onError)` would look near-identical at a call site while having opposite delivery semantics. Requiring `scheduler` keeps the two-argument and three-argument overload sets disjoint, which is also what removes any overload ambiguity.

This diverges from `GetPropertyChangeObservable`, where `scheduler` is optional. The divergence is deliberate and recorded here so a later reviewer does not "fix" it.

Because the package ships on NuGet, adding any further parameter later, even an optional one, would break already-compiled callers. These members evolve by new overload only.

## API surface

All in `PropertyChangeSubscriptionExtensions`.

```csharp
IObservable<SubjectPropertyChange> GetSynchronousChangeObservable(this PropertyReference property);

IDisposable Subscribe(this PropertyReference property,
    IPropertyChangeObserver observer, IScheduler scheduler, Action<Exception>? onError = null);

IDisposable Subscribe(this PropertyReference property,
    PropertyChangeCallback callback, IScheduler scheduler, Action<Exception>? onError = null);

IDisposable SubscribeToProperty<TSubject, TValue>(this TSubject subject,
    Expression<Func<TSubject, TValue>> propertySelector,
    IPropertyChangeObserver observer, IScheduler scheduler, Action<Exception>? onError = null)
    where TSubject : IInterceptorSubject;

IDisposable SubscribeToProperty<TSubject, TValue>(this TSubject subject,
    Expression<Func<TSubject, TValue>> propertySelector,
    PropertyChangeCallback callback, IScheduler scheduler, Action<Exception>? onError = null)
    where TSubject : IInterceptorSubject;
```

The `SubscribeToProperty` overloads exist because that is the entry point the docs lead with (`docs/tracking.md:116`). Without them, a caller who wants scheduled delivery has to abandon the typed selector and construct a `PropertyReference` by hand.

Overload resolution was checked and is clean: the new members have minimum arity 3 and 4 against the existing 2 and 3, and a lambda of the form `(in SubjectPropertyChange c) => ...` converts to `PropertyChangeCallback` and not to `IPropertyChangeObserver`. One pre-existing wrinkle carries over: a bare `null` first argument is ambiguous between the observer and callback overloads, which is why `PerPropertySubscriptionTests.cs:89-92` casts, and the null-argument tests here need the same casts.

Argument validation matches the existing overloads. Null observer, callback, or scheduler throws `ArgumentNullException`, `ImmediateScheduler.Instance` and `CurrentThreadScheduler.Instance` throw `ArgumentException`, and the underlying `Subscribe` still rejects properties that are neither intercepted nor derived, and selectors that are not a direct property access on the lambda parameter. All validation happens before anything is installed.

The public API snapshot (`src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`) is updated by accepting the received file.

## Delivery semantics

Guaranteed by the scheduled overloads:

- **Serialized per subscription.** Within one subscription the observer is never re-entered and needs no synchronization of its own. Shared observers, closures, and `onError` delegates across subscriptions are not covered.
- **Dispatch order.** Deliveries arrive in the order the interceptor pushed them. Dispatch order is not commit order under concurrent writes to the same property, exactly as for unscheduled delivery, because dispatch runs outside the subject lock.
- **Isolated from the writer.** Neither an observer exception, nor an `onError` exception, nor a scheduler exception can propagate into the write, suppress other channels' deliveries for that write, or tear the subscription down silently.
- **The post-subscribe delivery guarantee** is inherited: a write that commits after the subscribing call returns is accepted while the subscription stays live. Delivered means accepted by the channel, not that the callback has already run (`docs/tracking.md:145`).

Explicitly not guaranteed:

- **Dormancy is not symmetric with disposal.** Detaching the subject stops acceptance, not the drain, so a change accepted before the detach is still delivered after it. A lifecycle handler that tears down on `SubjectDetaching` can therefore receive callbacks after teardown. This is the opposite of the disposal behaviour below and is the one place the scheduled path does not inherit layer 0's semantics.
- **No backpressure.** The queue is unbounded. A writer faster than the observer grows it without limit, and every buffered change keeps its subject and boxed values alive. For hot properties, compose `Sample` or `Throttle` on `GetSynchronousChangeObservable()` instead of using the overload.
- **Staleness is widened, not introduced.** Deferred delivery means `GetNewValue<T>()` can be arbitrarily old by the time the callback runs, turning the sync path's microsecond window into a queue-length window. `GetCurrentValue<T>()` re-reads the property and `Revision` orders changes within one subject.
- **No completion.** The observable never completes and never signals `OnError`, so `ToTask` and `LastAsync` never return.
- **Thread pool saturation delays delivery.**

## Lifetime

Disposing the returned handle stops acceptance, releases the upstream per-property subscription, and decrements the process-wide gate (`PropertyChangeSubscription.cs:33` and `:93`, gating `PropertyChangeInterceptor.cs:156`). Three consequences are documented:

- Changes accepted but not yet drained are dropped on dispose.
- A delivery already running when `Dispose` is called can finish after `Dispose` returns, so a handler touching caller-owned disposable state must tolerate a late call. The same applies to `onError`.
- There is no finalizer, so a dropped handle keeps every write in the process on the slower listener-check path for the process lifetime. Restated here because scheduled subscriptions are the kind that get parked in a DI container.

The caller owns the scheduler's lifetime. Disposing a scheduler while subscriptions still use it faults those subscriptions through `onError` rather than wedging them, but the subscriptions stop delivering and should be disposed.

## Concurrency protocol and its verification

Testing cannot prove the absence of races. What it can do is keep the protocol small enough to argue exhaustively, write the argument down, and back it with deterministic rather than probabilistic tests. The protocol above is about twenty-five lines and rests on the two invariants stated with it.

The implementation carries a written argument for each hazardous interleaving: enqueue against enqueue, enqueue against drain exit, dispose against enqueue, dispose against a mid-flight delivery, a throwing `ScheduleDrain` against the counter, and subject detach against queued changes. Each argument names the field, the ordering primitive, and the invariant it preserves, in the style of the existing Dekker-pairing comments in `PropertyChangeSubscription.cs:56-60` and `PropertyChangeSubscriptions.cs:16-21`.

Verification has four parts:

1. A controllable test scheduler that lets a test decide when the drain runs, so the interesting interleavings are chosen rather than raced for.
2. An internal instrumented invariant counting concurrent drain entries, asserted zero across every concurrency test, so a protocol regression fails loudly instead of flakily.
3. Stress tests in the shape the repo already uses (`PerPropertySubscriptionLifecycleTests.WhenConcurrentWritesRaceSubscriptionDispose_...`, `...WhenSubscribeDisposeAndWriteRaceOnSameProperty_...`) with exact delivery counts.
4. An adversarial review pass pointed specifically at the protocol once drafted.

## Performance

Cold and allocation free until subscribed, with no intermediate subject. Per change, the cost is a `ConcurrentQueue` enqueue plus one interlocked increment, and one scheduler work item per drain burst rather than per change. The change is handed to the observer by reference through the drain, so the copies are the enqueue and the dequeue rather than the four `ObserveOn` imposes.

No existing path changes cost. The process-wide gate and the dispatch loop are untouched, and the new overloads install the same `PropertyChangeSubscription` the sync path installs.

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

All new tests join `PerPropertySubscriptionCollection`. This is an enforced gate rather than a convention: `PerPropertySubscriptionConventionsTests.cs:22-45` scans every test file for markers including `.Subscribe((in ` and fails any that lacks the `[Collection]` attribute. New test classes also need the `PropertyChangeSubscriptions.ResetForTests()` constructor call that `PerPropertySubscriptionTests.cs:10` has.

Naming follows `When<Condition>_Then<ExpectedBehavior>` with explicit Arrange, Act, and Assert comments, and no hardcoded `Task.Delay` or `Thread.Sleep`.

**The three promises with the highest regression risk**, none of which revision 1 covered:

- `WhenObserverThrows_ThenTheSetterReturnsNormally`.
- `WhenScheduledObserverThrows_ThenAnUnscheduledListenerOnTheSamePropertyStillFires`.
- `WhenWriteCommitsAfterSubscribeReturns_ThenItIsDelivered`, the scheduled twin of the `BlockingWriteInterceptor` test at `PerPropertySubscriptionTests.cs:114`, since the guarantee currently holds only because the subscribe chain installs the upstream synchronously.

**Protocol**
- Deliveries from a single writer arrive in dispatch order.
- A slow observer does not block the writer.
- Concurrent writers never re-enter one subscription's observer, asserted through the instrumented counter with exact delivery counts.
- Dispose racing enqueue, and dispose racing a mid-flight delivery, leave no exception escaping and no count drift.
- An observer shared across two subscriptions is allowed to overlap, pinning the per-subscription wording rather than a per-observer promise.

**Errors**
- A null `onError` swallows and delivery continues.
- A throwing `onError` is swallowed and delivery continues.
- A scheduler that throws on `Schedule` reports through `onError`, does not reach the writer, and does not wedge the subscription silently.
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
- One hundred subscriptions across one hundred properties deliver on a handful of distinct managed thread ids, collected from inside the callbacks. The regression this pins produces exactly one hundred distinct ids, so the assertion is direct and needs no margin.

## Scope boundaries

Three capabilities were considered and deferred, each because no consumer needs it yet and each additive from this design:

- **Demand-driven conflation** (keep the latest undelivered change, drop the rest). Rx offers only the time-based `Sample` and `Throttle`, so this is the one gap Rx cannot express, and it is the reason the unbounded queue is documented rather than fixed. Owning the dispatcher makes it straightforward to add later.
- **Current value at subscribe time.** The current value is publicly reachable in one hop through `PropertyReference.Metadata.GetValue`, which is what `SubjectPropertyChange.GetCurrentValue<TValue>()` already uses (`SubjectPropertyChange.cs:129`), so reading it is not the blocker. The blocker is that a raw read carries no `Revision`, so an incoming older change cannot be reconciled against it.
- **Async serialized handlers** (`Func<SubjectPropertyChange, CancellationToken, Task>`). An `async void` lambda passed to the scheduled overload silently loses serialization after the first await, which the XML docs must mention while this stays deferred.

### Sequencing

A review suggested landing `GetSynchronousChangeObservable` first and the scheduled overloads second, to shrink the diff of the risky part. Answered rather than accepted: the observable alone is release-safe and would be a legitimate PR, but the scheduled path is where all the review findings live and splitting it out does not make the protocol easier to verify, only later. Both land together, and the protocol carries its own review pass instead.

Everything added is public, reachable, and tested, so the rule against public API whose callers land in a later PR holds.

## Follow-ups

- `GetPropertyChangeObservable(scheduler)` (`InterceptorSubjectContextExtensions.cs:124`) flows the writer's `ExecutionContext` to subscribers for the same reason and with the same consequences. It is a released API, so changing it needs its own decision. File an issue.
