# Unified Property Change Interceptor with Subject-Stored Per-Property Subscriptions

Status: design approved, ready for implementation plan
Date: 2026-07-16 (revised 2026-07-17: subject-stored registrations; second revision after external and independent adversarial review)
Target packages: `Namotion.Interceptor.Tracking` (primary) plus one additive internal member on a core struct, no public API change: a per-write dedup flag on `PropertyWriteContext` (core already grants `InternalsVisibleTo` to Tracking, `Namotion.Interceptor.csproj:16`)

## Overview

Merge the two existing property-change delivery interceptors (`PropertyChangeObservable` and `PropertyChangeQueue`) into a single `PropertyChangeInterceptor`, and add a new synchronous per-property subscription API. A feature that cares about one or a few specific properties (a heartbeat, a watchdog, a dirty flag) registers an indexed, allocation-free callback and needs no observer, no queue, no thread, and no background service of its own.

Per-property registrations are stored on the subject itself (in `IInterceptorSubject.Data`), not in a registry owned by the interceptor. The callbacks live with the subject, like `INotifyPropertyChanged` handlers live with their source object. Consequences, all deliberate:

- No lifecycle coupling. The interceptor does not implement `IPropertyLifecycleHandler`; there is no detach-time cleanup, no hybrid disposal, and `WithLifecycle()` is not required for anything in this feature.
- Subscriptions survive detach and re-attach ("revival"), survive context moves, and can be created before the subject is first attached. While the subject is not attached to a context with a `PropertyChangeInterceptor`, the subscription is dormant: writes bypass interception, so nothing fires; delivery resumes when the subject is attached again.
- Ownership follows the standard `IDisposable` rule, no weak-reference machinery. The subject holds its subscriptions; drop the handle without disposing and subject, subscriptions, and observers form a rootless cycle collected together (fire-and-forget), or retain the handle and dispose it when done. There is no unconditional no-pin claim (an observer that captures the subject pins it through a retained handle); the rule is the `INotifyPropertyChanged` forgotten-handler caveat in a different shape. Documented, not managed by machinery.

This is a breaking change to the public API of `Namotion.Interceptor.Tracking`. It is acceptable at the current version (0.0.2) under three hard constraints:

1. No functionality is removed. Everything that works today keeps working, with an unchanged consumer-facing surface where practical.
2. No performance regression, on idle and active paths, including single-facet activity (only the queue or only the observable in use on a context), proven by benchmarks.
3. Fast paths stay super cheap, as specified below.

## Motivation

Both existing delivery paths are context-wide firehoses:

- `PropertyChangeObservable` (Rx). Each `GetPropertyChangeObservable()` call builds its own `.Publish().RefCount()` chain, so downstream subscribers of one returned instance share that instance's upstream subscription, but separate calls do not. Every subscriber still receives every change and evaluates its own predicate. With many features each watching one property this is O(features x changes) filtering.
- `PropertyChangeQueue` (high performance). `CreatePropertyChangeQueueSubscription()` gives each consumer an isolated `ConcurrentQueue` plus a dedicated consumer thread. This is heavy: a whole thread and queue per feature.

There is no indexed per-property subscription. A feature that watches a single property today pays for a full firehose subscription (and often a background service).

The two interceptors also run as two separate links in the write chain. On every write where both are active (the `WithFullPropertyTracking()` default), the change payload `SubjectPropertyChange` is built twice (two independent `SubjectPropertyChange.Create` calls, which for boxed or large-struct or reference-type values means duplicate holder allocations, and two `GetFinalValue()` calls, which for derived properties means the getter runs twice and the two facets can even publish different values for one write) and the write pays two interceptor hops.

## Current Architecture (for reference)

Both classes live in `src/Namotion.Interceptor.Tracking/Change/`. Both implement `IWriteInterceptor` and are registered independently as services. Both are public and appear in the public API snapshot.

- `PropertyChangeObservable : IObservable<SubjectPropertyChange>, IWriteInterceptor`. Gate: `if (!_subject.HasObservers) { next(ref context); return; }`. Builds the change, calls `_syncSubject.OnNext(change)` where `_syncSubject = Subject.Synchronize(_subject)`. `HasObservers` self-heals when the last observer unsubscribes.
- `PropertyChangeQueue : IWriteInterceptor, IDisposable`. Gate: `if (subscriptions.Length == 0) { next(ref context); return; }`. Builds the change, fans it out to a copy-on-write `PropertyChangeQueueSubscription[]`. Subscribe and unsubscribe mutate the array under a `Lock` (`PropertyChangeQueue.cs:13`). `Dispose()` completes all current subscriptions and wakes blocked `TryDequeue` consumers (`PropertyChangeQueue.cs:87-95`); it does not affect the observable, which is a separate object and not disposable.
- `PropertyChangeQueueSubscription : IDisposable`. The consumer-facing pull handle (`TryDequeue`), returned by `CreatePropertyChangeQueueSubscription()`.

Facts that shape the design:

- The write chain holds the subject lock only around the innermost field write. `WriteInterceptorFactory.cs:29-37` wraps only `innerWriteValue(...)` plus origin/timestamp finalization in `lock (context.Property.Subject.SyncRoot)`. Every interceptor's post-`next` code (all notification dispatch) runs outside that lock, on the writing thread.
- The write terminal already performs one subject-data dictionary operation per write: `SetWriteTimestamp` does `Subject.Data.GetOrAdd((Name, WriteTimestampKey), ...)` unconditionally (`WriteInterceptorFactory.cs:20,35` calling `PropertyReference.cs:107-111`). Per-subject tuple-keyed dictionary access is therefore already baseline hot-path cost, with existing public helpers (`GetOrSetPropertyData`, `TryRemovePropertyData`; the compare-remove uses reference equality for array values, which the copy-on-write scheme relies on).
- Services resolve across fallback contexts. `GetServices<T>()` (`InterceptorSubjectContext.cs:41`) aggregates services from fallback contexts, every aggregated interceptor participates in the write chain, the singular `GetService<T>` throws when more than one exists, and `TryAddService`'s exists-check spans fallback contexts (`InterceptorSubjectContext.cs:133-146`).
- The canonical property handle is `PropertyReference` (subject plus name, equality and hash via `PropertyReference.Comparer`), defined in the core package Tracking already references. Its constructor accepts any (subject, name) pair; the name is validated lazily against `subject.Properties` (`PropertyReference.cs:7-28`).
- Detached subjects really are dormant: detach removes the inherited fallback context (`ContextInheritanceHandler`), leaving writes on the zero-interceptor terminal path, so no dispatch of any kind occurs until re-attach. Nothing in the repo purges foreign `Subject.Data` entries, so subject-stored listener arrays survive detach.

