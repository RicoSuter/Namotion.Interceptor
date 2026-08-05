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
var tire = new Tire(); // Not in any graph yet

car.Tire = tire; // tire's own context now inherits from car's
```

This ensures that all objects in the subject graph resolve the same services, enabling consistent tracking, validation, and other interceptor features.

The child keeps its own context object. What it gains is an internal parent link to the parent's context, published by `ContextInheritanceHandler` when the child gains its first parent reference and cleared when it loses its last one. The handler is registered by `WithContextInheritance()` and by nothing else, so a context configured with `WithLifecycle()` alone tracks attach and detach without ever publishing a link, and its children resolve no services from their parent. The link resolves after anything you composed onto the child yourself, so explicit composition wins, and it is not reachable through `AddFallbackContext` or `RemoveFallbackContext`. See [Joining and Leaving a Graph](#joining-and-leaving-a-graph).

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

### Joining and Leaving a Graph

A subject enters a lifecycle graph in one of two ways, as a root through `AttachToContext` or as a
child when a parent property starts referencing it, and there are three kinds of edge on a subject's
context:

| Kind | Created by | Released by | Owner |
|---|---|---|---|
| Attach edge | `AttachToContext` | `DetachFromContext`, or the subject's last property detach | the library |
| Parent link | `WithContextInheritance()`'s handler, when a subject gains its first parent reference | the subject's last property detach | the library |
| Explicit fallback | `AddFallbackContext` | the caller | you |

```csharp
subject.AttachToContext(context);     // joins the graph, attaches the whole subtree
subject.DetachFromContext(context);   // leaves it, detaching the subtree

subject.IsAttached();                 // is this subject in a graph at all
subject.TryGetAttachContext();        // which context DetachFromContext would accept, or null
```

`AddFallbackContext(X)` on a subject's own context is not a first step towards `AttachToContext(X)`.
The guard runs before the deduplication check, so if `X` already carries an `ILifecycleInterceptor`
that first call throws and names `AttachToContext` (see the table below). If `X` carries none,
`AttachToContext(X)` writes no attach record, so there is nothing that could adopt the composed edge
and `DetachFromContext` never removes it.

An explicit fallback does become library-owned in one ordering: compose `X` onto the subject's
context while `X` still carries no lifecycle service, register the lifecycle services onto `X`
afterwards, then call `AttachToContext(X)`. The guard saw no interceptor at composition time, so the
edge was published; the later attach records `X` first and then finds that edge already there, so the
single edge is now the attach edge and `DetachFromContext(X)` removes it. That is the one case where
an explicit fallback becomes library-owned.

`AttachToContext` with a context that carries no lifecycle interceptor records nothing and
degenerates to plain composition, because there is no graph to join. Its inverse is then
`RemoveFallbackContext`, not `DetachFromContext`. Once the subject holds no parent references,
`DetachFromContext` finds no record, does nothing and leaves the composed edge in place. While a
parent property still references the subject, the reference-count guard runs first and throws, just
as it does for any other subject. This is what keeps `new Person(InterceptorSubjectContext.Create())`
usable, since the subject is not marked as belonging to a graph that does not exist and can still
join a real one later.

**One graph per subject.** A subject belongs to at most one lifecycle graph, and may be referenced
from any number of parents inside it. This is the model Entity Framework uses for tracked entities.
Attaching a subject that another graph already owns throws rather than half-attaching it.

**What throws:**

| Condition | Result |
|---|---|
| Attaching a subject another graph owns | `InvalidOperationException`; earlier items of the same batch stay attached |
| `AttachToContext` on a subject already attached through a different context | `InvalidOperationException`; detach it first |
| `AddFallbackContext` on a *subject's* context, with a lifecycle-bearing context that is not the recorded attach context | `InvalidOperationException` naming `AttachToContext` |
| `RemoveFallbackContext` aimed at the attach edge | `InvalidOperationException` naming `DetachFromContext` |
| `DetachFromContext` while the subject is still referenced from a parent property | `InvalidOperationException`; remove the references first. This guard runs before the attach-record checks, so it also applies to a subject whose context carries no lifecycle interceptor |
| `DetachFromContext` naming a context other than the recorded attach context | `InvalidOperationException`; pass what `TryGetAttachContext()` returns. With no record at all and no parent references it does nothing instead |
| Re-attaching a subject that holds a parent link, from a lifecycle callback, while its own detach is unwinding | `InvalidOperationException`. A root holds no parent link, so re-attaching a root during a root detach's unwind does not throw |

All four methods need the subject's context to be an `InterceptorExecutor`, which is what
`[InterceptorSubject]` classes and `DynamicSubject` provide. A hand-written `IInterceptorSubject`
returning some other context gets an `InvalidOperationException` naming the requirement.

`AttachToContext` and `DetachFromContext` are not atomic against each other. Calling them
concurrently on the same subject is not supported; roots are normally attached at startup and
detached at shutdown.

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
1. Break all cycle references before removing the parent. This is the supported route.
2. For a cycle that is already orphaned, call `DetachSubjectFromContext(subject)` on the
   `LifecycleInterceptor` that `context.TryGetLifecycleInterceptor()` returns, naming one subject
   inside the cycle. That extension resolves the concrete `LifecycleInterceptor` and returns null
   when none is registered; it throws when the context resolves more than one, which an aggregated
   configuration can produce. That call is the low-level descent operation: it removes that subject's own outgoing
   references, which is what drops the rest of the cycle to zero, and it deliberately ignores the
   reference-count and attach-record guards. Use it only on subjects that were never root-attached,
   since it bypasses the attach edge cleanup that `DetachFromContext` performs.

`subject.DetachFromContext(context)` is not a way out of this. It rejects a subject that parent
properties still reference, and calling it on the graph root leaves the orphaned cycle attached,
because the cascade stops at the first subject whose reference count does not reach zero.

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
