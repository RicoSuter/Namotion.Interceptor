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
using var handle = person.SubscribeToPropertyInline(x => x.FirstName, (in SubjectPropertyChange change) =>
{
    Console.WriteLine($"FirstName is now '{change.GetNewValue<object?>()}'.");
});

// Or via a PropertyReference and an IPropertyChangeObserver or callback:
var property = new PropertyReference(person, nameof(Person.FirstName));
using var handle2 = property.SubscribeInline((in SubjectPropertyChange change) => { /* ... */ });
```

The observer can be an `IPropertyChangeObserver` implementation or a `PropertyChangeCallback` delegate; both receive the change by `in` reference.

Only a direct property access on the lambda parameter is accepted (`x => x.FirstName`). Chained (`x => x.Child.Foo`), captured-variable, static, field, and method selectors throw `ArgumentException`. The property must be an intercepted or derived property; otherwise its changes never enter the interception chain and `SubscribeInline` throws.

**Instance, not path**: a subscription binds to the given subject instance and property name. It observes writes to that property on that instance no matter where the subject sits in any object graph, and it is unaffected by how the subject is referenced or re-parented. It is not a subscription to a path.

**Dormancy and revival**: you may subscribe before the subject is attached to a context that has the `PropertyChangeInterceptor`. The subscription is valid but dormant (no deliveries) until the subject is attached, and it revives automatically when the subject is re-attached. A subscription installed on an already attached subject is live immediately.

**Delivery guarantee**: provided the downstream interceptor chain returns normally after the commit, a write that commits after the subscribing call returned is always delivered while the subscription stays live and no earlier inline observer of the same write throws. A downstream interceptor that commits and then throws prevents outer interceptors from dispatching. A write that committed before the subscribing call returned may not be delivered, so read the property after subscribing to observe that earlier state. The same guarantee applies to all three channels: per-property subscriptions, `CreatePropertyChangeQueueSubscription`, and `GetPropertyChangeObservable` all resolve their consumers after the commit.

**Ownership and lifetime**: `SubscribeInline` and `SubscribeToPropertyInline` return an `IDisposable`, and disposing it is mandatory. Dispose stops future deliveries (one already in flight may still invoke the observer after `Dispose` returns) and releases the subscription. A dropped, undisposed handle keeps the observer receiving changes while the subject stays alive, and it also degrades the whole process permanently: the count that gates the idle write fast path is decremented only by `Dispose` (there is no finalizer), so one leaked subscription keeps every write in the process on the slower listener-check path for the process lifetime. The subject and its subscriptions are still collected together once nothing references the subject, but the fast path does not recover. A retained handle pins the subject, and an observer that captures the subject pins it too.

#### Scheduled delivery

Passing an `IScheduler` moves delivery off the writing thread: the write enqueues the change and returns, and a drain on the scheduler delivers it afterwards. Four overloads take one, `property.Subscribe(observer, scheduler, onError)` and `property.Subscribe(callback, scheduler, onError)` plus the two matching `subject.SubscribeToProperty(x => x.Foo, ..., scheduler, onError)` forms, where `onError` is optional. They return a `ScheduledPropertySubscription` instead of a bare `IDisposable`, and disposing it is mandatory for the same reasons as above.

```csharp
using var handle = person.SubscribeToProperty(
    x => x.FirstName,
    (in SubjectPropertyChange change) => WriteToDevice(change.GetCurrentValue<string?>()),
    Scheduler.Default,
    exception => logger.LogError(exception, "FirstName observer failed."));