## Design

### The merged interceptor: `PropertyChangeInterceptor`

A single `IWriteInterceptor` named `PropertyChangeInterceptor` (matching the `...Interceptor` convention of `LifecycleInterceptor`, `SubjectTransactionInterceptor`, `ValidationInterceptor`) owns the two context-scoped delivery facets and dispatches the subject-stored per-property listeners:

1. Observable facet: the synchronized Rx `Subject<SubjectPropertyChange>` that `GetPropertyChangeObservable()` wraps.
2. Queue facet: the copy-on-write `PropertyChangeQueueSubscription[]` that `CreatePropertyChangeQueueSubscription()` feeds.
3. Per-property dispatch: reads the listener array stored on the written subject itself and invokes it by `in`-ref. The interceptor holds no per-property registration state.

The interceptor implements `IObservable<SubjectPropertyChange>` (the observable facet that `GetPropertyChangeObservable()` wraps with `.Publish().RefCount()`), `IWriteInterceptor`, and `IDisposable` (semantics below). It does NOT implement `IPropertyLifecycleHandler`.

The two context-scoped facets live in a single immutable `DispatchState` snapshot:

```
// The single volatile field for the context facets. Null means neither facet has consumers.
// Rebuilt and atomically swapped under the modification lock on every facet consumer change.
private volatile DispatchState? _state;

private sealed class DispatchState
{
    public readonly PropertyChangeQueueSubscription[] QueueSubscriptions; // never null, Array.Empty when none
    public readonly ISubject<SubjectPropertyChange>? SyncSubject;         // null = no observable consumers
}
```

Publish policy: any queue or observable consumer change always publishes a rebuilt `DispatchState` (the queue array is a readonly field of the snapshot; a conditional publish would pin a disposed subscription in every future snapshot, a leak and an active-path cost). Per-property listeners are not part of the snapshot at all.

Observable consumer tracking (both directions). The interceptor's `Subscribe(IObserver<...>)` lazily creates the `Subject` plus `Subject.Synchronize` wrapper under the modification lock, increments an observable-consumer count, publishes a snapshot with `SyncSubject` set, subscribes the observer, and returns a wrapping `IDisposable`. That wrapper is a one-shot atomic transition: the first disposal unsubscribes the inner Rx subscription, decrements the count, and, at zero, publishes a snapshot with `SyncSubject = null`, closing the gate again. `GetPropertyChangeObservable()`'s `.Publish().RefCount()` invokes exactly this pair on first downstream subscribe and last downstream dispose, so an abandoned observable cannot leave the interceptor building and pushing changes into an observer-less subject forever. A write dispatching against a stale snapshot may `OnNext` a subject whose last observer just left; that is a benign no-op race. A gate-closure test and a subscribed-then-fully-unsubscribed benchmark configuration pin this.

### Write path

```
public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context,
    WriteInterceptionDelegate<TProperty> next)
{
    // Gate: one volatile field read for the context facets, one static volatile read for the
    // process-wide listener hint. Both cache-hot. Idle (nothing anywhere) = 2 loads.
    var state = _state;
    var mayHaveListeners = PropertyChangeSubscriptions.LiveCount != 0;
    if (state is null && !mayHaveListeners)
    {
        next(ref context);
        return;
    }

    var subscriptions = state?.QueueSubscriptions ?? [];
    var syncSubject = state?.SyncSubject;

    // Listener lookup on the written subject's own small Data dictionary; only paid when the
    // process-wide count says per-property subscriptions currently exist, and skipped when
    // another interceptor instance in an aggregated chain already claimed this write's dispatch.
    PropertyChangeSubscription[]? listeners = null;
    if (mayHaveListeners && !context.ArePropertyListenersClaimed)
    {
        listeners = TryGetListeners(context.Property); // Subject.Data.TryGetValue((Name, ListenersKey))
    }

    // Applicability gate: consumers exist somewhere, but none applies to THIS write
    // (for example a listener-only deployment writing an unwatched property). Skip the build.
    if (syncSubject is null && subscriptions.Length == 0 && listeners is null)
    {
        next(ref context);
        return;
    }

    if (listeners is not null)
    {
        context.ArePropertyListenersClaimed = true; // claim the dispatch so deeper aggregated
                                                     // instances skip their lookup during next()
    }

    var oldValue = context.CurrentValue;
    next(ref context);

    // Built exactly once, reused by every applicable facet.
    var change = SubjectPropertyChange.Create(
        context.Property, context.Origin,
        context.WriteTimestampForPublishing,
        SubjectChangeContext.Current.ReceivedTimestamp,
        oldValue, context.GetFinalValue());

    if (subscriptions.Length != 0) FanOutToQueues(subscriptions, change);
    if (syncSubject is not null) syncSubject.OnNext(change);
    if (listeners is not null) DispatchToListeners(listeners, in change);
}
```

Key properties:

- Build once. The change is created a single time and reused by every facet, removing today's double `SubjectPropertyChange.Create` (duplicate holder allocations for boxed values) and double `GetFinalValue()` (double derived-getter invocation).
- Three-tier gating. (1) Two cache-hot loads exit when nothing is subscribed anywhere. (2) The applicability gate exits without building when consumers exist but none applies to this write. (3) Otherwise build once and dispatch only to the applicable facets.
- Dispatch order: queues first, then the observable, then the per-property listeners. Cross-facet ordering is not a documented public guarantee. The default order matches today's unwind order (the queue is inner relative to the observable in `WithFullPropertyTracking()` registration order). One coupling this creates, documented: a synchronous Rx observer that throws (the `ImmediateScheduler` or direct-subscribe path) unwinds before the listener dispatch and suppresses listener delivery for that write; on master the facets were separate chain links, so an observable throw could not skip the queue. Same exception-free expectation as the rest of the unwind.

### Duplicate-dispatch protection under aggregation

