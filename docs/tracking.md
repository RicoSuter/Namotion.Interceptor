# Tracking

The `Namotion.Interceptor.Tracking` package provides comprehensive change tracking for interceptor subjects, including property value changes, derived property updates, subject lifecycle events, and parent-child relationships. A single `PropertyChangeInterceptor`, enabled with `WithPropertyChangeSubscriptions()`, routes property changes through three channels that share one write path: an **Rx observable** for composition and UI, a **high-performance queue** for high-throughput consumers, and **per-property subscriptions** for observing one property on one subject instance.

## Setup

Enable full property tracking in your interceptor context:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking(); // Includes all tracking features
```

This is a convenience method that registers:
- Equality checking to prevent unnecessary change notifications
- Derived property change detection
- Property change notifications (the `PropertyChangeInterceptor`, exposing the Rx observable, the high-performance queue, and per-property subscriptions)
- Context inheritance for child subjects

> **Note**: Transaction support is opt-in. Add `.WithTransactions()` or `.WithSourceTransactions()` to enable transaction support.

You can also enable features individually for more granular control.

## Change Tracking

All property change notifications flow through a single `PropertyChangeInterceptor`, registered with `WithPropertyChangeSubscriptions()` (also included in `WithFullPropertyTracking()`). The interceptor exposes three channels over one shared write path: the Rx observable, the high-performance queue, and per-property subscriptions. Enable it once and pick whichever channel fits the consumer.

### Property Change Observable (Rx-based)

The observable channel uses Reactive Extensions (Rx) and is ideal for UI scenarios, complex query composition, and when you need rich operator support:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyChangeSubscriptions();

context
    .GetPropertyChangeObservable()
    .Subscribe(change =>
    {
        Console.WriteLine(
            $"Property '{change.Property.Name}' changed " +
            $"from '{change.GetOldValue<object?>()}' to '{change.GetNewValue<object?>()}'.");
    });

var person = new Person(context)
{
    FirstName = "John",
    LastName = "Doe"
};
```

**Observable features:**
- Rich operator support (Where, Select, Throttle, Buffer, etc.)
- Easy composition with other Rx streams
- Scheduler support for thread control
- Great for UI data binding scenarios

**Observable limitations:**
- Higher memory overhead per change event
- Slightly lower throughput in high-frequency scenarios
- Subject synchronization overhead

### Property Change Queue (High Performance)

The queue channel uses a lock-free, allocation-conscious queue and is optimized for maximum throughput with minimal allocations. This is the preferred mechanism for high-performance scenarios such as background services, IoT data processing, and source synchronization:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyChangeSubscriptions();

using var subscription = context.CreatePropertyChangeQueueSubscription();

while (subscription.TryDequeue(out var change, cancellationToken))
{
    Console.WriteLine(
        $"Property '{change.Property.Name}' changed " +
        $"from '{change.GetOldValue<object?>()}' to '{change.GetNewValue<object?>()}'.");
}
```

**Queue performance characteristics:**

1. Zero-allocation value storage: Primitive types (int, decimal, bool, etc.) and small structs are stored inline without boxing
2. Lock-free queuing: Uses `ConcurrentQueue<T>` for non-blocking writes and low-overhead consumer wake-ups
3. Efficient signaling: `ManualResetEventSlim` is used to wake the consumer without busy-waiting

**Queue semantics and threading:**

- Enqueue is fully thread-safe and needs no synchronization; `TryDequeue` is single-consumer, so each subscription must be drained by one thread.
- Each subscription owns an isolated queue, so different subscriptions can be consumed concurrently.
- Independent subscriptions may observe different relative orderings under concurrent writes: dispatch enqueues to each subscription in turn on the writing thread, so two writers can interleave differently per subscription. There is no order that all subscriptions agree on.
- The implementation is deadlock-free and never loses an enqueued item.
- The queue is unbounded with no backpressure or overflow policy, so a slow consumer causes unbounded memory growth.
- Disposal returns immediately: it wakes a waiting consumer and stops future enqueues but does not wait for buffered items, which the consumer may still drain (`TryDequeue` returns the remaining items, then `false`). An enqueue already in flight may finish after `Dispose` returns.
- Cancellation takes priority over buffered items: `TryDequeue` checks the token before dequeuing, so a cancelled call returns `false` even when items are available.

**Queue limitations:**
- `TryDequeue` is synchronous and blocks a consumer thread until an item arrives, cancellation is requested, or the subscription is disposed. Continuously draining several subscriptions therefore costs one blocked consumer thread per subscription while they are idle, whereas the observable multiplexes all its subscribers onto the dispatch thread and its scheduler.
- There is no asynchronous consumer API: `TryDequeue` returns the change through an `out` parameter, so it cannot be awaited.

**Queue use cases:**
- Source synchronization (MQTT, OPC UA, databases)
- Background data processing services
- High-frequency property change scenarios (>1000 changes/second)
- IoT and industrial automation applications

### Per-Property Subscriptions

When you only care about a single property on a single subject, subscribe to that property directly instead of filtering the whole stream. Two entry points are available:

```csharp
// Strongly typed, via a direct property selector on the subject:
using var handle = person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange change) =>
{
    Console.WriteLine($"FirstName is now '{change.GetNewValue<object?>()}'.");
});

