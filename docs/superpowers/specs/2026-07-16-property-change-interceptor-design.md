# Unified Property Change Interceptor with Subject-Stored Per-Property Subscriptions

Status: design approved, ready for implementation plan
Date: 2026-07-16 (revised 2026-07-17: per-property registrations moved from an interceptor-owned registry to subject-stored data)
Target packages: `Namotion.Interceptor.Tracking` (primary) plus one additive member on a core struct (`PropertyWriteContext`)

## Overview

Merge the two existing property-change delivery interceptors (`PropertyChangeObservable` and `PropertyChangeQueue`) into a single `PropertyChangeInterceptor`, and add a new synchronous per-property subscription API. A feature that cares about one or a few specific properties (a heartbeat, a watchdog, a dirty flag) registers an indexed, allocation-free callback and needs no observer, no queue, no thread, and no background service of its own.

Per-property registrations are stored on the subject itself (in `IInterceptorSubject.Data`), not in a registry owned by the interceptor. The callbacks live with the subject, like `INotifyPropertyChanged` handlers live with their source object. Consequences, all deliberate:

- No lifecycle coupling. The interceptor does not implement `IPropertyLifecycleHandler`; there is no detach-time cleanup, no hybrid disposal, and `WithLifecycle()` is not required for anything in this feature.
- Subscriptions survive detach and re-attach ("revival"), survive context moves, and can be created before the subject is first attached. While the subject is not attached to a context with a `PropertyChangeInterceptor`, the subscription is dormant: writes bypass interception, so nothing fires; delivery resumes when the subject is attached again.
- Ownership is garbage-collector clean. The subject holds its subscriptions; dropping the subject collects them together with it. The lifetime rule is the standard .NET event rule: a subscription keeps its observer alive as long as the subject lives, until the consumer disposes it. This is documented, not managed by machinery.

This is a breaking change to the public API of `Namotion.Interceptor.Tracking` (plus one additive core-struct member). It is acceptable at the current version (0.0.2) under three hard constraints:

1. No functionality is removed. Everything that works today keeps working, with an unchanged consumer-facing surface where practical.
2. No performance regression, on idle and active paths, including single-facet configurations (queue-only, observable-only), proven by benchmarks.
3. Fast paths stay super cheap, as specified below.

## Motivation

Both existing delivery paths are context-wide firehoses:

- `PropertyChangeObservable` (Rx). Each `GetPropertyChangeObservable()` call builds its own `.Publish().RefCount()` chain, so downstream subscribers of one returned instance share that instance's upstream subscription, but separate calls do not. Every subscriber still receives every change and evaluates its own predicate. With many features each watching one property this is O(features x changes) filtering.
- `PropertyChangeQueue` (high performance). `CreatePropertyChangeQueueSubscription()` gives each consumer an isolated `ConcurrentQueue` plus a dedicated consumer thread. This is heavy: a whole thread and queue per feature.

There is no indexed per-property subscription. A feature that watches a single property today pays for a full firehose subscription (and often a background service).

The two interceptors also run as two separate links in the write chain. On every write where both are active (the `WithFullPropertyTracking()` default), the change payload `SubjectPropertyChange` is built twice (two independent `SubjectPropertyChange.Create` calls, which for boxed or large-struct or reference-type values means duplicate holder allocations, and two `GetFinalValue()` calls, which for derived properties means the getter runs twice) and the write pays two interceptor hops.

## Current Architecture (for reference)

Both classes live in `src/Namotion.Interceptor.Tracking/Change/`. Both implement `IWriteInterceptor` and are registered independently as services. Both are public and appear in the public API snapshot.

- `PropertyChangeObservable : IObservable<SubjectPropertyChange>, IWriteInterceptor`. Gate: `if (!_subject.HasObservers) { next(ref context); return; }`. Builds the change, calls `_syncSubject.OnNext(change)` where `_syncSubject = Subject.Synchronize(_subject)`.
- `PropertyChangeQueue : IWriteInterceptor, IDisposable`. Gate: `if (subscriptions.Length == 0) { next(ref context); return; }`. Builds the change, fans it out to a copy-on-write `PropertyChangeQueueSubscription[]`. Subscribe and unsubscribe mutate the array under a `Lock` (`PropertyChangeQueue.cs:13`). `Dispose()` completes all current subscriptions and wakes blocked `TryDequeue` consumers (`PropertyChangeQueue.cs:87-95`).
- `PropertyChangeQueueSubscription : IDisposable`. The consumer-facing pull handle (`TryDequeue`), returned by `CreatePropertyChangeQueueSubscription()`.

