# Scheduled per-property change subscriptions

Status: designed, not implemented.

Line citations were verified against the tree at the time of writing. Freeze the file before implementation, since they drift.

## Problem

Per-property subscriptions (`docs/tracking.md:110`) deliver synchronously, on the writing thread, possibly concurrently, and inside the write. `IPropertyChangeObserver` therefore requires its implementations to be thread-safe, fast, non-blocking, and to never throw, and a throwing observer propagates out of the setter and suppresses later deliveries for that write (`docs/tracking.md:144-145`).

Those four constraints are correct for the channel but wrong for a large class of consumers. An observer that pushes a value to a device, writes a row, or updates a UI cannot honour them. Today such a consumer has to build its own bridge from the per-property callback to a scheduler.

A real consumer did exactly that, and the result shows what the library currently forces people to rediscover. That implementation routed changes through an intermediate `Subject<T>` that it deliberately never disposed (with a `CA2000` suppression to match), documented "handler must not throw, an exception ends delivery permanently and silently" as a caller obligation rather than removing the hazard, named the method `SubscribeOn` when the semantics are `ObserveOn`, and passed the roughly 140 byte `SubjectPropertyChange` by value through four copies.

Two of those are traps the library's own design creates and then leaves in consumer code:

1. **Thread per subscription.** `ObserveOn` dedicates a private thread to every subscription that signals when the scheduler advertises `ISchedulerLongRunning`, which both `Scheduler.Default` and `TaskPoolScheduler` do. Per-property subscriptions exist precisely to be used one per property, so a model with thousands of properties detonates. Avoiding it requires `scheduler.DisableOptimizations(typeof(ISchedulerLongRunning))`, which nobody derives from first principles.
2. **Process death on a throwing handler.** An exception reaching an `ObserveOn` sink escapes a scheduler work item on the thread pool, which is unhandled and terminates the process in .NET Core.

## Why this belongs in the library

The library adds no capability here. Everything is reachable from the released `Subscribe(PropertyReference, PropertyChangeCallback)` plus Rx, in roughly forty lines, leak free and fast. What is at stake is which side of the boundary owns the two traps.

A narrower option was considered and rejected: ship only the observable primitive and let consumers compose scheduling. It fails on an inversion. The observable is the piece with exactly one correct implementation and no way to get it wrong, while the scheduling and error isolation are the pieces that are easy to get wrong. Shipping only the observable hands the library the safe part and leaves both dangerous parts at every call site, in a library whose design actively encourages many small per-property subscriptions.

Doing nothing and documenting the pattern was also considered. It has the same inversion and additionally requires every consumer to copy the same file.

## Decisions

### Three layers, each a faithful superset of the one below

| Layer | API | Thread | Serialized | Observer may throw |
|---|---|---|---|---|
| 0 (released) | `property.Subscribe(callback)` | writer | no | no, propagates into the write |
| 1 (new) | `property.GetChangeObservable()` | writer | no | no, propagates into the write |
| 2 (new) | `Subscribe(callback, scheduler, onError)` | scheduler | yes | yes, isolated |

### Layer 1 is a faithful adapter, not a safer one

`GetChangeObservable()` has layer 0's contract exactly: synchronous, on the writing thread, possibly concurrent, and a throwing handler propagates back into the setter. It is the same channel wearing an `IObservable<T>`, and that is the point, since an Rx adapter that silently changed delivery semantics would be harder to reason about than one that does not. The hazard is that "it is Rx now, so it must be safe" is the assumption people will make, so the remarks block states the inheritance explicitly.

`PropertyChangeObservable` implements `IObservable<T>` directly rather than deriving from `ObservableBase<T>`. Deriving would let Rx wrap observers in an auto-detaching decorator that tears the subscription down when the handler throws, diverging from layer 0 for no benefit.

It stays `internal`; only the extension method is public.

### Always apply `ObserveOn`, never special-case the scheduler

`GetPropertyChangeObservable` skips `ObserveOn` when the scheduler is `ImmediateScheduler.Instance` (`InterceptorSubjectContextExtensions.cs:118-122`). Copying that precedent here would be a bug. That shortcut is safe there because the upstream is already a synchronized multicast observable (`InterceptorSubjectContextExtensions.cs:111-116`), so serialization survives it. The per-property channel has no such upstream, so the `ObserveOn` sink's work-in-progress guard is the only thing providing serialization, and skipping it would silently drop the guarantee.