// Or via a PropertyReference and an IPropertyChangeObserver or callback:
var property = new PropertyReference(person, nameof(Person.FirstName));
using var handle2 = property.Subscribe((in SubjectPropertyChange change) => { /* ... */ });
```

The observer can be an `IPropertyChangeObserver` implementation or a `PropertyChangeCallback` delegate; both receive the change by `in` reference.

Only a direct property access on the lambda parameter is accepted (`x => x.FirstName`). Chained (`x => x.Child.Foo`), captured-variable, static, field, and method selectors throw `ArgumentException`. The property must be an intercepted or derived property; otherwise its changes never enter the interception chain and `Subscribe` throws.

**Instance, not path**: a subscription binds to the given subject instance and property name. It observes writes to that property on that instance no matter where the subject sits in any object graph, and it is unaffected by how the subject is referenced or re-parented. It is not a subscription to a path.

**Dormancy and revival**: you may subscribe before the subject is attached to a context that has the `PropertyChangeInterceptor`. The subscription is valid but dormant (no deliveries) until the subject is attached, and it revives automatically when the subject is re-attached. A subscription installed on an already attached subject is live immediately.

**Delivery guarantee**: provided the downstream interceptor chain returns normally after the commit, a write that commits after the subscribing call returned is always delivered while the subscription stays live and no earlier synchronous observer of the same write throws. A downstream interceptor that commits and then throws prevents outer interceptors from dispatching. A write that committed before the subscribing call returned may not be delivered, so read the property after subscribing to observe that earlier state. The same guarantee applies to all three channels: per-property subscriptions, `CreatePropertyChangeQueueSubscription`, and `GetPropertyChangeObservable` all resolve their consumers after the commit.

**Ownership and lifetime**: `Subscribe` and `SubscribeToProperty` return an `IDisposable`, and disposing it is mandatory. Dispose stops future deliveries (one already in flight may still invoke the observer after `Dispose` returns) and releases the subscription. A dropped, undisposed handle keeps the observer receiving changes while the subject stays alive, and it also degrades the whole process permanently: the count that gates the idle write fast path is decremented only by `Dispose` (there is no finalizer), so one leaked subscription keeps every write in the process on the slower listener-check path for the process lifetime. The subject and its subscriptions are still collected together once nothing references the subject, but the fast path does not recover. A retained handle pins the subject, and an observer that captures the subject pins it too.

### Path Subscriptions

When you care about a location in the object graph rather than a fixed instance, subscribe to a path. `SubscribeToPath` observes "the value at `x.Engine.PrimarySensor.Temperature`" as the path itself changes over time: `Engine` may be null at first and appear later, `PrimarySensor` may be replaced with a different subject, and the leaf value changes. A path subscription is a chain of per-property subscriptions, one per segment, that re-subscribes the suffix whenever an intermediate segment changes, so its cost is O(path depth) plus retrack work only on structural changes (which are rare relative to leaf writes).

Two overloads mirror the per-property primitive, one taking a callback and one a zero-closure observer:

```csharp
// Callback overload:
using var handle = car.SubscribeToPath(
    x => x.Engine.PrimarySensor.Temperature,
    (in SubjectPathChange<double> change) =>
    {
        // Re-render from Current on every callback (see below).
    });