Facts that shape the design:

- The write chain holds the subject lock only around the innermost field write. `WriteInterceptorFactory.cs:29-37` wraps only `innerWriteValue(...)` plus origin/timestamp finalization in `lock (context.Property.Subject.SyncRoot)`. Every interceptor's post-`next` code (all notification dispatch) runs outside that lock, on the writing thread.
- The write terminal already performs one subject-data dictionary operation per write: `SetWriteTimestamp` does `Subject.Data.GetOrAdd((Name, WriteTimestampKey), ...)` unconditionally (`WriteInterceptorFactory.cs:20,35` calling `PropertyReference.cs:107-111`). Per-subject tuple-keyed dictionary access is therefore already baseline hot-path cost, with existing public helpers (`GetOrSetPropertyData`, `TryRemovePropertyData`).
- Services resolve across fallback contexts. `GetServices<T>()` (`InterceptorSubjectContext.cs:41`) aggregates services from fallback contexts, so a context can expose more than one interceptor of a given type, every one participates in the write chain, and the singular `GetService<T>` throws when more than one exists.
- The canonical property handle is `PropertyReference` (subject plus name, equality and hash via `PropertyReference.Comparer`), defined in the core package Tracking already references. Its constructor accepts any (subject, name) pair; the name is validated lazily against `subject.Properties` (`PropertyReference.cs:7-28`).

## Design

### The merged interceptor: `PropertyChangeInterceptor`

A single `IWriteInterceptor` named `PropertyChangeInterceptor` (matching the `...Interceptor` convention of `LifecycleInterceptor`, `SubjectTransactionInterceptor`, `ValidationInterceptor`) owns the two context-scoped delivery facets and dispatches the subject-stored per-property listeners:

1. Observable facet: the synchronized Rx `Subject<SubjectPropertyChange>` that `GetPropertyChangeObservable()` wraps.
2. Queue facet: the copy-on-write `PropertyChangeQueueSubscription[]` that `CreatePropertyChangeQueueSubscription()` feeds.
3. Per-property dispatch: reads the listener array stored on the written subject itself and invokes it by `in`-ref. The interceptor holds no per-property registration state.

The interceptor implements `IDisposable` (semantics below). It does NOT implement `IPropertyLifecycleHandler`.

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

### Write path