```

**Serialized per subscription, not per observer**: within one subscription the observer is never re-entered, so it needs no synchronization of its own, and each delivery sees the state the previous delivery wrote even when the two run on different scheduler threads. One observer instance, callback closure, or `onError` delegate shared across several subscriptions is still invoked concurrently and must synchronize itself.

**Isolated from the writer**: an exception from the observer, from `onError`, or from the scheduler can never propagate into the write or suppress another channel's delivery for that write. An observer exception is reported to `onError` and the subscription stays live. A scheduler exception is reported too but faults the subscription, which stops accepting changes for good and is what `ScheduledPropertySubscription.IsFaulted` reports.

**Ordering and staleness**: deliveries arrive in the order the interceptor pushed them, which under concurrent writes to the same property is not commit order, exactly as for inline delivery. Deferred delivery widens the staleness window from microseconds to however long the queue is, so read `change.GetCurrentValue<TValue>()` rather than the delivered new value when you need the current one.

**The `onError` contract** has four parts. It must not throw: an exception from it is caught and swallowed, because a handler failure escaping into a scheduler work item terminates the process. It can run after `Dispose` returns, because an in-flight delivery finishes, so a handler writing into caller-owned disposable state has to tolerate a late call. It is serialized per subscription rather than per delegate, so a handler shared across subscriptions is invoked concurrently. And the scheduler-failure path reports from inside the write, so it can run synchronously on the writer thread under the transaction commit lock, which means it must not write properties, start a transaction, or block. Omitting it swallows every failure, which also makes a permanently throwing observer invisible, because such an observer keeps firing and keeps failing rather than stopping.

**Synchronous schedulers are rejected**, not merely discouraged: `ImmediateScheduler.Instance` and `CurrentThreadScheduler.Instance` throw `ArgumentException`. They do not give you inline delivery either. The dispatcher's work-in-progress counter means the writer that wins the counter race drains every other writer's changes from inside its own setter, so its latency grows with total throughput, and under `WithTransactions` the drain would run consumer code under the commit lock, where an observer that starts a transaction is rejected as nested and one that blocks waiting on that lock deadlocks. Only those two singletons are detectable. Any other scheduler that runs actions inline has the same hazard and cannot be caught, including a `DisableOptimizations()` wrapper over them, which is no longer reference-equal to either. `property.SubscribeInline(callback)` is the inline option.

**The queue is unbounded**, with no backpressure and no overflow policy. A writer faster than the observer grows it without limit, and every buffered change pins its subject and its boxed values. `ScheduledPropertySubscription.PendingCount` reports the changes accepted but not yet dequeued, so a backlog is observable rather than discovered through memory pressure; like `PropertyChangeQueueSubscription.Count` it is exact only when read from a quiescent state. For a hot property, compose `Sample` or `Throttle` on `GetInlineChangeObservable()` instead. A faulted subscription stops accepting and clears its queue, so `PendingCount` reads zero apart from a change that a writer already past its own state check lands afterwards and nothing then dequeues. Either way the value is indistinguishable from a healthy idle subscription's, so read `ScheduledPropertySubscription.IsFaulted` to tell the two apart rather than watching this one.

**An observer that writes the property it observes never drains**: each delivery enqueues its own successor, the queue never empties, work items continue indefinitely and `onError` never fires. Batched handoffs make this a fair, yielding loop rather than a held thread, so it degrades rather than starves, but it does not stop. The inline overload turns the same mistake into a loud `StackOverflowException`; here it is quiet.

**Dormancy is not symmetric with disposal**: detaching the subject stops acceptance but not the drain, so a change accepted before the detach is still delivered after it, and a lifecycle handler that tears down on `SubjectDetaching` has to tolerate a callback arriving after teardown. Disposal is the opposite. It drops the queued changes and releases them, so they stop pinning their subjects, and only a delivery already running, along with its `onError`, can still complete after `Dispose` returns. A writer that had already passed its own state check can still enqueue after the queue was cleared and nothing is guaranteed to dequeue such a change, so `PendingCount` is not guaranteed to reach zero once disposed.

**The caller owns the scheduler** and must dispose the subscriptions before the scheduler they run on. A `Schedule` call that throws, which is what an already disposed scheduler does, is reported to `onError` and faults the subscription, which `IsFaulted` then reports even with no handler supplied. A `Schedule` call that succeeds and whose work item then never runs, which is what disposing a scheduler mid-queue produces, is unrecoverable and unreported: that subscription stays live by `IsFaulted` and simply goes quiet. Disposal also releases the subscription's reference to the scheduler, so a parked handle does not keep an `EventLoopScheduler` thread reachable.

**Ambient context does not flow to the observer**: the drain is scheduled with `ExecutionContext` flow suppressed, so the writer's `AsyncLocal` values, `Activity.Current`, and logger scopes do not reach it. That is deliberate, because one drain batch delivers changes from many writers and a flowed context would stamp whichever writer enqueued first onto all of them, which is worse than no context at all. Suppression governs the context a work item carries, not the context a scheduler's worker thread was born with, so give a subscription its own scheduler or use `Scheduler.Default`: an `EventLoopScheduler` whose thread was created by someone else exposes observers to whatever ambient state existed at that thread's creation, frozen.

#### Composing with Rx

`property.GetInlineChangeObservable()` exposes one property's changes as an `IObservable<SubjectPropertyChange>` so Rx operators compose over a per-property subscription. Each subscriber installs its own underlying subscription and each call returns a distinct instance.

It carries the inline contract above exactly rather than a safer one: delivery is inline, on the writing thread, possibly concurrent, and a throwing handler propagates back into the setter. It is that channel wearing an `IObservable<T>`. The context-level `GetPropertyChangeObservable()` reschedules onto a scheduler by default and is therefore not the same thing. The sequence never completes and never signals `OnError`, so operators that wait for completion, such as `ToTask` and `LastAsync`, never return.

Two hazards when composing the off-thread hop by hand, which is why the scheduler overloads exist. `ObserveOn` dedicates a private thread to each subscription when the scheduler advertises `ISchedulerLongRunning`, which both `Scheduler.Default` and `TaskPoolScheduler` do, so composing it per property is unaffordable. And an exception from the handler escapes the `ObserveOn` sink into the scheduler, which does not catch it: on `Scheduler.Default` it goes unhandled and terminates the process, and on `TaskPoolScheduler` it becomes an unobserved task exception and the subscription stops delivering, so one bad change silently ends the stream. The scheduler overloads have neither.

### Concurrency and Delivery

Dispatch starts on the writing thread, outside the subject lock, and shares one contract. The per-property listeners and the queue run inline there; `GetPropertyChangeObservable()` pushes there too but reschedules its subscribers onto its scheduler by default (unless you pass `ImmediateScheduler.Instance`).

- **Lifecycle runs first** (with `WithLifecycle()`, included in `WithFullPropertyTracking()`): for subject-typed writes, notifications dispatch after attach/detach reconciliation, so at callback time the subject graph and registry already reflect the write (barring a concurrent overwrite or a concurrent detach of the parent). A subject assigned to a property is attached, and writes a consumer makes to it are themselves tracked. Removals are the reverse: the departing subject is already detached, so writes to it from a callback are stored but not tracked, which is intended. One consequence for custom handlers: an `ILifecycleHandler` that writes properties while attaching emits those changes before the structural change that introduced the subject.
- **Ordering**: under concurrent writes to the same property, notifications may arrive out of commit order. If you need the current value, re-read the property rather than relying on the delivered new value: `change.GetCurrentValue<TValue>()` does this for you, reading the property now instead of returning the value captured when the change was created, without needing to keep a separately typed reference to the subject. `GetOldValue<TValue>()` is the value the setter observed when it started, including when the subscription raced the write. It is not necessarily the value immediately preceding the commit, so under concurrency delivered old and new pairs may not chain.
- **Per-property observers are not serialized**: an `IPropertyChangeObserver` or `PropertyChangeCallback` may be invoked concurrently on multiple threads and must be thread-safe, fast, non-blocking, and must not throw. Wrap failing work in a try-catch internally. (The Rx observable is serialized through `Subject.Synchronize()`; the queue is single-consumer per subscription.) The [scheduled overloads](#scheduled-delivery) serialize per subscription, so an observer used by exactly one of them is never re-entered and needs no synchronization; one shared across several subscriptions is still invoked concurrently.
- **Throwing synchronous observers suppress later deliveries**: each interceptor dispatches its queue first, then its Rx observable, then any per-property listeners it resolved. With aggregated contexts, the innermost interceptor resolves the per-property listeners, so they may run before outer contexts' queue and Rx channels. An exception from any synchronous observer propagates out of the write and prevents later deliveries in that order; queue items already enqueued remain available. Keep synchronous observers exception-free. The exception surfaces from the setter after the value was committed and nothing is rolled back; the property keeps the new value. For scheduler-based Rx observers, delivery means the change was accepted by the channel, not that the callback has already run. The [scheduled overloads](#scheduled-delivery) isolate the observer from the write entirely: its exception is reported to `onError` and can neither reach the setter nor suppress any other consumer of that write.
- **A derived recalculation publishes the stabilized value**: the change carries the value the recalculation committed rather than a fresh read of the getter. The getter therefore runs once per recalculation instead of twice, a throwing getter does not suppresses the notification, and an interceptor that rewrites `NewValue` on that path now changes what is published.
- **Transactions replay on commit**: with `WithTransactions()`, writes captured inside a transaction do not notify during capture. They replay through the interceptor on commit and notifications fire then. If the transaction is rolled back (disposed without commit), the changes are discarded, no notifications fire, and the property keeps its pre-transaction value. If a best-effort commit partially applies and then reverts, listeners observe the apply-and-revert pair, so a consumer such as a watchdog or dirty flag must not treat the revert as a user change.

### Delivery Guarantees

Every committed write carries a `SubjectPropertyChange.Revision`: a counter that is monotonic **per subject** over committed writes, so two changes to the *same* subject are ordered by comparing it, the higher revision committed later. Revisions of *different* subjects are **not** comparable, and a change constructed outside a terminal write carries `0`, which orders against nothing.

The revision exists because arrival order can differ from commit order. Dispatch happens after the commit and outside the subject lock, so under concurrent writers a change that committed later can reach a consumer first. A consumer that has to converge on the current value compares `Revision` and keeps the higher one, or re-reads the property.

| Channel | Exactly-once | Order | Consumer runs on |
|---|---|---|---|
| Per-property callback | conditional (a) | arrival | writer thread |
| Observable | conditional (a) | arrival | writer thread |
| Pull queue | conditional (a) | arrival | consumer thread |
| `ChangeQueueProcessor`, buffer > 0 | no, latest-state-wins | arrival of survivors; per-property newest within a flush (b) | processor thread |
| Scheduled per-property callback | conditional (a), (c) | arrival | scheduler thread |

(a) A throwing lifecycle handler or a throwing earlier observer suppresses delivery for the rest of that write's consumers, so delivery is exactly-once only while those no-throw contracts hold.

(b) Per property, a flush collapses to the newest commit in that batch, and collapsing also applies **across** flushes: a change whose revision the property has already moved past is dropped rather than emitted. Which commits count as having moved the property past it depends on the connector, via `ChangeDeliveryRule`; see [Change Batching and Merging](connectors.md#change-batching-and-merging). A consumer that needs the current value still re-reads the property rather than assuming arrival order matches commit order.

(c) The scheduled channel loses accepted changes in ways the others do not. Disposal and a scheduler fault each clear the queue, dropping every change accepted but not yet delivered, and a `Schedule` call that succeeds while its work item never runs strands whatever is queued behind it. The pull queue has none of these: its buffered items stay drainable after disposal.

Note what the old value is and is not, on every channel. Revisions decide *which* change's old value survives a collapse, not that it is the value the property held at the preceding revision: the old value is captured by the generated setter at the call site, outside the subject lock, so under concurrent writers it can be a value that was already superseded. The new value is exact, the old value is a best-effort diff baseline. Compare `Revision` or re-read the property if you need more than that.

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
