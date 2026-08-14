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
- One dedicated dispatch thread per subscriber at the default scheduler, which is what bounds how many subscribers this channel supports (see [Channel Cost](#channel-cost))
- Higher memory overhead per subscriber
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
- `TryDequeue` is synchronous and blocks a consumer thread until an item arrives, cancellation is requested, or the subscription is disposed. Continuously draining several subscriptions therefore costs one blocked consumer thread per subscription while they are idle, though the observable at its default scheduler is no cheaper here: it takes a dedicated dispatch thread per subscriber.
- There is no asynchronous consumer API: `TryDequeue` returns the change through an `out` parameter, so it cannot be awaited.

**Queue use cases:**
- Source synchronization (MQTT, OPC UA, databases)
- Background data processing services
- High-frequency property change scenarios (>1000 changes/second)
- IoT and industrial automation applications

### Per-Property Subscriptions

When you only care about a single property on a single subject, subscribe to it directly instead of filtering the whole stream. Use `SubscribeInline` when the callback is quick, thread-safe and cannot throw, and [scheduled delivery](#scheduled-delivery) for anything that does I/O, may block or may throw. A scheduled subscription costs far more memory, which decides the choice at thousands of subscriptions (see [Channel Cost](#channel-cost)).

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

Only a direct property access on the lambda parameter is accepted (`x => x.FirstName`); chained (`x => x.Child.Foo`), captured-variable, static, field, and method selectors throw `ArgumentException`, as does a property that is neither intercepted nor derived.

**Instance, not path**: a subscription binds to a subject instance and property name and follows that instance through any re-parenting.

**Dormant until attached**: it delivers nothing until the subject is attached to a context with the `PropertyChangeInterceptor`, and revives on re-attach.

**Subscribe, then read**: a write that committed before the subscribing call returned may not be delivered, so read the property afterwards to catch that state.

**Disposal is mandatory**: there is no finalizer, so one dropped handle keeps delivering and keeps every write in the process on the slower listener-check path for the rest of the process.

#### Scheduled delivery

Passing an `IScheduler` to `Subscribe` or `SubscribeToProperty` moves delivery off the writing thread: the write enqueues the change and returns, and a drain on the scheduler delivers changes one at a time, reporting observer exceptions to an optional `onError` the writer never sees. Deferral widens the staleness window, so read `change.GetCurrentValue<TValue>()` when you need the current value.

```csharp
using var handle = person.SubscribeToProperty(
    x => x.FirstName,
    (in SubjectPropertyChange change) => WriteToDevice(change.GetCurrentValue<string?>()),
    Scheduler.Default,
    exception => logger.LogError(exception, "FirstName observer failed."));
```

**Serialized per subscription, not per observer**: one subscription never re-enters its observer, but an observer shared across several subscriptions is still invoked concurrently, and one that blocks starves every other subscription on its scheduler.

**The queue is unbounded**, with no backpressure: `handle.PendingCount` makes a growing backlog observable, and draining it back to zero does not give the memory back, because the queue keeps the largest segment it ever grew to until disposal.

**Synchronous schedulers are rejected**: `ImmediateScheduler.Instance` and `CurrentThreadScheduler.Instance` throw `ArgumentException`; use `property.SubscribeInline(callback)` when you want the callback inside the write.

**Ambient context does not flow to the observer**: the drain suppresses `ExecutionContext` flow, so the writer's `AsyncLocal` values, `Activity.Current` and logger scopes do not reach it; create a caller-owned scheduler such as an `EventLoopScheduler` outside any transaction scope, or property writes the observer makes vanish silently into the transaction that thread inherited, for as long as that transaction is live.

**Dormancy is not symmetric with disposal**: detaching the subject stops acceptance but not the drain, so a change accepted before the detach is still delivered afterwards, while disposal drops the whole queue.

#### Composing with Rx

`property.GetInlineChangeObservable()` exposes one property's changes as an `IObservable<SubjectPropertyChange>`. It stays inline, on the writing thread, and a throwing handler propagates back into the setter; the context-level `GetPropertyChangeObservable()` reschedules onto a scheduler by default and is not the same thing.

**Notifications are serialized per subscriber**, so stateful operators such as `Take`, `Skip`, `Scan`, `DistinctUntilChanged` and `Buffer` by count are safe over concurrent writers without extra work.

**A handler composed over this must not throw.** Once an operator is in the chain, the first exception ends the subscription silently instead of propagating to the writer.

### Delivery Guarantees

Dispatch starts on the writing thread, outside the subject lock and after the commit, so a change that committed later can reach a consumer first. Every committed write carries a `SubjectPropertyChange.Revision`, monotonic **per subject**: of two changes to the *same* subject, the higher revision committed later. Revisions of *different* subjects are **not** comparable, and a change constructed outside a terminal write carries `0`. A consumer converging on the current value keeps the higher `Revision` or re-reads the property.

| Channel | Exactly-once | Order | Consumer runs on | Serialized |
|---|---|---|---|---|
| Per-property callback (`SubscribeInline`) | conditional (a) | arrival | writer thread | no, concurrent writers re-enter it |
| Per-property observable (`GetInlineChangeObservable`) | conditional (a) | arrival | writer thread | per subscriber |
| Scheduled per-property callback (`Subscribe`) | conditional (a), (c) | arrival | scheduler thread | per subscription, not per observer instance |
| Observable (`GetPropertyChangeObservable`) | conditional (a) | arrival | scheduler thread, writer thread with `ImmediateScheduler` | yes, through `Subject.Synchronize()` |
| Pull queue | conditional (a) | arrival | consumer thread | single consumer by contract |
| `ChangeQueueProcessor`, buffer > 0 | no, latest-state-wins | arrival of survivors; per-property newest within a flush (b) | processor thread | one flush at a time |

(a) A throwing lifecycle handler or a throwing earlier observer suppresses delivery for the rest of that write's consumers, so exactly-once holds only while those no-throw contracts do.

(b) Per property, a flush collapses to the newest commit in that batch, and across flushes a change whose revision the property has already moved past is dropped rather than emitted. Which commits count depends on the connector, via `ChangeDeliveryRule`; see [Change Batching and Merging](connectors.md#change-batching-and-merging).

(c) Disposal and a scheduler fault each drop the queue, discarding accepted but undelivered changes, and a `Schedule` whose work item never runs strands what is queued behind it. The pull queue stays drainable after disposal.

Rules that hold across every channel:

- **Lifecycle runs first** (with `WithLifecycle()`): subject-typed writes dispatch after attach/detach reconciliation, so the graph and registry already reflect the write and writes to a newly assigned subject are tracked while writes to a removed one are not, and an `ILifecycleHandler` that writes while attaching emits those changes before the structural change that introduced the subject.
- **Ordering**: notifications may arrive out of commit order, so call `change.GetCurrentValue<TValue>()` when you need the value the property holds now.
- **Throwing synchronous observers suppress later deliveries**: the exception propagates out of the write and prevents the rest of that write's deliveries, with nothing rolled back, though enqueued queue items stay available and the [scheduled overloads](#scheduled-delivery) report to `onError` instead.
- **A derived recalculation publishes the stabilized value**: the change carries the value the recalculation committed rather than a fresh getter read, so a throwing getter does not suppress the notification.
- **Transactions replay on commit** (see [Transactions](#transactions)): a best-effort commit that partially applies and then reverts delivers the apply-and-revert pair, which a watchdog or dirty flag must not treat as a user change.

On every channel, the old value is what the generated setter observed at the call site, outside the subject lock, so under concurrent writers it can already have been superseded and delivered old and new pairs may not chain. Revisions decide *which* change's old value survives a collapse, not that it is the value at the preceding revision. The new value is exact, the old value a best-effort diff baseline.

### Channel Cost

Measured on one Apple M4 Max running .NET 9.0.10 arm64, so the byte figures hold as absolute values but the timings are usable only as factors between rows of the same run.

| Channel | Allocated per write | Allocated per delivery | Held per live subscription | Allocated per subscribe and dispose | Write time, inline = 1 |
|---|---|---|---|---|---|
| `GetPropertyChangeObservable()` | none | none | about 5,672 bytes per additional subscriber, plus one dedicated dispatch thread each | 5,736 bytes | about 2.2 |
| `CreatePropertyChangeQueueSubscription()` | none | none | about 5,496 bytes per additional subscription | 5,552 bytes | 1.9 |
| `SubscribeInline` | none | in the write | about 172 bytes | 136 bytes | 1.0, the reference |
| `GetInlineChangeObservable` | none | in the write, one lock taken | about 216 bytes | 248 bytes | 1.0 |
| `Subscribe` with a scheduler | about 34 bytes | 160 bytes keeping up, none under backlog | about 5,607 bytes | 5,607 bytes | 2.6 |

**These compare one identical write, not equal workloads.** A context-level channel delivers every property's changes, so watching one property out of five hundred means paying for the other 499 as well, which is why 2.6 is usually the cheaper choice despite being the highest number in the column.

**Setup dominates, not steady state.** Almost all of a scheduled subscription's footprint is its empty `ConcurrentQueue<SubjectPropertyChange>`, so a thousand of them cost roughly 5.35 MB against roughly 0.16 MB for a thousand inline ones, and on a wide model that, not the per-write cost, decides the channel.

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