```
public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context,
    WriteInterceptionDelegate<TProperty> next)
{
    // Gate: one volatile field read for the context facets, one static volatile read for the
    // process-wide listener presence hint. Both cache-hot. Idle (nothing anywhere) = 2 loads.
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
    // process-wide hint says per-property subscriptions exist at all, and skipped for outer
    // interceptors when an inner one already dispatched (see dedup below).
    PropertyChangeSubscription[]? listeners = null;
    if (mayHaveListeners && !context.ArePropertyListenersNotified)
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
        context.ArePropertyListenersNotified = true; // claim dispatch before next() so re-entrant
                                                     // and outer interceptors cannot double-deliver
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

- Build once. The change is created a single time and reused by every facet, removing today's double `SubjectPropertyChange.Create` (duplicate holder allocations for boxed values) and double `GetFinalValue()` (double derived-getter invocation, which today can even publish different values to the two facets for one write).
- Three-tier gating. (1) Two cache-hot loads exit when nothing is subscribed anywhere. (2) The applicability gate exits without building when consumers exist but none applies to this write. (3) Otherwise build once and dispatch only to the applicable facets.
- Dispatch order: queues first, then the observable, then the per-property listeners. Cross-facet ordering is not a documented public guarantee. The default order matches today's unwind order (the queue is inner relative to the observable in `WithFullPropertyTracking()` registration order).

### Duplicate-dispatch protection under aggregation

Because listener arrays live on the subject, every `PropertyChangeInterceptor` instance in an aggregated write chain (fallback contexts can contribute several) would read the same array and double-deliver. A per-write flag on the core `PropertyWriteContext` struct prevents that: `ArePropertyListenersNotified` (name open), an additive member. The first interceptor that resolves listeners for the write claims the dispatch by setting the flag before calling `next`; interceptors deeper or shallower in the chain skip the listener lookup and dispatch. Claiming before `next` also covers re-entrant writes cleanly (a nested write is a new context with its own flag). Queue and observable facets are unaffected: they are interceptor-owned, so aggregation intentionally fans out to each interceptor's own consumers, as today.

This is the one core change of the feature: an additive member on a public struct, reflected in the core public API snapshot.

### Fast-path rules

- Idle gate is two cache-hot loads: the `_state` volatile field (context facets) and the process-wide listener presence hint, a `static volatile int` count of live per-property subscriptions maintained with `Interlocked` on subscribe and dispose. Master comparison: the default `WithFullPropertyTracking()` configuration pays two interceptor hops with one gate load each, so the merged idle path (one hop, two loads) is at or below master's default. The queue-only master path is one hop and one load; the merged path adds one static load, which the benchmark gate must show is not measurable. The hint can only produce false positives (listener lookups that find nothing), never false negatives: it counts live subscriptions process-wide and is independent of context topology, so subjects moving between contexts cannot strand it.
- Active listener lookup rides existing machinery: one `TryGetValue` on the written subject's own `Data` dictionary, a small per-subject table with entries only for written-property timestamps and listener arrays. The write terminal already performs a `Data` access per write for `SetWriteTimestamp`, so this is a second access to an already-hot structure, and it replaces the previous design's lookup in one global process-shared dictionary; the per-subject table is smaller and has better locality.
- The snapshot fields are plain reads. `DispatchState` is immutable; `subscriptions.Length` is not hoisted into an extra local (`array.Length` is an immutable JIT intrinsic that is common-subexpression-eliminated).
- No `ConcurrentDictionary.IsEmpty` or `.Count` on the hot path. The hint gates the listener lookup; `_state is null` gates the context facets.
- Performance claims at this granularity are directional; the benchmark gate (below) is the proof, and phrasing like "strictly cheaper" applies only to ARM's load-acquire savings.

### Facet isolation: pay only for what you use

- Observable facet. The `Subject` and its `Subject.Synchronize` wrapper are created lazily on the first observable subscription (under the modification lock) and published into the snapshot; while no observable consumer exists, `SyncSubject` is null and a queue-only context never allocates the `Subject`. `GetPropertyChangeObservable()` wraps the interceptor with `.Publish().RefCount()`, so the interceptor's `Subscribe` is only invoked when a real downstream subscriber arrives.
- Queue facet. `QueueSubscriptions` starts as the shared `Array.Empty<PropertyChangeQueueSubscription>()` singleton and becomes a real copy-on-write array only after the first `CreatePropertyChangeQueueSubscription()`.
- Per-property dispatch. Contexts and subjects that never see a per-property subscription pay only the static hint read; the hint is zero until the first subscription anywhere in the process.

### Listener storage on the subject

Subscriptions are stored per property in the subject's existing extension bag: `Subject.Data[(propertyName, ListenersKey)]` holds an immutable `PropertyChangeSubscription[]` (copy-on-write). Each subscription object holds the `PropertyReference`, the observer, and an `int _disposed` flag.

- Mutation (subscribe, dispose) is not the hot path. It uses a CAS loop on the `ConcurrentDictionary`: read the current array, build the replacement, `TryUpdate` with the old array as comparand, retry on conflict; first subscription uses `TryAdd`, removal of the last uses the existing `TryRemovePropertyData(key, expectedValue)` compare-and-remove helper. This is linearizable without any lock.
- Disposal is a one-shot atomic transition: `Interlocked.Exchange` on `_disposed`; only the first transition performs the CAS removal and the `Interlocked.Decrement` of the process-wide hint. Repeated or concurrent disposal is a no-op.
- Dispatch reads the array snapshot and, immediately before each invocation, checks the subscription's disposed flag with `Volatile.Read` (the `Interlocked.Exchange` on the dispose side has no visibility effect on a plain read side). A subscription disposed between the flag check and the call can still receive one trailing invocation; this is documented best-effort, identical to Rx's in-flight `OnNext` semantics. Per-consumer volatile flag checks during fan-out are parity with master, whose queue fan-out already performs one volatile `_completed` read per subscription in `Enqueue` (`PropertyChangeQueueSubscription.cs:17,31`).

Ownership and lifetime (documented in `docs/tracking.md` and the XML docs):

- The subject holds its subscriptions; a subscription keeps its observer (and any captured state) alive for as long as the subject lives, until the consumer disposes it. This is exactly the `INotifyPropertyChanged` event rule and carries the same forgotten-handler caveat. There is no external table pinning subjects and therefore nothing for lifecycle tracking to clean up: dropping the subject collects subject, subscriptions, and observers together.
- Detach and re-attach: while the subject is detached (or attached to a context without a `PropertyChangeInterceptor`), writes bypass interception and the subscription is dormant; delivery resumes on re-attach, in whatever context the subject lands (any interceptor instance dispatches from the subject's own storage). Subscribing before first attach is valid for the same reason.
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

Subscribe touches only the subject: it installs the subscription into `Subject.Data` and increments the process-wide hint. It does not resolve, and does not require, any interceptor or context; a subject without a notifying context accepts subscriptions that stay dormant until it is attached (documented). Subscribe validates the property name against `subject.Properties` and throws for an unknown member.

Subscription semantics: instance, not path. A subscription binds to a concrete subject instance plus property name (`PropertyReference`) and follows the instance, not its location in the object graph. If the instance is re-parented, the subscription keeps firing. If the instance is replaced at some path (`garage.Car = otherCar`), the subscription stays with the old instance and does not transfer; watching "whatever is at this path" is a different, higher-level feature that would compose per-property subscriptions on the structural segments and belongs in Registry, out of scope here.

The strongly-typed resolver is Tracking-local and strict about expression shape: unwrap any `Convert` boxing node, then require the body to be a `MemberExpression` whose `.Expression` is the lambda's own `ParameterExpression`, take `.Member.Name`, and build `new PropertyReference(subject, name)`. The parameter-target check is essential: `PropertyReference` accepts any (subject, name) pair and validates the name only lazily, so without it a chained selector `m => m.Child.Speed` or a captured-variable selector `m => other.Speed` would resolve to the leaf name `Speed` and, if the root subject also has a `Speed`, silently subscribe to the wrong property. Chained, captured-variable, and static-member selectors therefore throw a clear error; only direct `m => m.Foo` is supported. The Registry's `TryGetRegisteredProperty` resolver is not used because Registry depends on Tracking (package cycle).

Delivery is by `in`-ref. `SubjectPropertyChange` is a large readonly struct (easily 100 bytes or more), so by-ref delivery avoids the per-call copy; `IObserver<T>.OnNext(T)` is by value by signature, which is why the core cannot be `IObservable<T>`. The interface overload lets a service be its own observer with zero closure allocation; the delegate overload wraps into an internal adapter, one allocation at subscribe time, none per event.

### Concurrency contract for callbacks

Per-property callbacks run synchronously within the write operation on the writing thread, outside the subject lock (dispatch is post-`next`; the `SyncRoot` lock covers only the innermost field write). Under a transaction, capture defers delivery: callbacks run at commit replay, on the committing thread. This is where the observable's `OnNext` and the queue's `Enqueue` already run today.

Unlike the observable (serialized through `Subject.Synchronize`), per-property callbacks are not serialized:

- Two racing writes to the same property can invoke the same observer concurrently.
- One observer shared across several subjects can be invoked concurrently from writes to different subjects.

The contract, documented in `docs/tracking.md` and the XML docs on `Subscribe`, `SubscribeToProperty`, and `IPropertyChangeObserver`:

- Thread-safe. Observers may be invoked concurrently and must tolerate it.
- Fast and non-blocking. A slow callback stalls the writer itself.
- Must not throw. Dispatch runs post-`next` inside the write chain, so an exception from a callback propagates up and can skip later unwind work (lifecycle reconciliation, derived recalculation) for a write that already committed. Observers must not throw, exactly the `ILifecycleHandler` contract ("must be exception-free"). Deliberately not caught, to keep the dispatch hot path free of per-callback try/catch; the contract is documented instead, and a test pins that exceptions propagate rather than being swallowed.
- Delivered possibly out of commit order. Because dispatch is outside `SyncRoot`, two racing writes A then B can invoke the callback as B then A, and tick-granular timestamps cannot disambiguate. An observer that needs the current value must re-read the property rather than trust the callback's `newValue`. This matters for exactly the advertised use cases (watchdog, dirty flag).

A consumer that needs off-thread or serialized processing hands off in one line and owns its own queue or `Channel`:

```csharp
property.Subscribe((in SubjectPropertyChange c) => _channel.Writer.TryWrite(c));
```

Re-entrancy is safe: a callback may write properties and re-enter the write chain (dispatch holds no lock and iterates an immutable array snapshot). Note the same is already true of a synchronous Rx observer today; the per-write dedup flag additionally guarantees a re-entrant write cannot double-deliver to listeners.

### Registration, ordering, and aggregation

A single `PropertyChangeInterceptor` per context serves the queue and observable facets. `WithPropertyChangeObservable()` and `WithPropertyChangeQueue()` both register it idempotently (calling both wires one instance) as an `IWriteInterceptor`.

Interceptor ordering. `SubjectTransactionInterceptor` currently declares `[RunsBefore(typeof(PropertyChangeObservable))]` and `[RunsBefore(typeof(PropertyChangeQueue))]` (`SubjectTransactionInterceptor.cs:14-15`). Both are deleted and the ordering is expressed target-side instead: `PropertyChangeInterceptor` declares `[RunsAfter(typeof(SubjectTransactionInterceptor))]`. Target-side matters under aggregation: `ServiceOrderResolver`'s type-to-index map keeps only the last instance per type (`ServiceOrderResolver.cs:115-117`), so a source-side `RunsBefore` on the transaction interceptor would order it before only one of several aggregated interceptor instances; `RunsAfter` edges are built per service instance (`ServiceOrderResolver.cs:136-143`), so every `PropertyChangeInterceptor` instance is ordered after the transaction interceptor. The transaction interceptor's `[RunsBefore(typeof(DerivedPropertyChangeHandler))]` is unchanged. Residual duplicated-transaction hole, documented: with two or more aggregated `SubjectTransactionInterceptor` instances, each `RunsAfter` edge binds to only one of them; that ordering cannot leak staged writes in practice only because capture's context-binding validation calls the singular `TryGetService<SubjectTransactionInterceptor>()` (`SubjectTransactionInterceptor.cs:65`), which throws under such aggregation before any dispatch. The resolver's duplicate-type limitation itself is pre-existing on master (it affects today's `PropertyChangeObservable`/`PropertyChangeQueue` edges and `ValidationInterceptor` the same way) and is a follow-up issue, not fixed here.

Aggregation behavior:

- Per-property listeners: dispatched exactly once per write via the `ArePropertyListenersNotified` flag regardless of how many interceptor instances are in the chain; `Subscribe` involves no interceptor selection at all.
- Queue and observable facets: interceptor-owned as today. The retained creation APIs `GetPropertyChangeObservable()` and `CreatePropertyChangeQueueSubscription()` keep their current singular `GetService` resolution and therefore keep throwing under aggregation, for parity; this is stated, not accidental.
- Known behavior deviation, documented: `TryAddService`'s exists-check spans fallback contexts (`InterceptorSubjectContext.cs:133-146`). Today a parent context with `WithPropertyChangeObservable()` and a child with `WithPropertyChangeQueue()` yield two interceptors with different audiences; merged, the child's registration finds the parent's interceptor and registers nothing, so a queue subscription created via the child context receives changes from every subject of the parent context. Exotic topology, accepted and called out.

### Interceptor disposal

`PropertyChangeInterceptor.Dispose()`:

- Queue facet: completes all current queue subscriptions and wakes blocked `TryDequeue` consumers, exactly today's `PropertyChangeQueue.Dispose` behavior.
- Observable facet: left untouched (no `OnCompleted`); parity, since today's observable is not disposable and nothing completes it.
- Per-property listeners: untouched; they belong to subjects, not to the interceptor.
- `_state` swaps to null under the modification lock; in-flight dispatches complete against their captured snapshot.
- After dispose, `GetPropertyChangeObservable`'s subscribe path and `CreatePropertyChangeQueueSubscription` throw `ObjectDisposedException`. This is a deliberate deviation from today, where `Dispose` does not mark the queue disposed and a later `Subscribe()` accidentally works; that is judged an artifact, not a contract.

While rewriting the subscription internals, fix the latent enqueue-versus-dispose race on master: `Dispose` eagerly disposes the `ManualResetEventSlim` while a concurrent producer's `Enqueue` can still call `Set()` on it, throwing `ObjectDisposedException` into the write chain (`PropertyChangeQueueSubscription.cs:31-37` versus `:99-105`). The rewrite must not dispose the signal while producers can still observe the subscription (drop the eager `_signal.Dispose()` or guard the `Set`), and disposal itself becomes a one-shot atomic transition (`Interlocked.Exchange` instead of today's non-atomic check-then-act at `:94-99`).

### Public API changes

Removed public types: `PropertyChangeObservable`, `PropertyChangeQueue`.

Retained: `PropertyChangeQueueSubscription` (unchanged consumer usage; internals repoint at the interceptor). Its public constructor currently takes a `PropertyChangeQueue`; that parameter is retyped to `PropertyChangeInterceptor` or the constructor becomes internal, since `CreatePropertyChangeQueueSubscription()` is the intended creation path.

New public surface: `PropertyChangeInterceptor`, `IPropertyChangeObserver`, `PropertyChangeCallback`, the `Subscribe` / `SubscribeToProperty` extension methods, and the additive `PropertyWriteContext.ArePropertyListenersNotified` member in the core package.

Snapshots: the `VerifyChecksTests.PublicApi.verified.txt` files for `Namotion.Interceptor.Tracking` and for the core `Namotion.Interceptor` package are updated.

### Forward compatibility (out of scope now)

Value-assertion corrections (the approved `docs/superpowers/specs/2026-07-12-change-origin-design.md`) are not implemented yet and are out of scope here. Noted only so this merge stays compatible: when that path lands, a `Correction` must inject through the interceptor's queue facet only (fan out to `DispatchState.QueueSubscriptions`), resolved via `GetServices<PropertyChangeInterceptor>()` (plural, to cover aggregation), and must never reach the observable or the per-property listeners, since both are application-level consumers and a correction is not a model change. Corrections do not flow through `WriteProperty`, so the listener path never sees them by construction.

## Constraints (hard)

1. No functionality removed. `GetPropertyChangeObservable` (with all schedulers), `CreatePropertyChangeQueueSubscription`, `PropertyChangeQueueSubscription.TryDequeue`, the unbounded isolated per-subscription queue semantics, all documented thread-safety guarantees, `ChangeQueueProcessor`, and all connector and source behavior keep working with identical observable behavior, with two documented carve-outs: (a) unordered subscribe/write races may resolve differently, and (b) a queue subscription created synchronously inside a write (from within the write chain, same thread) no longer receives the triggering change, because dispatch uses the pre-`next` snapshot while master re-reads the array post-`next` (`PropertyChangeQueue.cs:80`); this same-thread case is deterministic on master, so it is a real, accepted, documented behavior change (no in-repo consumer does this; the lifecycle-attach `ChangeQueueProcessor` pattern runs in post-`next` unwind and is unaffected).
2. No performance regression, on idle and active paths, including single-facet configurations, proven by the benchmark gate. Directional claims: idle two cache-hot loads versus master default's two gate loads plus an extra interceptor hop; active listener lookup rides the already-paid per-subject `Data` access pattern; build-once makes the both-active case faster.
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

Migration surface (update to `PropertyChangeInterceptor`):
- `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs` (`new PropertyChangeQueue()` at 7 sites, plus the matching `AddService`).
- `src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeQueueTests.cs` (retarget `GetService<PropertyChangeQueue>()`, rename the file and class).
- `src/Namotion.Interceptor.Benchmark/SubjectSourceBenchmark.cs` (`GetService<PropertyChangeQueue>()`).
- `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionLifecycleTests.cs:116-133` (asserts on the literal type name `"PropertyChangeObservable"` in interceptor ordering; retarget to `PropertyChangeInterceptor`, or supersede with the new ordering tests).
- `PropertyChangeQueueSubscription`'s constructor parameter.

After migration:
- All existing `Namotion.Interceptor.Tracking` tests pass.
- All connector and source tests that depend on the queue pass (OPC UA, MQTT, WebSocket servers, `SubjectSourceBase`, `ChangeQueueProcessor`).
- Transaction ordering: the transaction interceptor runs before `PropertyChangeInterceptor` under both registration orders and with multiple fallback-context interceptor instances.

New per-property tests (`When<Condition>_Then<ExpectedBehavior>` naming, Arrange/Act/Assert):

- Sync in-ref dispatch to a single listener and to multiple listeners for one property.
- Indexed isolation: a write to property B never invokes a listener registered for property A.
- Re-entrancy: a listener that writes another property inside its callback does not deadlock and delivers correctly; a re-entrant write to the same property does not double-deliver (dedup flag).
- Manual dispose stops delivery; repeated and concurrent disposal is a no-op after the first transition (one-shot flag) and never stops delivery to remaining live subscriptions.
- Dormancy and revival: subscribing before attach delivers nothing until the subject attaches, then delivers; detach silences delivery; re-attach (same or different context with a `PropertyChangeInterceptor`) resumes delivery with the same subscription.
- Aggregation: with multiple fallback contexts each contributing an interceptor, a per-property subscription delivers exactly once per write.
- Lifetime: the subject holds the subscription (a dropped subject is collectable together with its subscriptions and observers; no process-global structure retains it, hint count aside).
- Concurrency: concurrent writes to one property invoke a shared observer concurrently (documented, not serialized); concurrent subscribe, dispose, and write are race-free (CAS loop loses no registrations; no delivery after disposal beyond the documented best-effort trailing call, checked via `Volatile.Read`).
- Callback exception contract: a throwing observer is not caught (documented must-not-throw); a test pins that the exception propagates rather than being swallowed.
- Strongly-typed `SubscribeToProperty(x => x.Foo, ...)` resolves the correct property; an unknown member throws; a chained selector (`x => x.Child.Value`), a captured-variable selector, and a static-member selector throw rather than silently binding the leaf name.
- Hint bookkeeping: the process-wide count returns to zero after all subscriptions are disposed (gate closes again).

Fast-path, allocation, and layering:

- No `SubjectPropertyChange.Create`, no `Data` listener lookup, and no snapshot reads occur when `_state` is null and the hint is zero; no build occurs when consumers exist but none applies to the written property (white-box or allocation assertion).
- No `ConcurrentDictionary.IsEmpty` or `.Count` on the write path.
- The feature compiles with no `Namotion.Interceptor.Registry` reference.

Performance (benchmark-gated):

- Benchmark the write path in `Namotion.Interceptor.Benchmark` for idle, queue-only, observable-only, both-active, listener-on-written-property, and listener-elsewhere (hint set, lookup miss) configurations, before and after, proving no regression and confirming the build-once improvement.

Public API and docs:

- Update and accept the `VerifyChecksTests.PublicApi.verified.txt` snapshots for `Namotion.Interceptor.Tracking` and `Namotion.Interceptor` (core).
- Update `docs/tracking.md`: the merged interceptor, the per-property subscription API, the concurrency contract (not serialized, must not throw, possible out-of-order delivery, re-read for current value), the ownership/lifetime rule (INPC-style, dispose when done), dormancy/revival semantics, and instance-not-path subscription semantics.

## Open questions

- Secondary names: `IPropertyChangeObserver`, `PropertyChangeCallback`, `SubscribeToProperty`, the `Data` key constant, the `PropertyWriteContext` flag name (`ArePropertyListenersNotified`), and the static hint holder (`PropertyChangeSubscriptions.LiveCount`). The class name `PropertyChangeInterceptor` is decided.
- Whether `PropertyChangeQueueSubscription`'s public constructor becomes internal or is retyped to `PropertyChangeInterceptor`.