Consequence: a synchronous scheduler keeps serialization but loses everything else. With `ImmediateScheduler` or `CurrentThreadScheduler` the drain runs inline on a writer thread, so a slow observer blocks writers, and one writer can drain another writer's queued change inside its own setter. This is documented rather than rejected, so deliberate use in tests keeps working.

### `onError` is optional and swallows by default

Making it required was considered and rejected on two grounds. Mandatory ceremony on the safe path pushes people onto the unsafe one, since a raw `.ObserveOn(...)` next to it demands nothing and lands back on both traps. And nothing else in this library requires an error handler: the queue channel leaves consumer exceptions to the consumer's own thread, and `GetPropertyChangeObservable` lets a throwing subscriber take out the scheduler work item.

The default swallows rather than logs. `Namotion.Interceptor.Tracking` depends on System.Reactive and the core project only, with no `ILogger` anywhere in it or in the core, so logging would mean adding `Microsoft.Extensions.Logging.Abstractions` to service one error path. Independently of the dependency, a handler that throws on every change of a hot property would emit thousands of entries per second, turning one broken handler into an outage of the logging pipeline. Swallowing keeps the blast radius at the handler and lets callers opt into logging.

The XML docs state the diagnostic consequence: with no handler, a permanently throwing observer is invisible, because it keeps firing and keeps failing rather than stopping.

### The error handler needs its own guard

```csharp
try { observer.OnChange(in change); }
catch (Exception exception) { onError?.Invoke(exception); }
```

An exception from `onError` leaves the catch block, leaves the Rx lambda, and escapes a scheduler work item on the thread pool, which is unhandled and kills the process. A logging sink that throws when its buffer is full is how that happens in production. `onError` is invoked inside its own try-catch that swallows, since the alternative is process death caused by the failure of the thing added to observe failures.

### Naming

`GetChangeObservable` rather than `GetPropertyChangeObservable`, which is what the context-level equivalent is called (`InterceptorSubjectContextExtensions.cs:109`). The long name stutters on a `PropertyReference`.

The scheduled entry points are overloads of the existing `Subscribe` and `SubscribeToProperty` rather than new names, so the scheduler reads as a delivery modifier on a known operation.

## API surface

All in `PropertyChangeSubscriptionExtensions`.