// Observer overload (ISubjectPathChangeObserver<double>, no per-event closure):
using var handle2 = car.SubscribeToPath(x => x.Engine.PrimarySensor.Temperature, observer);
```

`SubscribeToPath` delivers no event at subscribe time. Every delivered event corresponds to exactly one real property write, carried as `Cause`. Consumers seed their initial state from `Current`, which computes the current observed state on demand and never caches it:

```csharp
var initial = handle.Current;
if (initial.IsResolved)
{
    Console.WriteLine($"Temperature is {initial.Value}.");
}
```

For a single-segment path (`x => x.Speed`) `SubscribeToProperty` is the lighter choice; `SubscribeToPath` accepts it for uniformity but adds walk and retrack machinery you do not need there.

**Seeding without races**: subscribe-then-read-`Current` is not atomic with the event stream. A write may fire the callback before you read `Current` the first time. Pull consumers (render from `Current`, re-render on every callback) are always safe, since `Current` is authoritative and side-effect free. A caching consumer must not seed with a value captured before a newer callback applied, or it can go permanently stale. Hold one consumer lock across subscription creation, handle assignment, and the initial seed, and re-take it in every callback, reading `Current` at apply time rather than a cached snapshot or the event's `New`:

```csharp
lock (gate)
{
    subscription = car.SubscribeToPath(
        x => x.Engine.PrimarySensor.Temperature,
        (in SubjectPathChange<double> change) =>
        {
            lock (gate)
            {
                state = subscription.Current; // read Current, never change.New or a cached snapshot
            }
        });

    state = subscription.Current; // seed under the same lock
}
```

A callback fired by a write racing the subscribe blocks on `gate` until the handle is assigned and the seed applied, so it never observes a null handle or overwrites the seed out of order. Reading `Current` at apply time means the last apply always reads the freshest state, and the guaranteed callback after the last write makes it converge.

**Observed state**: each observed state is a `SubjectPathValue<TValue>`.

- `IsResolved` is true when every intermediate segment is non-null and every index is in range, so the leaf property exists. Its value may itself be null.
- `Value` is the leaf value (default when unresolved).
- `TryGetValue(out value)` returns false when unresolved and true (with a possibly-null `value`) for a resolved null leaf, so a resolved null is distinct from unresolved.
- `GetValueOrDefault()` and `GetValueOrDefault(fallback)` collapse the unresolved case to a default or a supplied fallback.

**Transitions and suppression**: a `SubjectPathChange<TValue>` carries `Kind`, `Old`, `New`, and `Cause`. `Kind` is `ValueChange` when the chain was intact and the write hit the current resolved leaf, and `PathChange` for every other observable transition (a structural write, or a revalidation triggered by an off-path write). `Old` is the state before the event and `New` the state now; `Cause` is the real `SubjectPropertyChange` that triggered it. An event is suppressed when `Old` equals `New`: same resolvedness and equal values under `EqualityComparer<TValue>.Default` (the comparer `WithEqualityCheck` uses; for subject-typed leaves without an `Equals` override this is reference equality). Chained transitions therefore hold, so event N+1's `Old` equals event N's `New`. Racing leaf writes that arrive out of commit order coalesce (the first re-reads the latest value, the second is a suppressed no-op), and structural churn that lands the same subject with an equal leaf value at a slot is suppressed. A path subscription is a current-value observer, not a write log; consumers that need every write use the queue or observable channels.

**Expression rules**: segments chain directly off the lambda parameter: property access (`.Foo`), collection index (`.Bar[3]`, an `int` into a subject collection), and dictionary index (`.Items["key"]`, a key into a subject dictionary). Index arguments are evaluated exactly once at subscribe time, so `[3]` is a fixed position and `[i]` does not re-read `i` later. Indexable collection and dictionary types in a path are `T[]`, `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `ImmutableArray<T>`, `Dictionary<TKey, TValue>`, `IDictionary<TKey, TValue>`, and `IReadOnlyDictionary<TKey, TValue>` (any key type). `IEnumerable<T>`, `ICollection<T>`, and `IReadOnlyCollection<T>` have no indexer, so a path through them does not compile (a C# limitation, not a runtime rejection). `ArrayList` and `Hashtable` stay valid subject property types but cannot appear as path segments, because their `object`-typed indexers require a cast the expression rules reject.

**Validation**: static expression-shape defects always throw `ArgumentException` at subscribe, since they can never become valid: casts, method calls other than a single-argument indexer, multi-argument indexers, field selectors, captured-object chains (`m => other.Speed`), static members, the identity path (`x => x`), a path ending in an indexed element (`x => x.Items[3]` with no trailing property), an index argument that references the lambda parameter (`x => x.Items[x.Index]`), a `get_Item` indexer on a property that is not a subject collection or dictionary, a nested indexer whose receiver is another indexer, a negative collection index, and an index whose subscribe-time evaluation yields a null dictionary key. `SubscribeToPath` also throws `ArgumentNullException` for a null subject, expression, callback, or observer.

Runtime validity is treated leniently, not as a subscribe-time throw. Every segment must satisfy `IsIntercepted || IsDerived` against the runtime subject during a walk. A `[Derived]` leaf, or a `[Derived]` intermediate that selects a child subject, is subscribable and carries the derived-change-detection prerequisite (see the cost model). A plain, non-derived, non-intercepted segment does not throw: it never enters the write chain, so the walk resolves the path as unresolved from that segment onward. The same rule applies to interfaces: a `[Derived]` interface-default property is subscribable, while a plain interface default resolves unresolved. A defect discovered later on a walk (a segment missing on a different runtime type after a heal or `new`-hiding, or a leaf value not assignable to `TValue`) likewise downgrades to unresolved rather than throwing into a writer's chain, because runtime validity is per runtime type and not permanent.

**Delivery contract**: deliveries are totally ordered per subscription and chained as above. The delivering thread is whichever writer is currently draining, usually the writing thread itself. Callbacks run outside the tracker's per-subscription lock through an exclusive-drain queue, so a callback on one path may safely write a segment of another path without a lock-ordering deadlock. Getter reads during walks happen under that lock, so getters must be side-effect free, the same expectation the lifecycle and derived machinery already place on them. Racing structural changes may coalesce and intermediate states can be skipped. The callback contract is the primitive's, verbatim: fast, non-blocking, and must not throw. A throwing callback propagates to the drainer and abandons the current drain; already-queued events are delivered when the next event is computed.

Quiescent consistency is narrowly scoped: once observed writes settle, the last event's `New` and `Current` agree with the real graph. Five cases are carved out, and in each of them `Current` stays accurate while the event stream lags until a subsequent delivered callback (or dispose and re-subscribe) heals it:

1. A throwing callback abandons the drain; events it stranded are delivered late on the next write, or not at all if no further write occurs.
2. A divergence created while a segment is dormant (see below) persists until the next delivered callback, with no callback guaranteed in the meantime.
3. A foreign synchronous observer of the same write throws and aborts dispatch before the segment callback runs (per-property listeners dispatch after each interceptor's queue and Rx channels), so a committed write's callback never fires. This is another consumer violating the must-not-throw contract; it heals on the next delivered callback.
4. A getter, or a container operation (`Count`, an indexer, `TryGetValue`, a comparer), that throws during the walk publishes unresolved and recovers only on a later delivered segment callback, not on the mere clearing of the failing condition, because the tracker subscribes to path segments and not to whatever off-path or container state the traversal reads.
5. An `Equals`-suppressed recompute of a derived subject-valued intermediate returns a distinct but `Equals`-equal instance: the equality handler suppresses the synthetic write, so no segment callback fires, yet the getter now returns a new instance with its own subtree. The chain stays on the old subtree until the next non-suppressed segment callback retracks it.

Reading `Current` does not heal the subscription; it is side-effect free and simply stays accurate despite the divergence.

**Dormancy and the context-inheritance prerequisite**: a segment fires only while its subject is attached to a context with a `PropertyChangeInterceptor`. A structural change made while a segment's subject is dormant is not observed when it happens; the chain re-syncs on the next callback delivered to any still-subscribed segment, because every processed callback revalidates with a fresh walk. Until then the divergence persists silently, but `Current` is always computed fresh, so reads never go stale. Subscribing before the root is first attached is valid; the subscription is dormant until attach.

> **Prerequisite**: multi-segment paths that dynamically introduce context-free children require context inheritance. Use `WithFullPropertyTracking()`, or at least `WithPropertyChangeSubscriptions()` plus `WithContextInheritance()`, for any path spanning more than one subject. Without `ContextInheritanceHandler`, a context-free child assigned into the graph never inherits the root's context, so its writes bypass interception: segment 0 fires, deeper segments through that child are silently inert, `Current` still works, and nothing errors. A child that carries its own notifying context (constructed with one, or attached to a notifying context independently) works without inheritance.

**Derived-intermediate caveat**: a child subject reachable only through a `[Derived]` intermediate (held by no intercepted property anywhere) is a special case. Lifecycle discovers children only through intercepted properties, so such a child is not attached at graph attach. Its segment subscriptions are installed but stay dormant until the derived property first recalculates to an `Equals`-distinct value through the write chain. A stable or equal-recomputing derived intermediate leaves the suffix inert. A derived intermediate that aliases a child already held by an intercepted property (for example `Best => Candidates[0]`) is attached via that property and is unaffected.

**Ownership and lifetime**: `SubscribeToPath` returns a `SubjectPathSubscription<TValue>`, and disposing it is mandatory. `Dispose()` tears down every segment subscription, drops queued undelivered events, and clears references; afterwards `Current` returns the unresolved default rather than throwing into a racing reader. Self-dispose from inside a callback is supported. A dropped, undisposed handle keeps the observer receiving changes and, worse, permanently degrades the process-wide idle write fast path by the chain's depth, because each segment increments the primitive's process-wide subscription count and only `Dispose` decrements it. This is the primitive's forgotten-handler cost multiplied across the chain. Any externally rooted chain subject (for example a child also held by a cache) pins its segment subscription, and through it the tracker and the rest of the chain, until the handle is disposed or a retrack moves the chain off that subject.

**Transactions**: subscribing inside a transaction is disallowed. Because the tracker builds its chain by walking intercepted getters, and reads on a flow with an active transaction return staged, uncommitted values, `SubscribeToPath` throws `InvalidOperationException` when an active, not-yet-committing transaction is on the current flow, so it never installs a chain on speculative subjects a rollback would strand. Staged writes deliver at commit replay, on the committing thread. A transaction disposed without commit delivers nothing; a failed commit that reverts applied changes replays inverse writes, so a subscriber can observe apply-and-revert transition pairs that converge back to the pre-transaction observed state. The event-computation walk reads committed state (it suppresses an ambient transaction), so a rare `[Derived]`-with-setter or cross-context write made on a transaction-holding flow does not retrack onto a speculative subject and strand the subscription. `Current` is left unconstrained: read inside a transaction flow it returns that transaction's read-your-writes view, which self-corrects on the next callback.

**Cost model**: while at least one per-property or path subscription exists anywhere in the process, every write additionally pays a slow-path branch and one dictionary probe on the written subject to look for listeners; beyond that, writes outside any watched path pay nothing (old-value capture and the post-commit re-check run on every write regardless, so they are not a cost of subscribing). A write to a watched segment additionally pays, inline on the writing thread, a short per-subscription lock, a validating walk along the path (one intercepted property read per segment, each of which may briefly take that subject's `SyncRoot`; with full tracking each read also runs the derived-tracking read hook), and your callback. Structural changes additionally pay the re-subscription of the segments below the change; a structural write to a property watched by N path subscriptions pays N rebuilds inline. Leaf writes pay the walk whether or not the event is delivered, since the walk is what produces `New` for the suppression comparison.

Delivery is allocation-free for inline value types and strings, including a path with an `ImmutableArray<T>` intermediate: the tracker reuses a per-subscription walk buffer and `Current` rents from `ArrayPool`. The one exception is a non-`IEquatable<T>` struct leaf, which boxes both operands in the `EqualityComparer<TValue>.Default` suppression comparison.

> **Warning**: a `[Derived]` segment's getter executes on every processed event of the subscription; keep derived segments off hot paths. A derived subject-valued intermediate must alias a stable instance: a getter that constructs a fresh instance per read forces a full suffix retrack on every event and leaves that suffix dormant, because the reconstructed subjects are never the instance lifecycle attached.

**Thread marshaling for UI consumers**: callbacks run on writer or draining threads, never a UI thread. Blazor and other UI consumers must marshal (for example via `InvokeAsync`) before touching component state.

> **Future extensions**: the following are intentionally not built yet, so readers see what is deliberately out of scope.
> - **Prefix sharing**: many subscriptions under a common prefix each hold their own segment chain today; a shared prefix tree is a pure optimization to add once real workloads show it is needed.
> - **String-path subscriptions**: compose this primitive at the Registry or connector level with `IPathProvider` parsing for externally supplied paths (OPC UA node addresses, MQTT topics).
> - **Dynamic (runtime-named) segments and late-added-property discovery** (issue #387): string-named segments for paths not known at compile time, and watching properties added at runtime.
> - **Cast support in selectors** (`x => ((Car)x.Vehicle).Speed`).
> - **Paths ending in an indexed element** (`x => x.Items[3]` with no trailing property), observing which value sits at a position.
> - **In-place collection mutation tracking** (`INotifyCollectionChanged`); today all collections change by reassignment, which the design relies on.
> - **Asynchronous delivery**; consumers hand off to their own `Channel`, as with the primitive.
> - **`Refresh()`** on the handle: an explicit re-sync hook for the dormancy edge and for tests.

### Concurrency and Delivery

Dispatch starts on the writing thread, outside the subject lock, and shares one contract. The per-property listeners and the queue run inline there; `GetPropertyChangeObservable()` pushes there too but reschedules its subscribers onto its scheduler by default (unless you pass `ImmediateScheduler.Instance`).

- **Lifecycle runs first** (with `WithLifecycle()`, included in `WithFullPropertyTracking()`): for subject-typed writes, notifications dispatch after attach/detach reconciliation, so at callback time the subject graph and registry already reflect the write (barring a concurrent overwrite or a concurrent detach of the parent). A subject assigned to a property is attached, and writes a consumer makes to it are themselves tracked. Removals are the reverse: the departing subject is already detached, so writes to it from a callback are stored but not tracked, which is intended. One consequence for custom handlers: an `ILifecycleHandler` that writes properties while attaching emits those changes before the structural change that introduced the subject.
- **Ordering**: under concurrent writes to the same property, notifications may arrive out of commit order. If you need the current value, re-read the property rather than relying on the delivered new value. `OldValue` is the value the setter observed when it started, including when the subscription raced the write. It is not necessarily the value immediately preceding the commit, so under concurrency delivered old and new pairs may not chain.
- **Per-property observers are not serialized**: an `IPropertyChangeObserver` or `PropertyChangeCallback` may be invoked concurrently on multiple threads and must be thread-safe, fast, non-blocking, and must not throw. Wrap failing work in a try-catch internally. (The Rx observable is serialized through `Subject.Synchronize()`; the queue is single-consumer per subscription.)
- **Throwing synchronous observers suppress later deliveries**: each interceptor dispatches its queue first, then its Rx observable, then any per-property listeners it resolved. With aggregated contexts, the innermost interceptor resolves the per-property listeners, so they may run before outer contexts' queue and Rx channels. An exception from any synchronous observer propagates out of the write and prevents later deliveries in that order; queue items already enqueued remain available. Keep synchronous observers exception-free. The exception surfaces from the setter after the value was committed and nothing is rolled back; the property keeps the new value. For scheduler-based Rx observers, delivery means the change was accepted by the channel, not that the callback has already run.
- **Transactions replay on commit**: with `WithTransactions()`, writes captured inside a transaction do not notify during capture. They replay through the interceptor on commit and notifications fire then. If the transaction is rolled back (disposed without commit), the changes are discarded, no notifications fire, and the property keeps its pre-transaction value. If a best-effort commit partially applies and then reverts, listeners observe the apply-and-revert pair, so a consumer such as a watchdog or dirty flag must not treat the revert as a user change.

## Property Value Equality Check

Prevents unnecessary change notifications when a property is set to the same value:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithEqualityCheck();

var person = new Person(context);
person.Name = "John"; // Triggers change
person.Name = "John"; // No change triggered (same value)
```

Uses `EqualityComparer<T>.Default` for every property type. Reference equality is used only when the type does not provide value equality.

## Transactions

Transactions allow you to batch property changes and commit them atomically. Changes are captured during the transaction and applied together on commit, with change notifications fired after all changes are applied.

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithTransactions(); // Required for transaction support (opt-in)

var person = new Person(context);

using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    person.LastName = "Doe";

    // Changes captured but not applied yet
    // Reading returns pending values (read-your-writes)
    Console.WriteLine(person.FullName); // Output: John Doe

    await transaction.CommitAsync(cancellationToken);
    // All changes applied, notifications fired
}
```

Key features:
- **Atomic commits**: All changes applied together
- **Read-your-writes**: Reading returns pending values inside the transaction
- **Notification suppression**: Change notifications fired after commit, not during capture
- **Rollback on dispose**: Uncommitted changes discarded if transaction not committed

For external source integration (OPC UA, MQTT, etc.), use `WithSourceTransactions()` from the Connectors package to write changes to external sources before applying them to the local model.

See [Transactions](tracking-transactions.md) for detailed documentation.

## Derived Property Change Detection

Automatically tracks dependencies between properties and triggers change events for derived properties when their dependencies change:

> **Prerequisite**: Automatic derived-property notifications require `WithDerivedPropertyChangeDetection()`, which is bundled in `WithFullPropertyTracking()`. Manual `RecalculateDerivedProperty()` (below) also requires it.

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";
}

var context = InterceptorSubjectContext
    .Create()
    .WithDerivedPropertyChangeDetection()
    .WithPropertyChangeSubscriptions();

context.GetPropertyChangeObservable().Subscribe(change =>
{
    Console.WriteLine($"{change.Property.Name}: {change.GetOldValue<object?>()} → {change.GetNewValue<object?>()}");
});

var person = new Person(context);
person.FirstName = "John";
// Output: FirstName:  → John
// Output: FullName:  → John

person.LastName = "Doe";
// Output: LastName:  → Doe
// Output: FullName: John  → John Doe
```

**How it works:**
- During derived property evaluation, the handler records which properties are read
- When a dependency changes, the derived property is recalculated
- If the derived value changes, a change event is triggered with `Source = null` (indicating local calculation)

### Manual Recalculation

When a derived property's getter depends on data outside the interceptor system (external APIs, services, static state, etc.), automatic dependency tracking cannot detect changes. Use `RecalculateDerivedProperty()` to manually trigger recalculation:

```csharp
[InterceptorSubject]
public partial class Sensor
{
    public partial string? Label { get; set; }

    [Derived]
    public double CalibratedTemperature => _externalService.GetCalibratedTemperature();
}

// When external data changes, trigger recalculation:
var property = new PropertyReference(sensor, nameof(Sensor.CalibratedTemperature));
property.RecalculateDerivedProperty();
// Getter is re-evaluated; if the value changed, change notifications fire
```

This goes through the same pipeline as automatic recalculation: the getter is re-evaluated, dependencies are updated, and all notifications (observable, queue, per-property subscriptions, `INotifyPropertyChanged`) fire if the value changed. It is fully thread-safe and can be called concurrently with property writes. Like automatic detection, it requires `WithDerivedPropertyChangeDetection()`.

> **Internal design:** For details on the dependency graph, concurrency model, and correctness guarantees, see [Derived Property Design](design/tracking-derived-properties.md).

## Context Inheritance

Automatically assigns the parent context to child subjects, ensuring they participate in the same tracking and interception pipeline:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithContextInheritance();

var car = new Car(context);
var tire = new Tire(); // No context assigned yet

car.Tire = tire; // tire.Context is automatically set to context
```

This ensures that all objects in the subject graph share the same context, enabling consistent tracking, validation, and other interceptor features.

## Subject Lifecycle Tracking

Track when subjects enter or leave the object graph, and when property references are added or removed:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string Name { get; set; }
    public partial Person[] Children { get; set; }
}

var context = InterceptorSubjectContext
    .Create()
    .WithLifecycle()
    .WithService(() => new MyLifecycleHandler());

var person = new Person(context);
var child = new Person { Name = "Child" };

person.Children = [child]; // HandleLifecycleChange: IsContextAttach + IsPropertyReferenceAdded
person.Children = [];      // HandleLifecycleChange: IsPropertyReferenceRemoved + IsContextDetach

public class MyLifecycleHandler : ILifecycleHandler
{
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            Console.WriteLine($"Attached: {change.Subject} via {change.Property?.Name}");
        }
        if (change.IsContextDetach)
        {
            Console.WriteLine($"Detached: {change.Subject} via {change.Property?.Name}");
        }
    }
}
```

### SubjectLifecycleChange Flags

The `HandleLifecycleChange` method receives a `SubjectLifecycleChange` with flags indicating what happened:

| Flag | Description |
|------|-------------|
| `IsContextAttach` | Subject **first entered** the graph (first property reference) |
| `IsPropertyReferenceAdded` | A property reference to the subject was added |
| `IsPropertyReferenceRemoved` | A property reference to the subject was removed |
| `IsContextDetach` | Subject is **leaving** the graph (last reference removed) |

Flags can be combined. For example, when a child is first assigned to a property:
- `IsContextAttach = true` and `IsPropertyReferenceAdded = true`

When the same subject is assigned to a second property:
- `IsContextAttach = false` (already in graph) and `IsPropertyReferenceAdded = true`

**Lifecycle tracking is used by:**
- **Hosting package**: Start/stop `IHostedService` implementations when attached/detached
- **Registry package**: Track subjects and properties in the registry
- **Sources package**: Subscribe/unsubscribe from external data sources
- **Derived property detection**: Initialize derived properties on attach

### Lifecycle Events

In addition to `ILifecycleHandler`, the `LifecycleInterceptor` provides events for dynamic subscribers:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithLifecycle();

var lifecycleInterceptor = context.TryGetLifecycleInterceptor();

lifecycleInterceptor.SubjectAttached += change =>
{
    Console.WriteLine($"Subject attached: {change.Subject}");
};

lifecycleInterceptor.SubjectDetaching += change =>
{
    Console.WriteLine($"Subject detaching: {change.Subject}");
};
```