Because listener arrays live on the subject, every `PropertyChangeInterceptor` instance in an aggregated write chain (fallback contexts can contribute several) would read the same array and double-deliver. A per-write flag on the core `PropertyWriteContext` struct prevents that: `ArePropertyListenersClaimed` (name open), an additive INTERNAL member. Core already grants `InternalsVisibleTo` to Tracking (`Namotion.Interceptor.csproj:16`) and Tracking already consumes internal `PropertyWriteContext` members (`WriteTimestampForPublishing`), so this adds no core public API surface, changes no core snapshot, and cannot be set or cleared by external interceptors to suppress or duplicate listener delivery.

The chain threads one context by ref end to end on the writing thread, so flag mutations are visible to all instances in program order; two instances can never both claim. The first instance that resolves listeners claims the dispatch before calling `next`, so deeper aggregated instances skip the lookup during `next`. Re-entrant writes are unaffected by the flag for a simpler reason: a nested write is a new `PropertyWriteContext` with a fresh flag, and it SHOULD deliver, because it is a distinct change. The guarantee is exactly once per write context, not once per callback cascade. Queue and observable facets are unaffected: they are interceptor-owned, so aggregation intentionally fans out to each interceptor's own consumers, as today.

### Fast-path rules

- Idle gate is two cache-hot loads: the `_state` volatile field (context facets) and the process-wide listener count, a `static volatile int` (`PropertyChangeSubscriptions.LiveCount`, name open) of subscriptions created and not yet disposed, maintained with `Interlocked` on subscribe and dispose; the gate reads `LiveCount != 0`. Master comparison: the default `WithFullPropertyTracking()` configuration pays two interceptor hops with one gate load each, so the merged idle path (one hop, two loads) is at or below master's default. The queue-only master path is one hop and one load; the merged path adds one static load, which the benchmark gate must show is not measurable.
- The count restores the fast path when the feature stops being used. Subscribe does `Interlocked.Increment` before installing into `Data`; the one-shot disposal transition does `Interlocked.Decrement` after removing from `Data`, so a concurrent write never reads zero while a subscription is still installed. When every per-property subscription in the process is disposed, `LiveCount` returns to zero and writes take the zero-lookup fast path again. One honest caveat, documented: a fire-and-forgotten subscription (handle and subject dropped without `Dispose`) never decrements, so its slot persists until process exit and keeps the lookup active. This is not a new cost (a monotonic flag would have stayed set forever regardless); it is the reason to prefer disposal when you want the fast path back. A finalizer could reclaim fire-and-forget slots on GC but was rejected: finalizing every subscription adds allocation-time and extra-GC-generation overhead (against priority 2) to buy back one hot-`Data` probe per write, and finalizers for pure bookkeeping are an anti-pattern. While the count is nonzero, every intercepted write pays one lookup on the written subject's own small `Data` dictionary (usually a miss); this active-lookup state is a benchmarked configuration. No false negatives for writes that begin after `Subscribe` returns; a write racing an in-flight `Subscribe` may miss it, the same unordered subscribe/write race that exists today.
- The count is process-wide, not per-interceptor, for correctness rather than convenience. Registrations live on the subject, so dispatch must work in whatever context the subject is attached to; a per-interceptor (per-context) count would be zero on an interceptor that never saw the `Subscribe` call, so a subject that moves from context A (where it was subscribed) to context B would silently stop delivering, and subscribe-before-attach (no context, no interceptor to count on) could not work at all. Recovering those cases per-interceptor would require the interceptor to inspect subjects at attach time, which is exactly the `IPropertyLifecycleHandler` coupling this design removed, with a worse failure mode (missed deliveries, not just a leak). The gate must therefore be visible to every interceptor with zero synchronization, which a process-wide static provides. Escalation path if the active-lookup state ever measures: not per-interceptor but per-subject, a dedicated field on `IInterceptorSubject` (one cache-hot load off the object already being written, exact per-subject scoping, still no lifecycle); it was rejected here only for blast radius (core interface plus source generator), not correctness.
- Active listener lookup rides existing machinery: one `TryGetValue` on the written subject's own `Data` dictionary, a small per-subject table with entries only for written-property timestamps and listener arrays. The write terminal already performs a `Data` access per write for `SetWriteTimestamp`, so this is a second access to an already-hot structure.
- The snapshot fields are plain reads. `DispatchState` is immutable; `subscriptions.Length` is not hoisted into an extra local (`array.Length` is an immutable JIT intrinsic that is common-subexpression-eliminated).
- No `ConcurrentDictionary.IsEmpty` or `.Count` on the hot path. The `LiveCount != 0` read gates the listener lookup; `_state is null` gates the context facets.
- Performance claims at this granularity are directional; the benchmark gate (below) is the proof, and phrasing like "strictly cheaper" applies only to ARM's load-acquire savings.

### Facet isolation: pay only for what you use

- Observable facet. The `Subject` and its `Subject.Synchronize` wrapper are created lazily on the first observable subscription (under the modification lock); while no observable consumer exists, `SyncSubject` is null in the snapshot and a queue-only context never allocates the `Subject`. When the last observer unsubscribes, the snapshot is republished with `SyncSubject = null` (see Observable consumer tracking above), so transient observable use does not leave a permanent tax.
- Queue facet. `QueueSubscriptions` starts as the shared `Array.Empty<PropertyChangeQueueSubscription>()` singleton and becomes a real copy-on-write array only after the first `CreatePropertyChangeQueueSubscription()`.
- Per-property dispatch. Contexts and subjects in a process that never creates a per-property subscription pay only the static hint read.

### Listener storage on the subject

Subscriptions are stored per property in the subject's existing extension bag: `Subject.Data[(propertyName, ListenersKey)]` holds an immutable `PropertyChangeSubscription[]` (copy-on-write). Each subscription object holds the property name, a strong reference to the subject (needed only by `Dispose` for the copy-on-write removal), the observer, and an `int _disposed` flag.