```csharp
IObservable<SubjectPropertyChange> GetChangeObservable(this PropertyReference property);

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

Argument validation matches the existing overloads: null observer, callback, or scheduler throws `ArgumentNullException` before anything is installed, and the underlying `Subscribe` still rejects properties that are neither intercepted nor derived, and selectors that are not a direct property access on the lambda parameter.

The public API snapshot (`src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`) is updated by accepting the received file.

## Delivery semantics

Guaranteed by the scheduled overloads:

- **Serialized.** The observer is never invoked concurrently with itself and needs no synchronization of its own. Holds for every scheduler.
- **Dispatch order.** Deliveries arrive in the order the interceptor pushed them. Dispatch order is not commit order under concurrent writes to the same property, exactly as for unscheduled delivery, because dispatch runs outside the subject lock.
- **Isolated from the writer.** An observer exception cannot propagate into the write, cannot suppress other channels' deliveries for that write, and cannot tear the subscription down.
- **Dormancy, revival, and the post-subscribe delivery guarantee** are inherited unchanged from the underlying per-property subscription. As with the existing scheduler-based Rx wording (`docs/tracking.md:145`), delivered means accepted by the channel, not that the callback has already run.

Explicitly not guaranteed:

- **No backpressure.** The queue is unbounded. A writer faster than the observer grows it without limit, and every buffered change keeps its subject and boxed values alive. For hot properties, compose `Sample` or `Throttle` on `GetChangeObservable()` instead of using the overload.
- **Staleness is widened, not introduced.** Deferred delivery means `GetNewValue<T>()` can be arbitrarily old by the time the callback runs, turning the sync path's microsecond window into a queue-length window. `GetCurrentValue<T>()` re-reads the property and `Revision` orders changes within one subject.
- **No completion.** The sequence never completes and never signals `OnError`, so `ToTask` and `LastAsync` never return.
- **Thread pool saturation delays delivery.** The alternative Rx offers is a dedicated thread per subscription, which is unaffordable at one subscription per property.

## Lifetime

Disposing the returned handle disposes the `ObserveOn` sink, which disposes the upstream per-property subscription and decrements the process-wide gate. Two consequences are specific to the scheduled path and are documented:

- Changes accepted but not yet drained are dropped on dispose.
- A callback already running when `Dispose` is called can finish after `Dispose` returns, so a handler touching disposable state must tolerate a late call.

The existing warning applies unchanged and is restated here, because scheduled subscriptions are the kind that get parked in a DI container: there is no finalizer, so a dropped handle keeps every write in the process on the slower listener-check path for the process lifetime.

## Performance

Cold and allocation free until subscribed, with no intermediate subject. Per change it enqueues into the `ObserveOn` queue plus one scheduler work item when the drain is idle, which amortizes to near zero under sustained load. `in` is preserved at the observer boundary; the one unavoidable by-value copy of `SubjectPropertyChange` happens at the `IObserver<T>.OnNext` boundary.

No existing path changes cost. The process-wide gate and the dispatch loop are untouched, and the new overloads install the same `PropertyChangeSubscription` the sync path installs.

Scheduled cases are added to `PropertyChangeSubscriptionsBenchmark` for a per-change baseline against the sync path, run locally with `-LocalOnly` rather than gating the pull request on a base-branch comparison, since nothing existing regresses.

## Documentation obligations

`docs/tracking.md`:

- A scheduled-delivery subsection under Per-Property Subscriptions (`:110`) covering the guarantees, the synchronous-scheduler caveat, the unbounded queue, and the disposal semantics.
- A fourth row in the channel table (`:155-159`) for the scheduled per-property callback: exactly-once conditional, order dispatch, consumer runs on scheduler.
- An amendment to "Per-property observers are not serialized" (`:144`) noting the scheduled exception.
- An amendment to "Throwing synchronous observers suppress later deliveries" (`:145`) noting that the scheduled path is isolated.
- A short "Composing with Rx" note introducing `GetChangeObservable` and carrying both traps, so a reader who composes by hand meets them before they bite.

## Testing

All new tests join `PerPropertySubscriptionCollection`, since they create per-property subscriptions and the count is process-wide.

Fixes to the existing `ScheduledPropertySubscriptionTests`:

- `WhenManyPropertiesAreSubscribed_ThenNoThreadIsDedicatedPerSubscription` drops the `async Task` signature and its trailing `await Task.CompletedTask`, and disposes its subscriptions on the failure path.
- Add the `PropertyChangeSubscriptions.ResetForTests()` constructor call that `PerPropertySubscriptionTests` has.

New tests:

**Semantics**
- A single writer's writes arrive in dispatch order.
- A slow observer does not block the writer: the writer completes while the observer is still draining.
- `ImmediateScheduler` still serializes the observer but delivers on the writer thread.
- The subscription revives after the subject is detached and reattached.

**Errors**
- A null `onError` swallows the exception and delivery continues.
- A throwing `onError` is swallowed, delivery continues, and nothing escapes to the scheduler.

**Lifetime**
- Dispose returns the process-wide subscription count to zero on the scheduled path.

**Guards**
- Null observer, null callback, and null scheduler each throw and leave the count at zero.

**`GetChangeObservable`**, which has no coverage today
- Cold: no subscription is installed until an observer subscribes.
- Two observers each install their own subscription and both receive.
- Disposing the Rx handle removes the underlying subscription.
- A throwing handler propagates to the writer, pinning the layer 0 inheritance.
- `Take(1)` disposes the underlying subscription.
- A null observer throws.

**`SubscribeToProperty` scheduled overloads**
- Delivery happens off the writer thread.
- An invalid selector still throws.

## Scope boundaries

Three capabilities were considered and deferred, each because no consumer needs it yet and each additive from this design:

- **Demand-driven conflation** (keep the latest undelivered change, drop the rest). Rx offers only the time-based `Sample` and `Throttle`, so this is the one gap Rx cannot express, and it is the reason the unbounded queue is documented rather than fixed.
- **Current value at subscribe time.** There is no public value getter on `PropertyReference`, and a raw read carries no `Revision`, so an incoming older change cannot be reconciled against it. Closing this properly needs more than an operator.
- **Async serialized handlers** (`Func<SubjectPropertyChange, CancellationToken, Task>`). Composable today as `.Select(c => Observable.FromAsync(...)).Concat()`, and an `async void` lambda passed to the scheduled overload silently loses serialization after the first await, which the docs should mention if this stays deferred.