**Important distinction:**
- `ILifecycleHandler.HandleLifecycleChange`: Called for **every** lifecycle change (context attach, property add, property remove, context detach)
- `SubjectAttached` event: Fires **once** when subject first enters the graph
- `SubjectDetaching` event: Fires **once** when subject is about to leave the graph

**Event timing (symmetry):**
- `SubjectAttached` fires **after** `ILifecycleHandler.HandleLifecycleChange(attach)` - all handlers have initialized
- `SubjectDetaching` fires **before** `ILifecycleHandler.HandleLifecycleChange(detach)` - handlers can still access full graph

This symmetry ensures that both events fire when the full object graph is accessible, which is useful for handlers that need to traverse relationships or access child subjects during cleanup.

Events are useful for:
- Cache invalidation when subjects are removed from the object graph
- Dynamic subscribers that register/unregister at runtime (unlike `ILifecycleHandler` which is registered at startup)
- Integration packages (MQTT, OPC UA) that need to clean up internal state

### Thread Safety

The lifecycle interceptor is fully thread-safe. Multiple threads can concurrently write to the same structural property. Reference counts remain consistent, no subjects are orphaned, and all attach/detach callbacks fire exactly once per transition.

> **Internal design:** For details on the concurrency model and correctness guarantees, see [Lifecycle Interceptor Design](design/tracking-lifecycle.md).