- References are strong; standard `IDisposable` semantics apply, no weak references. A retained, undisposed handle keeps the subject and observer alive until disposed, exactly like any `IDisposable` that owns a resource: dispose it, or drop it. Weak references were considered and rejected because they do not deliver a real no-pin guarantee anyway: the typical observer is a closure or service that itself captures the subject, so `handle -> subscription -> observer -> subject` pins it regardless of how the back-reference is held.
- Mutation (subscribe, dispose) is not the hot path. It uses a CAS loop on the `ConcurrentDictionary`: read the current array, build the replacement, `TryUpdate` with the old array as comparand, retry on conflict; first subscription uses `TryAdd`, removal of the last uses the existing `TryRemovePropertyData(key, expectedValue)` compare-and-remove helper (reference equality on array values). Every mutation installs a freshly allocated array, so there is no ABA hazard. This is linearizable without any lock.
- Disposal is a one-shot atomic transition: `Interlocked.Exchange` on `_disposed`; only the first transition performs the copy-on-write removal, then clears the subscription's subject and observer references with `Volatile.Write` (pairing with the dispatch-side `Volatile.Read`) so a disposed handle pins neither, then `Interlocked.Decrement`s `LiveCount` (after the `Data` removal). Repeated or concurrent disposal is a no-op, so the count decrements exactly once per subscription and never goes negative.
- Dispatch captures the observer into a strong local, then invokes the local: `var observer = Volatile.Read(ref subscription.Observer); if (observer is not null) observer.OnChange(in change);`. Because dispose clears the observer field with `Volatile.Write`, the captured local is either a still-valid observer (invoke) or null (the subscription was disposed and cleared; skip). Capturing before use is what makes this null-safe despite the concurrent clear; a naive "check the disposed flag, then dereference the field" would race the clear and throw a `NullReferenceException` into the write chain after the write already committed. Trailing-invocation bound: at most one trailing invocation per dispatch that was already in flight when disposal cleared the field. This is per-dispatch, not global: with N writes racing a dispose, up to N dispatches may each have captured the observer before the clear and each invoke once; bounding it to one globally would require serialization the design deliberately avoids. This is best-effort, the same semantics as Rx's in-flight `OnNext` during unsubscribe. Per-consumer volatile reads during fan-out are parity with master, whose queue fan-out already performs one volatile `_completed` read per subscription in `Enqueue` (`PropertyChangeQueueSubscription.cs:17,31`).

Ownership and lifetime (documented in `docs/tracking.md` and the XML docs):

- The subject holds its subscriptions. A subscription keeps its observer (and whatever the observer captures) alive as long as the subject lives or the handle is retained undisposed, whichever is longer; the standard `IDisposable` rule applies. Fire-and-forget works: drop the handle without disposing and the subject, its `Data`, the subscription array, and the observers form a reference cycle with no external root and are collected together (the process-wide count is a number, not a reference; a fire-and-forgotten subscription simply never decrements it, keeping the process-wide lookup active, whereas disposing restores the fast path). Retain the handle and you must dispose it when done, exactly as an `INotifyPropertyChanged` `-=` must eventually run. There is no unconditional no-pin guarantee: an observer that captures the subject pins it through a retained handle. This is the forgotten-handler caveat in a different shape.
- Detach and re-attach: while the subject is detached (or attached to a context without a `PropertyChangeInterceptor`), writes bypass interception and the subscription is dormant; delivery resumes on re-attach, in whatever context the subject lands. Subscribing before first attach is valid for the same reason.
- No detach notification and no automatic teardown exist or are needed. If an observer must know when its subject leaves the graph, that is the existing lifecycle API's job.

### Per-property subscription API (Tracking-only surface)

```csharp
public interface IPropertyChangeObserver
{
    void OnChange(in SubjectPropertyChange change);
}

public delegate void PropertyChangeCallback(in SubjectPropertyChange change);

// Strongly typed. The expression resolver is Tracking-local (see below), no Registry dependency.
IDisposable SubscribeToProperty<TSubject, TValue>(
    this TSubject subject,
    Expression<Func<TSubject, TValue>> propertySelector,
    IPropertyChangeObserver observer) where TSubject : IInterceptorSubject;

IDisposable SubscribeToProperty<TSubject, TValue>(
    this TSubject subject,
    Expression<Func<TSubject, TValue>> propertySelector,
    PropertyChangeCallback callback) where TSubject : IInterceptorSubject;

// Low level, resolver-free.
IDisposable Subscribe(this PropertyReference property, IPropertyChangeObserver observer);
IDisposable Subscribe(this PropertyReference property, PropertyChangeCallback callback);
```

Subscribe touches only the subject: it installs the subscription into `Subject.Data` and increments the process-wide count (increment before install). It does not resolve, and does not require, any interceptor or context; a subject without a notifying context accepts subscriptions that stay dormant until it is attached (documented). Subscribe validates the target against `subject.Properties`: it throws for an unknown member, and for a member that is neither intercepted nor derived, whose writes never enter the chain and would otherwise produce a subscription that silently never fires. The accept condition is `metadata.IsIntercepted || metadata.IsDerived` (`SubjectPropertyMetadata.cs:38,48`). Derived properties must be accepted even though an ordinary `[Derived]` getter is non-partial and therefore has `IsIntercepted == false`: `DerivedPropertyChangeHandler` re-enters the write chain when a derived value recalculates, so listeners on derived properties do fire. Rejecting all non-intercepted members (an earlier draft) would have rejected exactly the derived properties this paragraph calls valid.

Subscription semantics: instance, not path. A subscription binds to a concrete subject instance plus property name and follows the instance, not its location in the object graph. If the instance is re-parented, the subscription keeps firing. If the instance is replaced at some path (`garage.Car = otherCar`), the subscription stays with the old instance and does not transfer; watching "whatever is at this path" is a different, higher-level feature that would compose per-property subscriptions on the structural segments and belongs in Registry, out of scope here.

The strongly-typed resolver is Tracking-local and strict about expression shape: unwrap any `Convert` boxing node, then require the body to be a `MemberExpression` whose `.Expression` is the lambda's own `ParameterExpression`, take `.Member.Name`, and build `new PropertyReference(subject, name)`. The parameter-target check is essential: `PropertyReference` accepts any (subject, name) pair and validates the name only lazily, so without it a chained selector `m => m.Child.Speed` or a captured-variable selector `m => other.Speed` would resolve to the leaf name `Speed` and, if the root subject also has a `Speed`, silently subscribe to the wrong property. Chained, captured-variable, and static-member selectors therefore throw a clear error; only direct `m => m.Foo` is supported. The Registry's `TryGetRegisteredProperty` resolver is not used because Registry depends on Tracking (package cycle).

