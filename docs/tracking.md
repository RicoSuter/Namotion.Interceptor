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

| API | Use it for | Delivered on | Serialized | Section |
|---|---|---|---|---|
| `context.GetPropertyChangeObservable()` | Rx composition and UI binding over the whole model | a scheduler, by default | yes | [Property Change Observable](#property-change-observable-rx-based) |
| `context.CreatePropertyChangeQueueSubscription()` | high throughput and source synchronization | your own consumer thread | one consumer per subscription | [Property Change Queue](#property-change-queue-high-performance) |
| `property.SubscribeInline(callback)` | one property, cheapest possible | the writing thread, inside the write | no, your callback must be thread-safe | [Per-Property Subscriptions](#per-property-subscriptions) |
| `property.GetInlineChangeObservable()` | one property, with Rx operators | the writing thread, inside the write | per subscriber | [Composing with Rx](#composing-with-rx) |
| `property.Subscribe(callback, scheduler, onError)` | one property whose observer is slow, blocking or may throw | the scheduler | per subscription, not per observer | [Scheduled delivery](#scheduled-delivery) |

The contract they share, per channel and across channels, is in [Delivery Guarantees](#delivery-guarantees), and what each one costs is in [Channel Cost](#channel-cost).

### Property Change Observable (Rx-based)

The observable channel uses Reactive Extensions (Rx) and is ideal for UI scenarios, complex query composition, and when you need rich operator support:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyChangeSubscriptions();

// Delivery is rescheduled onto Scheduler.Default, so the setter returns before the handler runs and the
// handler cannot assume the value is still current. Pass ImmediateScheduler.Instance for inline delivery.
// The handler must not throw: an exception escapes the scheduler work item and terminates the process.
// Dispose when done, because every write pays this channel's synchronization lock while a subscriber lives.
using var subscription = context
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

When you only care about a single property on a single subject, subscribe to it directly instead of filtering the whole stream. `SubscribeInline` runs your callback on the writing thread inside the write: the cheapest option, and the right one when the callback is quick, thread-safe and cannot throw. [Scheduled delivery](#scheduled-delivery) moves it off the writer, for anything that does I/O, may block or may throw. Inline holds roughly a thirtieth of the memory per subscription, which decides the choice once you subscribe to thousands of properties.

Each form takes either an `IPropertyChangeObserver` or a `PropertyChangeCallback`, and both receive the change by `in` reference.

```csharp
// Strongly typed, via a direct property selector on the subject. Inline means this callback runs on
// whichever thread wrote the property, before the setter returns, and possibly on several at once, so it
// must be quick, thread-safe, and must not throw: an exception here propagates out of the setter.
using var handle = person.SubscribeToPropertyInline(x => x.FirstName, (in SubjectPropertyChange change) =>
{
    Console.WriteLine($"FirstName is now '{change.GetNewValue<object?>()}'.");
});

// Or via a PropertyReference, when the property is chosen at runtime rather than by a selector:
var property = new PropertyReference(person, nameof(Person.FirstName));
using var handle2 = property.SubscribeInline((in SubjectPropertyChange change) => { /* ... */ });
```

Only a direct property access on the lambda parameter is accepted (`x => x.FirstName`). Chained (`x => x.Child.Foo`), captured-variable, static, field, and method selectors throw `ArgumentException`. The property must be an intercepted or derived property; `SubscribeInline` throws otherwise.

**Instance, not path**: a subscription binds to a subject instance and property name, and observes writes to that property wherever the subject sits in an object graph and however it is re-parented. It is not a subscription to a path.

**Dormancy and revival**: subscribing before the subject is attached to a context that has the `PropertyChangeInterceptor` is valid but dormant, delivering nothing until the subject is attached and reviving on re-attach. On an already attached subject it is live immediately.

**Delivery guarantee**: a write that commits after the subscribing call returned is delivered while the subscription stays live, unless an earlier inline observer of the same write throws or a downstream interceptor commits and then throws. A write that committed before it returned may not be delivered, so read the property after subscribing to catch that earlier state. All three channels resolve their consumers after the commit and share this guarantee.

**Ownership and lifetime**: disposing the returned `IDisposable` is mandatory. Dispose stops future deliveries (one already in flight may still invoke the observer) and releases the subscription. A dropped, undisposed handle keeps delivering and permanently degrades the whole process: only `Dispose` decrements the count that gates the idle write fast path, and there is no finalizer, so one leaked subscription keeps every write in the process on the slower listener-check path for the rest of the process, and letting the subject be collected does not recover it. A retained handle pins the subject, and so does an observer that captures it.

#### Scheduled delivery

Passing an `IScheduler` moves delivery off the writing thread: the write enqueues the change and returns, and a drain on the scheduler delivers changes one at a time. `Subscribe` and `SubscribeToProperty` take an observer or a callback, the scheduler, and an optional `onError`, and return a `ScheduledPropertySubscription` whose disposal is as mandatory as above. An observer exception reaches `onError` instead of the writer, and deferral widens the staleness window, so read `change.GetCurrentValue<TValue>()` when you need the current value.

```csharp
using var handle = person.SubscribeToProperty(
    x => x.FirstName,
    (in SubjectPropertyChange change) => WriteToDevice(change.GetCurrentValue<string?>()),
    Scheduler.Default,
    exception => logger.LogError(exception, "FirstName observer failed."));
```

**Serialized per subscription, not per observer**: the observer of one subscription is never re-entered and needs no synchronization of its own, even across scheduler threads, but one shared across several subscriptions is still invoked concurrently, and a blocking observer starves every other subscription on its scheduler.

**The queue is unbounded**, with no backpressure and no overflow policy: a writer faster than the observer grows it without limit, and `handle.PendingCount` is what makes that backlog observable.

**Synchronous schedulers are rejected**: `ImmediateScheduler.Instance` and `CurrentThreadScheduler.Instance` throw `ArgumentException`; use `property.SubscribeInline(callback)` when you want the callback inside the write.

**Ambient context does not flow to the observer**: the drain runs with `ExecutionContext` flow suppressed, so the writer's `AsyncLocal` values, `Activity.Current`, and logger scopes do not reach it. Create a long-lived caller-owned scheduler such as an `EventLoopScheduler` outside any transaction scope, or property writes the observer makes vanish silently into the transaction that thread inherited for life.

**Dormancy is not symmetric with disposal**: detaching the subject stops acceptance but not the drain, so a change accepted before the detach is still delivered afterwards, carrying a subject that has already left the registry. Disposal instead drops whatever is still queued.

**Disposal allocates rather than releases**: clearing the queue installs a fresh segment instead of dropping the old one, so disposal allocates about 5,312 bytes and a disposed handle that is still referenced keeps roughly 5.3 KB alive. Drop the handle, do not merely dispose it.

#### Composing with Rx

`property.GetInlineChangeObservable()` exposes one property's changes as an `IObservable<SubjectPropertyChange>`, and each subscriber installs its own underlying subscription. It stays inline: delivery is on the writing thread, and a throwing handler propagates back into the setter. The context-level `GetPropertyChangeObservable()` reschedules onto a scheduler by default and is not the same thing.

**Notifications are serialized per subscriber**, on the writing thread, so stateful operators such as `Take`, `Skip`, `Scan`, `DistinctUntilChanged` and `Buffer` by count are safe over concurrent writers without extra work. The serializing lock is held across the handler call, so the handler must not block and must not take locks of its own that a writer might hold.

**A handler composed over this must not throw.** Once an operator is in the chain, the first exception ends the subscription silently instead of propagating to the writer.

### Delivery Guarantees

Dispatch starts on the writing thread, outside the subject lock, and from there every channel shares one contract.

Every committed write carries a `SubjectPropertyChange.Revision`, a counter monotonic **per subject** over committed writes: two changes to the *same* subject are ordered by comparing it, the higher revision committed later. Revisions of *different* subjects are **not** comparable, and a change constructed outside a terminal write carries `0`, which orders against nothing. Dispatch happens after the commit, so under concurrent writers a change that committed later can reach a consumer first; a consumer that has to converge on the current value keeps the higher `Revision` or re-reads the property.

| Channel | Exactly-once | Order | Consumer runs on | Serialized |
|---|---|---|---|---|
| Per-property callback (`SubscribeInline`) | conditional (a) | arrival | writer thread | no, concurrent writers re-enter it |
| Per-property observable (`GetInlineChangeObservable`) | conditional (a) | arrival | writer thread | per subscriber |
| Scheduled per-property callback (`Subscribe`) | conditional (a), (c) | arrival | scheduler thread | per subscription, not per observer instance |
| Observable (`GetPropertyChangeObservable`) | conditional (a) | arrival | scheduler thread, writer thread with `ImmediateScheduler` | yes, through `Subject.Synchronize()` |
| Pull queue | conditional (a) | arrival | consumer thread | single consumer by contract |
| `ChangeQueueProcessor`, buffer > 0 | no, latest-state-wins | arrival of survivors; per-property newest within a flush (b) | processor thread | one flush at a time |

(a) A throwing lifecycle handler or a throwing earlier observer suppresses delivery for the rest of that write's consumers, so delivery is exactly-once only while those no-throw contracts hold.

(b) Per property, a flush collapses to the newest commit in that batch, and collapsing also applies **across** flushes: a change whose revision the property has already moved past is dropped rather than emitted. Which commits count as having moved the property past it depends on the connector, via `ChangeDeliveryRule`; see [Change Batching and Merging](connectors.md#change-batching-and-merging).

(c) Disposal and a scheduler fault each clear the queue, dropping accepted but undelivered changes, and a `Schedule` call whose work item never runs strands what is queued behind it. The pull queue keeps its buffered items drainable after disposal.

An observer on an unserialized channel may be invoked concurrently on multiple threads and must be thread-safe, fast, non-blocking, and must not throw; wrap failing work in a try-catch internally.

Rules that hold across every channel:

- **Lifecycle runs first** (with `WithLifecycle()`, included in `WithFullPropertyTracking()`): for subject-typed writes, notifications dispatch after attach/detach reconciliation, so at callback time the subject graph and registry already reflect the write, barring a concurrent overwrite or detach of the parent. Writes a consumer makes to a newly assigned subject are themselves tracked; writes to a removed one are stored but not tracked, since it is already detached, which is intended. An `ILifecycleHandler` that writes properties while attaching emits those changes before the structural change that introduced the subject.
- **Ordering**: notifications may arrive out of commit order. If you need the current value, call `change.GetCurrentValue<TValue>()`, which reads the property now instead of returning the value captured when the change was created, and needs no separately typed reference to the subject.
- **Throwing synchronous observers suppress later deliveries**: each interceptor dispatches its queue, then its Rx observable, then the per-property listeners it resolved; with aggregated contexts the innermost interceptor resolves those listeners, so they may run before an outer context's queue and Rx channels. An exception from any synchronous observer propagates out of the write and prevents later deliveries in that order, though queue items already enqueued remain available. Nothing is rolled back, so the property keeps the new value. For scheduler-based Rx observers, delivery means the change was accepted by the channel, not that the callback has already run. The [scheduled overloads](#scheduled-delivery) are exempt: the exception is reported to `onError` and never reaches the setter.
- **A derived recalculation publishes the stabilized value**: the change carries the value the recalculation committed rather than a fresh read of the getter, so a throwing getter does not suppress the notification and an interceptor that rewrites `NewValue` on that path changes what is published.
- **Transactions replay on commit**: with `WithTransactions()`, writes captured inside a transaction notify when they replay on commit, not during capture. A rollback (disposal without commit) discards them, fires nothing, and leaves the pre-transaction value. A best-effort commit that partially applies and then reverts delivers the apply-and-revert pair, so a consumer such as a watchdog or dirty flag must not treat the revert as a user change.

On every channel, the old value is what the generated setter observed at the call site, outside the subject lock, including when the subscription raced the write, so under concurrent writers it can be a value that was already superseded and delivered old and new pairs may not chain. Revisions decide *which* change's old value survives a collapse, not that it is the value the property held at the preceding revision. The new value is exact, the old value is a best-effort diff baseline; compare `Revision` or re-read the property if you need more.

### Channel Cost

Everything below was measured on an Apple M4 Max running .NET 9.0.10 arm64, so read the figures as one machine's, not as universal. Allocation is quoted in absolute bytes because it came out bit-identical across three runs. Time is quoted only as a factor between channels measured in the same run: the noise floor here is about 1.5 percent with no CPU pinning available, and absolute timings did not hold up across runs.

| Channel | Allocated per write | Allocated per delivery | Held per live subscription | Allocated per subscribe and dispose | Write time, inline = 1 |
|---|---|---|---|---|---|
| `GetPropertyChangeObservable()` | not measured at its default, see below | not measured | not measured | not measured | not measured at its default, see below |
| `CreatePropertyChangeQueueSubscription()` | none | not measured | not measured | not measured | 1.9, with a consumer draining |
| `SubscribeInline` | none | in the write | about 172 bytes | 136 bytes | 1.0, the reference |
| `GetInlineChangeObservable` | none | in the write, one lock taken | about 172 bytes | not measured | not measured |
| `Subscribe` with a scheduler | about 34 bytes | 160 bytes keeping up, none under backlog | about 5,607 bytes | 10,912 bytes | 2.6 |

Every write in the table is the same single-property write, so the factors compare channels rather than workloads, and the context-level channels were measured on the write side only. A property whose value is a reference type other than `string` adds about 48 bytes per write, once per write no matter how many channels consume it.

**Why the observable has no write figure.** The benchmark subscribes it with `ImmediateScheduler.Instance`, which is not its default and which takes a different code path: that scheduler skips `ObserveOn` entirely, so the measurement covers a synchronization lock and an inline handler and nothing else. At the default the write also enqueues into the `ObserveOn` sink and dispatches a scheduler work item, which is strictly more than the queue channel's enqueue and signal, so the observable's real write cost is above the queue's rather than below it. Publishing the `ImmediateScheduler` number here would invert that ordering, so it is left out until the default is measured.

**Setup dominates, not steady state.** Almost all of a scheduled subscription's footprint is its empty `ConcurrentQueue<SubjectPropertyChange>`, about 5,376 bytes, so a thousand of them cost roughly 5.35 MB against roughly 0.16 MB for a thousand inline ones. On a wide model that memory, not the per-write cost, is what decides the channel, and it is invisible from per-write cost alone.

**Disposing a scheduled subscription allocates rather than releases**, so the subscribe-and-dispose figure above is the two halves added together and a disposed handle that stays referenced keeps holding most of it; see [Scheduled delivery](#scheduled-delivery).

**The two delivery regimes differ by more than a constant.** While the observer keeps up, every change pays its own scheduler work item and allocates. Once a backlog forms, scheduling amortises to one work item per 1024 changes and the per-change allocation falls away as segment slots are recycled, which is why the table quotes two figures rather than one. That zero applies to a queue repeatedly filled and drained; one that keeps growing still pays for each new slot.

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