### Handler Requirements

> **Important**: Both `ILifecycleHandler` methods and lifecycle events are invoked **synchronously inside a lock**. Handlers must follow these requirements:

1. **Must be exception-free**: Throwing exceptions will break the lifecycle pipeline for other handlers. Wrap any potentially failing operations in try-catch internally.

2. **Must be fast**: The lock is held during invocation, so blocking operations will degrade performance across the entire system. Typical handlers should complete in microseconds (e.g., dictionary operations).

3. **Dispatch long-running work**: If you need to perform I/O, network calls, or other slow operations, dispatch to an external queue and process asynchronously:

```csharp
// Good: Fast dispatch to queue
lifecycleInterceptor.SubjectDetaching += change =>
{
    _cleanupQueue.Enqueue(change.Subject); // Returns immediately
};

// Bad: Blocking I/O in handler
lifecycleInterceptor.SubjectDetaching += async change =>
{
    await database.DeleteAsync(change.Subject); // Blocks the lock!
};
```

4. **Thread-safe operations**: Use thread-safe data structures like `ConcurrentDictionary` with atomic operations (`TryRemove`, `TryAdd`) rather than check-then-act patterns.

> **Tip**: Multiple handlers can be ordered using `[RunsBefore]`, `[RunsAfter]`, `[RunsFirst]`, and `[RunsLast]` attributes. See [Service Ordering](interceptor.md#service-ordering) for details.

### Reference Counting

Each subject tracks how many property references point to it via `GetReferenceCount()`:

```csharp
var referenceCount = subject.GetReferenceCount();
// Returns the number of properties referencing this subject
// Returns 0 if not attached or lifecycle tracking is disabled
```

**Important notes:**
- Subjects created directly with context (root subjects) have `refs: 0` - they have no property references pointing to them
- Subjects attached via properties have their reference count incremented/decremented on add/remove
- `GetReferenceCount()` returns property reference count, not total attachment count

The `SubjectLifecycleChange` includes `ReferenceCount` after the operation. Use the flags to determine the event type:

```csharp
public void HandleLifecycleChange(SubjectLifecycleChange change)
{
    if (change.IsContextDetach)
    {
        // Subject leaving graph - safe to clean up
        CleanupResources(change.Subject);
    }
}
```

This enables proper cleanup when subjects are removed from all parent references, even when referenced by multiple properties or collections.

### Object Graph Behavior

Understanding how the lifecycle system handles different graph topologies:

**Hierarchies (Trees)**

When a branch is removed, the entire subtree cascades detachment:

```
Root
  ├── Device1  ← stays attached
  └── Device2  ← detached when Root.Device2 = null
       ├── Child1  ← cascade detached
       └── Child2  ← cascade detached
```

Siblings are protected - removing Device2 doesn't affect Device1.

**DAGs (Directed Acyclic Graphs)**

Shared nodes stay attached if they have remaining references:

```
Root
  ├── A ──┐
  └── B ──┴── Shared (refs: 2)
```

Removing A reduces Shared's refs to 1 - it stays attached via B.
Removing B after A detaches Shared (refs: 0).

**Cycles (Limitation)**

Nodes that only reference each other stay attached due to reference counting:

```
Root → A → B ↔ C (internal cycle)
```

If `Root.A = null`:
- A detaches (lost reference from Root)
- B and C **stay attached** (they keep each other alive with refs: 1 each)

This is the classic reference counting limitation. **Workarounds:**
1. Call `DetachSubjectFromContext(subject)` explicitly
2. Break all cycle references before removing the parent

## Parent-Child Relationship Tracking

Tracks parent-child relationships in the subject graph, enabling upward navigation:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithParents();

var car = new Car(context);
var tire = new Tire(context);

car.Tires = [tire];

var parents = tire.GetParents(); // Returns ImmutableArray with [(car, "Tires", 0)]
```

This enables scenarios like:
- Finding the root object of a subject graph
- Navigating from child to parent for validation or business logic
- Building hierarchical displays in UI

## Read Property Recorder

Records which properties are accessed during a specific scope, useful for advanced dependency tracking or auditing:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithReadPropertyRecorder();

var person = new Person(context);

using var scope = ReadPropertyRecorder.Start();

var fullName = person.FullName; // Records FirstName and LastName

var accessedProperties = scope.GetPropertiesAndDispose();
// accessedProperties contains references to FirstName and LastName
```

This is primarily used internally by the derived property change detection system but can also be used for custom scenarios.

## Change Origin and Timestamps

**Change Sources**: Use the `SetValueFromSource()` extension method to apply a value coming from an external source:

```csharp
propertyReference.SetValueFromSource(
    source: mqttSource,
    changedTimestamp: DateTimeOffset.Now,
    receivedTimestamp: DateTimeOffset.Now,
    valueFromSource: newValue);
// change.Origin is ChangeOrigin.FromSource(mqttSource)
```

Source marking is per write, not through an ambient scope. This prevents feedback loops where changes from external sources are written back to those same sources.

**Atomic Timestamps**: Use `SubjectChangeContext.WithChangedTimestamp()` when several property writes belong to one logical event and should publish with the same timestamp. Without the scope, each write reads `UtcNow` separately and consumers see distinct events microseconds apart. Pass `null` when the source has no timestamp.

```csharp
using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
{
    position.X = 1.0;
    position.Y = 2.0;
    position.Z = 3.0;
}
```

The scope reads `UtcNow` once on entry and reuses it for every write inside (also slightly faster). Keep the scope short: the timestamp does not update, so late writes still get the original time.

## Integration with Other Packages

The Tracking package is foundational and used by:

- **Registry**: Requires `WithLifecycle()` for subject/property registration
- **Hosting**: Requires `WithLifecycle()` for hosted service management  
- **Sources**: Uses the high-performance queue via `WithPropertyChangeSubscriptions()` for synchronization
- **Validation**: Can trigger validation on property changes
- **Blazor**: Uses `WithPropertyChangeSubscriptions()` for UI updates

See the individual package documentation for integration details.