Delivery is by `in`-ref. `SubjectPropertyChange` is a large readonly struct (easily 100 bytes or more), so by-ref delivery avoids the per-call copy; `IObserver<T>.OnNext(T)` is by value by signature, which is why the core cannot be `IObservable<T>`. The interface overload lets a service be its own observer with zero closure allocation; the delegate overload wraps into an internal adapter, one allocation at subscribe time, none per event.

### Concurrency contract for callbacks

Per-property callbacks run synchronously within the write operation on the writing thread, outside the subject lock (dispatch is post-`next`; the `SyncRoot` lock covers only the innermost field write). Under a transaction, capture defers delivery: callbacks run at commit replay, on the committing thread; staged writes never reach listeners during capture. On rollback (or best-effort commit failure), inverse writes replay through the full chain, so listeners observe apply-and-revert pairs, the same visibility the queue and observable have today; a watchdog or dirty-flag consumer must not interpret a revert delivery as a user change (documented).

Unlike the observable (serialized through `Subject.Synchronize`), per-property callbacks are not serialized:

- Two racing writes to the same property can invoke the same observer concurrently.
- One observer shared across several subjects can be invoked concurrently from writes to different subjects.

The contract, documented in `docs/tracking.md` and the XML docs on `Subscribe`, `SubscribeToProperty`, and `IPropertyChangeObserver` (the out-of-order and re-read points below also go on the queue and observable entry points, see the docs task):

- Thread-safe. Observers may be invoked concurrently and must tolerate it.
- Fast and non-blocking. A slow callback stalls the writer itself.
- Must not throw. Dispatch runs post-`next` inside the write chain, so an exception from a callback propagates up and can skip later unwind work (lifecycle reconciliation, derived recalculation) for a write that already committed. Observers must not throw, exactly the `ILifecycleHandler` contract ("must be exception-free"). Deliberately not caught, to keep the dispatch hot path free of per-callback try/catch; the contract is documented instead, and a test pins that exceptions propagate rather than being swallowed.
- Delivered possibly out of commit order. Callbacks are synchronous, but "synchronous" is per-write: each runs inline on its own writer's thread. Racing writers commit under `SyncRoot` in a definite order (A then B), but dispatch runs post-`next`, after the lock is released, so a thread that committed first can be preempted and dispatch last: commit order A then B, delivery order B then A, and tick-granular timestamps cannot disambiguate. An observer that needs the current value must re-read the property rather than trust the callback's `newValue`. This is not new (the queue and even the `Subject.Synchronize` observable are first-come-first-served at their gate, so serialized is not commit-ordered either), and it is inherent to a single writer being in order while concurrent writers are not. It is not fixed by dispatching under the lock: callbacks would then run holding `SyncRoot`, blocking all reads and writes of that subject for the callback's duration and inviting lock-ordering deadlocks when a callback touches another subject, which violates the codebase's getters-and-callbacks-outside-locks discipline; the only order-restoring alternative (per-property sequence numbers plus a consumer-side reordering buffer) reintroduces exactly the per-consumer queueing this feature avoids. This matters only for "cache the latest value" semantics; a watchdog or dirty flag reacts to any change and is unaffected.

A consumer that needs off-thread or serialized processing hands off in one line and owns its own queue or `Channel`:

```csharp
property.Subscribe((in SubjectPropertyChange c) => _channel.Writer.TryWrite(c));
```

Re-entrancy is safe: a callback may write properties and re-enter the write chain (dispatch holds no lock and iterates an immutable array snapshot; a nested write is a new context and delivers as its own change). Note the same is already true of a synchronous Rx observer today.

### Registration, ordering, and aggregation

Single dual-facet registration. There are no facet flags. A `PropertyChangeInterceptor` always carries both the queue and observable facets (each still allocation-free until first used: the `Subject` is lazy, the queue array is the empty singleton). One method registers it, `WithPropertyChangeNotifications()`, through the existing lock-protected `TryAddService(() => new PropertyChangeInterceptor(), exists: _ => already present)`, so the registration is atomic and idempotent with no new container capability. `WithFullPropertyTracking()` calls `WithPropertyChangeNotifications()`. The separate `WithPropertyChangeObservable()` and `WithPropertyChangeQueue()` methods are removed (breaking change; migration below). This deletes the entire facet-scoping design of the prior revision (per-facet flags, local-versus-inherited merge logic, the own-services accessor, and facet-filtered resolution), because a repo-wide audit found no product code that registers a partial facet: the only non-test callers of the old per-facet methods were the two lines inside `WithFullPropertyTracking()`, and every `AddFallbackContext` site chains full-tracking contexts, never split ones. The split-facet topology those methods enabled was never used.

Resolution is singular. `GetPropertyChangeObservable()` and `CreatePropertyChangeQueueSubscription()` resolve the interceptor with singular `GetService<PropertyChangeInterceptor>()`. With one context that is the sole instance; under multi-context aggregation (a subject whose context aggregates several full-tracking contexts) singular resolution throws, which is exactly master's behavior today (master's `GetService<PropertyChangeQueue>()`/`GetService<PropertyChangeObservable>()` throw there too). Corrections, when that path lands, use plural `GetServices<PropertyChangeInterceptor>()` for the same reason the change-origin design gives.

Accepted deviation, documented (the sole behavioral carve-out of the collapse). A consumer that deliberately built the old split topology (observable-only parent, queue-only child) sees one of two changes, by registration order. If the child context is chained to the parent before the child registers, the child's `WithPropertyChangeNotifications()` is a no-op (the exists-check spans fallbacks, `InterceptorSubjectContext.cs:133-146`) and the child inherits the parent's dual-facet interceptor, so a queue subscription created on the child observes the parent's writes too. If the child registers before being chained, both contexts hold their own interceptor and the child's `CreatePropertyChangeQueueSubscription()`/`GetPropertyChangeObservable()` throw on singular resolution (two same-type instances) where master's two distinct types each resolved singly. Crucially the throw flavor is not a divergence for the way facets are actually used: with full tracking on both contexts (the combined usage everywhere in the repo), master already throws under the same aggregation, because the child's own `PropertyChangeObservable` and the inherited parent's are two instances of one type and `GetService<PropertyChangeObservable>()` throws exactly as `GetService<PropertyChangeInterceptor>()` does now. The merge diverges from master only in the split topology, which master supported solely as a side effect of the two facets being distinct types. Neither flavor occurs in product code (verified: every `AddFallbackContext` site chains full-tracking contexts). For the inherit flavor, `ChangeQueueProcessor`-shaped consumers filter by source/property so the surplus is dropped, not misdelivered; a raw `CreatePropertyChangeQueueSubscription()` consumer that does not property-filter (the sample profilers) would see the broadened stream, but those subscribe at the root context and are firehose-by-intent. Listed alongside the other carve-outs.

Interceptor ordering. `SubjectTransactionInterceptor` currently declares `[RunsBefore(typeof(PropertyChangeObservable))]` and `[RunsBefore(typeof(PropertyChangeQueue))]` (`SubjectTransactionInterceptor.cs:14-15`). Both are deleted and the ordering is expressed target-side instead: `PropertyChangeInterceptor` declares `[RunsAfter(typeof(SubjectTransactionInterceptor))]`. Target-side matters under aggregation: `ServiceOrderResolver`'s type-to-index map keeps only the last instance per type (`ServiceOrderResolver.cs:115-117`), so a source-side `RunsBefore` on the transaction interceptor would order it before only one of several aggregated interceptor instances; `RunsAfter` edges are built per service instance (`ServiceOrderResolver.cs:136-143`), so every `PropertyChangeInterceptor` instance is ordered after the transaction interceptor. The transaction interceptor's `[RunsBefore(typeof(DerivedPropertyChangeHandler))]` is unchanged. Residual duplicated-transaction hole, documented: with two or more aggregated `SubjectTransactionInterceptor` instances, each `RunsAfter` edge binds to only one of them; that ordering cannot leak staged writes in practice only because capture's context-binding validation calls the singular `TryGetService<SubjectTransactionInterceptor>()` (`SubjectTransactionInterceptor.cs:65`), which throws under such aggregation before any dispatch. The resolver's duplicate-type limitation itself is pre-existing on master (it affects today's `PropertyChangeObservable`/`PropertyChangeQueue` edges and `ValidationInterceptor` the same way) and is a follow-up issue, not fixed here.

Per-property listeners are aggregation-neutral: `Subscribe` involves no interceptor at all, and the dedup flag guarantees once-per-write delivery regardless of chain composition.

### Interceptor disposal

`PropertyChangeInterceptor.Dispose()` disposes the queue facet and nothing else, which is exact parity: today's `PropertyChangeQueue.Dispose` never affected the observable because the observable was a separate, non-disposable object.

- Queue facet: completes all current queue subscriptions and wakes blocked `TryDequeue` consumers, exactly today's behavior. Future `CreatePropertyChangeQueueSubscription()` calls throw `ObjectDisposedException` (a deliberate deviation: today's `Dispose` does not mark the queue disposed and a later `Subscribe()` accidentally works; that is judged an artifact, not a contract).
- Observable facet: fully unaffected. Existing observers keep receiving changes and new observers can still subscribe; the post-dispose snapshot is rebuilt with an empty queue array and the `SyncSubject` preserved. Silently starving live observers, or completing them, would both be behavior changes.
- Per-property listeners: untouched; they belong to subjects, not to the interceptor.
- In-flight dispatches complete against their captured snapshot.

While rewriting the subscription internals, fix two latent races on master. First, the enqueue-versus-dispose race: `Dispose` eagerly disposes the `ManualResetEventSlim` while a concurrent producer's `Enqueue` can still call `Set()` on it, throwing `ObjectDisposedException` into the write chain (`PropertyChangeQueueSubscription.cs:31-37` versus `:99-105`). The rewrite must not dispose the signal while producers can still observe the subscription (drop the eager `_signal.Dispose()` or guard the `Set`), and disposal itself becomes a one-shot atomic transition (`Interlocked.Exchange` instead of today's non-atomic check-then-act at `:94-99`). Second, the completion lost-wakeup: `TryDequeue` checks `_completed`, then `Reset()`s the signal, then `Wait()`s, so if `Dispose` sets `_completed` and signals between the check and the `Reset()`, the reset clears the wake and the consumer blocks until its cancellation token fires instead of returning on completion. Re-check `_completed` after `Reset()` and before `Wait()` (the code already re-checks the queue there). Both fixes get an orchestrated dispose-during-wait concurrency test.

### Public API changes

Removed public types: `PropertyChangeObservable`, `PropertyChangeQueue`. Removed public extension methods: `WithPropertyChangeObservable()`, `WithPropertyChangeQueue()` (breaking; callers migrate to `WithPropertyChangeNotifications()`).

Retained: `PropertyChangeQueueSubscription` (unchanged consumer usage; internals repoint at the interceptor). Its constructor becomes `internal` and takes `PropertyChangeInterceptor` (decided): the type stays public as the handle you get back, but only Tracking constructs it, and consumers create subscriptions through `CreatePropertyChangeQueueSubscription()`. This is the correct call, not just a minimal-surface preference: today's public ctor is the sole external footgun in this area, because it does not register the subscription (that happens in the queue's `Subscribe()`), so `new PropertyChangeQueueSubscription(queue)` produces a dead subscription that receives nothing and whose `Dispose()` is a no-op; the only in-repo caller is the queue's own `Subscribe()`. The parameter type changes regardless, so no external source-compat is preserved by keeping it public. The public ctor line leaves the API snapshot. Also retained unchanged: `GetPropertyChangeObservable()` and `CreatePropertyChangeQueueSubscription()` (they now resolve the single dual-facet interceptor).

New public surface: `PropertyChangeInterceptor` (public parameterless constructor, always dual-facet; implements `IWriteInterceptor`, `IObservable<SubjectPropertyChange>`, `IDisposable`), `WithPropertyChangeNotifications()`, `IPropertyChangeObserver`, `PropertyChangeCallback`, and the `Subscribe` / `SubscribeToProperty` extension methods. The `PropertyWriteContext.ArePropertyListenersClaimed` member is internal (no core public API change).

Snapshots: the `VerifyChecksTests.PublicApi.verified.txt` snapshot for `Namotion.Interceptor.Tracking` is updated; the core snapshot is unchanged.

### Forward compatibility (out of scope now)

Value-assertion corrections (the approved `docs/superpowers/specs/2026-07-12-change-origin-design.md`) are not implemented yet and are out of scope here. Noted only so this merge stays compatible: when that path lands, a `Correction` must inject through the interceptor's queue facet only (fan out to `DispatchState.QueueSubscriptions`), resolved via `GetServices<PropertyChangeInterceptor>()` (plural, to cover aggregation), and must never reach the observable or the per-property listeners, since both are application-level consumers and a correction is not a model change. Corrections do not flow through `WriteProperty`, so the listener path never sees them by construction.

## Constraints (hard)

1. No functionality removed for the way the library is actually used; the one exception is the deliberately-built split-facet topology, relaxed and documented as carve-out (c) below. `GetPropertyChangeObservable` (with all schedulers), `CreatePropertyChangeQueueSubscription`, `PropertyChangeQueueSubscription.TryDequeue`, the unbounded isolated per-subscription queue semantics, all documented thread-safety guarantees, `ChangeQueueProcessor`, and all connector and source behavior keep working with identical observable behavior, with three documented carve-outs: (a) unordered subscribe/write races may resolve differently; (b) a queue subscription created synchronously inside a write (from within the write chain, same thread) no longer receives the triggering change, because dispatch uses the pre-`next` snapshot while master re-reads the array post-`next` (`PropertyChangeQueue.cs:80`); this same-thread case is deterministic on master, so it is a real, accepted, documented behavior change (no in-repo consumer does this; the lifecycle-attach `ChangeQueueProcessor` pattern runs in post-`next` unwind and is unaffected); and (c) a deliberately-built split-facet topology (observable-only on one context, queue-only on a chained one) either broadens a queue subscription's audience or throws on singular resolution depending on registration order, per the Registration section. Carve-out (c) is the sole facet-collapse deviation, does not occur in product code, and is at parity with master for the combined full-tracking usage that does occur (master throws under the same aggregation).
2. No performance regression, on idle and active paths, including single-facet activity (only one facet in use), proven by the benchmark gate. Directional claims: idle two cache-hot loads versus master default's two gate loads plus an extra interceptor hop; active listener lookup rides the already-paid per-subject `Data` access pattern; build-once makes the both-active case faster. The active-lookup state (count nonzero, no listener on the written property) and the observable subscribed-then-fully-unsubscribed state are explicit benchmark configurations.
3. Super-cheap fast paths as specified in Fast-path rules; no `ConcurrentDictionary.IsEmpty` or `.Count` on the hot path.

## Out of scope (YAGNI)

- Built-in asynchronous delivery for per-property subscriptions (hand off to your own `Channel`, or use the queue facet).
- A per-property Rx adapter (`property.AsObservable()`); `GetPropertyChangeObservable().Where(...)` covers Rx composition.
- A per-subject index (all properties of one subject).
- Path-based subscriptions ("whatever is at `Root.Garage.Car.Speed`"); composable later at the Registry level from this primitive.
- Detach notifications for subscribers; dormancy and revival make them unnecessary, and the existing lifecycle API covers subjects leaving the graph.
- Value-assertion corrections (see Forward compatibility).

## Testing and verification

Behavior preservation (the merge). The criterion is behavior preservation after a mechanical migration off the removed types, not "unchanged sources".

Migration surface:
- All call sites of the removed `WithPropertyChangeObservable()` / `WithPropertyChangeQueue()` extension methods migrate to `WithPropertyChangeNotifications()`. A grep found these only in tests (Connectors.Tests, Tracking.Tests, Registry.Tests, WebSocket.Tests) plus the two lines inside `WithFullPropertyTracking()`; mechanical replacement, and a queue-only or observable-only test simply gains the unused other facet (free). The public API snapshot loses the two methods and gains `WithPropertyChangeNotifications()`.
- `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs` (`new PropertyChangeQueue()` at 7 sites -> `new PropertyChangeInterceptor()`, plus the matching `AddService`).
- `src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeQueueTests.cs` (rename the file and class; migrate `context.GetService<PropertyChangeQueue>().Subscribe()` at `:211` to `context.CreatePropertyChangeQueueSubscription()`). Note: the queue's low-level `Subscribe()`/`Unsubscribe()` methods are removed with the type and are NOT re-exposed on `PropertyChangeInterceptor` (which already has `Subscribe(IObserver<...>)` for the observable facet; a second parameterless queue `Subscribe()` would be confusing). Consumers create queue subscriptions via `CreatePropertyChangeQueueSubscription()` and end them via `PropertyChangeQueueSubscription.Dispose()`.
- `src/Namotion.Interceptor.Benchmark/SubjectSourceBenchmark.cs` (`GetService<PropertyChangeQueue>()`).
- `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionLifecycleTests.cs:116-133` (asserts on the literal type name `"PropertyChangeObservable"` in interceptor ordering; retarget to `PropertyChangeInterceptor`, or supersede with the new ordering tests).
- `PropertyChangeQueueSubscription`'s constructor parameter.

After migration:
- All existing `Namotion.Interceptor.Tracking` tests pass.
- All connector and source tests that depend on the queue pass (OPC UA, MQTT, WebSocket servers, `SubjectSourceBase`, `ChangeQueueProcessor`).
- Transaction ordering: the transaction interceptor runs before `PropertyChangeInterceptor` under both registration orders and with multiple fallback-context interceptor instances.
- Single interceptor: `WithFullPropertyTracking()`, and `WithPropertyChangeNotifications()` called more than once, register exactly one `PropertyChangeInterceptor` per context, with one build per write when both facets are active.
- Aggregation parity: a subject whose context aggregates two full-tracking contexts makes `GetPropertyChangeObservable()` and `CreatePropertyChangeQueueSubscription()` throw (singular resolution over two same-type instances), matching master's behavior with the two removed types.
- Interceptor disposal: queue subscriptions complete and wake; existing observable subscribers keep receiving changes after `Dispose`; new observable subscribers still work; `CreatePropertyChangeQueueSubscription` throws.
- Gate closure: after the last observable subscriber unsubscribes (and no other consumers exist), writes take the fast path again (no build, no `OnNext`).

New per-property tests (`When<Condition>_Then<ExpectedBehavior>` naming, Arrange/Act/Assert). Static-state isolation: tests that assert on the process-wide count or on fast-path behavior run in a dedicated, non-parallel xUnit collection and use an internal reset hook for the count (Tracking grants `InternalsVisibleTo` to its test project); without this, any concurrently-running test that subscribes would poison fast-path assertions.

- Sync in-ref dispatch to a single listener and to multiple listeners for one property.
- Indexed isolation: a write to property B never invokes a listener registered for property A.
- Re-entrancy: a listener that writes another property inside its callback does not deadlock and delivers correctly; delivery is exactly once per write context (a re-entrant write is its own context and delivers as its own change).
- Manual dispose stops delivery; repeated and concurrent disposal is a no-op after the first transition; a disposed handle no longer references the subject or observer.
- Dispatch-versus-dispose safety: a write dispatching while a subscription is concurrently disposed (observer field cleared) never throws (the observer is captured into a local first); the trailing-invocation bound is per in-flight dispatch, so N writes racing one dispose may each deliver one trailing call to a still-valid observer, not one globally.
- Dormancy and revival: subscribing before attach delivers nothing until the subject attaches, then delivers; detach silences delivery; re-attach (same or different context with a `PropertyChangeInterceptor`) resumes delivery with the same subscription.
- Aggregation: with multiple fallback contexts each contributing an interceptor, a per-property subscription delivers exactly once per write.
- Transactions: staged writes do not reach listeners during capture; delivery happens at commit replay; rollback replays inverse writes and listeners observe the apply-and-revert pair (documented visibility).
- Lifetime: a dropped subject with a dropped (undisposed) handle is collectable together with its subscriptions and observers (rootless cycle; no global retention; the count is a number, not a reference). No claim is tested that a retained undisposed handle fails to pin the subject, because it does pin it (standard `IDisposable`).
- Concurrency: concurrent writes to one property invoke a shared observer concurrently (documented, not serialized); concurrent subscribe, dispose, and write are race-free (CAS loop loses no registrations; no delivery after disposal beyond the documented best-effort trailing call, checked via `Volatile.Read`).
- Callback exception contract: a throwing observer is not caught (documented must-not-throw); a test pins that the exception propagates rather than being swallowed.
- Strongly-typed `SubscribeToProperty(x => x.Foo, ...)` resolves the correct property; an unknown member throws; a member that is neither intercepted nor derived throws; a chained selector (`x => x.Child.Value`), a captured-variable selector, and a static-member selector throw rather than silently binding the leaf name.
- Derived subscription: subscribing to a `[Derived]` property (which is non-partial, `IsIntercepted == false`, `IsDerived == true`) is accepted and fires when the derived value recalculates.
- Queue completion: an orchestrated dispose-during-`TryDequeue`-wait test proves the consumer returns on completion rather than blocking until cancellation (lost-wakeup fix), and that a concurrent `Enqueue` during `Dispose` never throws `ObjectDisposedException`.
- Count bookkeeping: `LiveCount` returns to zero after all subscriptions are disposed (restoring the fast path); a fire-and-forgotten (never-disposed) subscription does not decrement it; repeated and concurrent disposal decrement exactly once and never go negative; the internal reset hook restores it for test isolation.

Fast-path, allocation, and layering:

- No `SubjectPropertyChange.Create`, no `Data` listener lookup, and no snapshot reads occur when `_state` is null and the count is zero; no build occurs when consumers exist but none applies to the written property (white-box or allocation assertion, inside the serialized collection).
- No `ConcurrentDictionary.IsEmpty` or `.Count` on the write path.
- The feature compiles with no `Namotion.Interceptor.Registry` reference.

Performance (benchmark-gated):

- Benchmark the write path in `Namotion.Interceptor.Benchmark` for: idle, queue-only active, observable-only active, both-active, listener-on-written-property, listener-elsewhere (count nonzero, lookup miss), and observable-subscribed-then-fully-unsubscribed, before and after, proving no regression and confirming the build-once improvement.

Public API and docs:

- Update and accept the `VerifyChecksTests.PublicApi.verified.txt` snapshot for `Namotion.Interceptor.Tracking` (core snapshot unchanged; the dedup flag is internal).
- XML docs on every notification entry point must document the out-of-order-under-concurrent-writes behavior and the re-read-for-current-value guidance, because it applies to all three delivery paths (and has existed unstated on master's queue and observable): `GetPropertyChangeObservable()`, `CreatePropertyChangeQueueSubscription()` and `PropertyChangeQueueSubscription`, and `Subscribe`/`SubscribeToProperty`/`IPropertyChangeObserver`. Wording, roughly: "Under concurrent writes to the same property, notifications may arrive out of commit order (dispatch runs outside the subject lock); if you need the current value, re-read the property rather than relying on the delivered new value." The per-property XML docs additionally carry the not-serialized, must-not-throw, and dispose/lifetime points.
- Update `docs/tracking.md`: the merged interceptor, the per-property subscription API, the concurrency contract (not serialized, must not throw, possible out-of-order delivery, re-read for current value, transaction commit-replay and rollback visibility), the ownership/lifetime rule (standard `IDisposable`: dispose or drop the handle; a retained undisposed handle pins the subject, and an observer that captures the subject pins it too), dormancy/revival semantics, instance-not-path subscription semantics, and the note that a throwing synchronous Rx observer suppresses listener delivery for that write. Also fix the dangling references to removed APIs in other docs: `docs/tracking-transactions.md:525` (`WithPropertyChangeObservable()` -> `WithPropertyChangeNotifications()`) and `docs/blazor.md:171` (the `PropertyChangeObservable` type in the tracking-flow diagram -> `PropertyChangeInterceptor`). `docs/graphql.md:164` needs no change (it uses the retained `GetPropertyChangeObservable()`).

## Open questions

No blocking decisions remain. The one non-blocking item is secondary naming, to settle during implementation and bikeshed-only: `IPropertyChangeObserver`, `PropertyChangeCallback`, `SubscribeToProperty`, the `Data` key constant, the internal flag name (`ArePropertyListenersClaimed`), and the static count holder (`PropertyChangeSubscriptions.LiveCount`). The class name `PropertyChangeInterceptor` and the `PropertyChangeQueueSubscription` internal-ctor decision are settled.
