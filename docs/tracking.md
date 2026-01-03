# Tracking

The `Namotion.Interceptor.Tracking` package provides comprehensive change tracking for interceptor subjects, including property value changes, derived property updates, subject lifecycle events, and parent-child relationships. It offers two mechanisms for observing changes: **Observable** (Rx-based) and the **(high performance) queue**, with the queue being the preferred choice for high-throughput scenarios.

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
- Property changed observable (Rx-based)
- Property changed queue (high performance)
- Context inheritance for child subjects

> **Note**: Transaction support is opt-in. Add `.WithTransactions()` or `.WithSourceTransactions()` to enable transaction support.

You can also enable features individually for more granular control.

## Change Tracking: Observable vs (High Performance) Queue

The Tracking package provides two mechanisms for monitoring property changes, each optimized for different use cases.

### Property Changed Observable (Rx-based)

The Observable approach uses Reactive Extensions (Rx) and is ideal for UI scenarios, complex query composition, and when you need rich operator support:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyChangeObservable();

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

**Observable Features:**
- Rich operator support (Where, Select, Throttle, Buffer, etc.)
- Easy composition with other Rx streams
- Scheduler support for thread control
- Great for UI data binding scenarios

**Observable Limitations:**
- Higher memory overhead per change event
- Slightly lower throughput in high-frequency scenarios
- Subject synchronization overhead

### Property Changed Queue (High Performance)

The queue approach uses a lock-free, allocation-conscious queue and is optimized for maximum throughput with minimal allocations. This is the preferred mechanism for high-performance scenarios such as background services, IoT data processing, and source synchronization:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyChangeQueue();

using var subscription = context.CreatePropertyChangeQueueSubscription();

while (subscription.TryDequeue(out var change, cancellationToken))
{
    Console.WriteLine(
        $"Property '{change.Property.Name}' changed " +
        $"from '{change.GetOldValue<object?>()}' to '{change.GetNewValue<object?>()}'.");
}
```

**Queue Performance Characteristics:**

1. Zero-allocation value storage: Primitive types (int, decimal, bool, etc.) and small structs are stored inline without boxing
2. Lock-free queuing: Uses `ConcurrentQueue<T>` for non-blocking writes and low-overhead consumer wake-ups
3. Efficient signaling: `ManualResetEventSlim` is used to wake the consumer without busy-waiting
4. Single-reader optimization: Designed for efficient single-consumer scenarios

**Queue Use Cases:**
- Source synchronization (MQTT, OPC UA, databases)
- Background data processing services
- High-frequency property change scenarios (>1000 changes/second)
- IoT and industrial automation applications

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

Uses `EqualityComparer<T>.Default` for value types and strings, and reference equality for reference types.

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

For external source integration (OPC UA, MQTT, etc.), use `WithSourceTransactions()` from the Sources package to write changes to external sources before applying them to the in-process model.

See [Transactions](tracking-transactions.md) for detailed documentation.

## Derived Property Change Detection

Automatically tracks dependencies between properties and triggers change events for derived properties when their dependencies change:

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
    .WithPropertyChangeObservable();

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

Track when subjects enter or leave the lifecycle system (used by registry, hosting, sources):

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
    .WithContextInheritance()
    .WithService(() => new MyLifecycleHandler());

var person = new Person(context);
var child = new Person { Name = "Child" }; // No context yet

person.Children = [child]; // OnSubjectAttached and OnSubjectAttachedToProperty called
person.Children = [];      // OnSubjectDetachedFromProperty and OnSubjectDetached called

public class MyLifecycleHandler : ILifecycleHandler
{
    public void OnSubjectAttached(SubjectLifecycleChange change)
    {
        Console.WriteLine($"Attached: {change.Subject} via property {change.Property?.Name}");
    }

    public void OnSubjectDetached(SubjectLifecycleChange change)
    {
        Console.WriteLine($"Detached: {change.Subject} via property {change.Property?.Name}");
    }
}
```

**Lifecycle tracking is used by:**
- **Hosting package**: Start/stop `IHostedService` implementations when attached/detached
- **Registry package**: Track subjects and properties in the registry
- **Sources package**: Subscribe/unsubscribe from external data sources
- **Derived property detection**: Initialize derived properties on attach

### Handler Interfaces

The lifecycle system provides three handler interfaces for different tracking needs:

| Interface | Methods | When Called |
|-----------|---------|-------------|
| `ILifecycleHandler` | `OnSubjectAttached`, `OnSubjectDetached` | First entry / Final exit from object graph |
| `IReferenceLifecycleHandler` | `OnSubjectAttachedToProperty`, `OnSubjectDetachedFromProperty` | Each property assignment / removal |
| `IPropertyLifecycleHandler` | `OnPropertyAttached`, `OnPropertyDetached` | Subject's own properties tracked |

**Handler Ordering:**
- **Attach**: Handlers called in registration (forward) order
- **Detach**: Handlers called in **reverse** order for symmetry

This symmetrical ordering ensures cleanup handlers run in the opposite order of initialization handlers.

### Lifecycle Events

In addition to handler interfaces, the `LifecycleInterceptor` provides events for dynamic subscribers:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithLifecycle();

var lifecycleInterceptor = context.TryGetLifecycleInterceptor();

lifecycleInterceptor.SubjectAttached += change =>
{
    Console.WriteLine($"Subject attached: {change.Subject}");
};

lifecycleInterceptor.SubjectDetached += change =>
{
    Console.WriteLine($"Subject detached: {change.Subject}");
};
```

**SubjectAttached** fires when:
- A subject is created with a context (`new Person(context)`) - context-only attachment
- A subject is assigned to an intercepted property (e.g. `parent.Child = child`) - property attachment

**SubjectDetached** fires when:
- A subject is removed from an intercepted property (e.g. `parent.Child = null`) - property detachment
- A subject's fallback context is removed (triggered by `ContextInheritanceHandler`) - context-only detachment

**Two-phase detachment** (requires `WithContextInheritance()`): When a subject loses its last property reference, two detach events fire:
1. **Context-only detach** (`Property == null`) - the subject leaves lifecycle entirely, triggered by `ContextInheritanceHandler`
2. **Property detach** (`Property != null`) - the property reference is removed

This two-phase pattern is triggered by `ContextInheritanceHandler`, which automatically removes the fallback context when `ReferenceCount` reaches 0. Without context inheritance, only the property detach fires.

**Event Order During Attach:**
1. `ILifecycleHandler.OnSubjectAttached()` - handlers in forward order
2. `IReferenceLifecycleHandler.OnSubjectAttachedToProperty()` - handlers in forward order
3. `SubjectAttached` event fires
4. `IPropertyLifecycleHandler.OnPropertyAttached()` - for each property

**Event Order During Detach:**
1. `IReferenceLifecycleHandler.OnSubjectDetachedFromProperty()` - handlers in reverse order
2. `IPropertyLifecycleHandler.OnPropertyDetached()` - for each property (if last detach)
3. `SubjectDetached` event fires (if last detach)
4. `ILifecycleHandler.OnSubjectDetached()` - handlers in reverse order (if last detach)

This pattern allows handlers to distinguish between:
- Property detachment (property reference removed, subject may still be referenced elsewhere)
- Final detachment (subject being removed from lifecycle/registry)

Events are useful for:
- Cache invalidation when subjects leave the lifecycle
- Dynamic subscribers that register/unregister at runtime (unlike `ILifecycleHandler` which is registered at startup)
- Integration packages (MQTT, OPC UA) that need to clean up internal state

### Handler Requirements

> **Important**: Both `ILifecycleHandler` methods and lifecycle events are invoked **synchronously inside a lock**. Handlers must follow these requirements:

1. **Must be exception-free**: Throwing exceptions will break the lifecycle pipeline for other handlers. Wrap any potentially failing operations in try-catch internally.

2. **Must be fast**: The lock is held during invocation, so blocking operations will degrade performance across the entire system. Typical handlers should complete in microseconds (e.g., dictionary operations).

3. **Dispatch long-running work**: If you need to perform I/O, network calls, or other slow operations, dispatch to an external queue and process asynchronously:

```csharp
// Good: Fast dispatch to queue
lifecycleInterceptor.SubjectDetached += change =>
{
    _cleanupQueue.Enqueue(change.Subject); // Returns immediately
};

// Bad: Blocking I/O in handler
lifecycleInterceptor.SubjectDetached += async change =>
{
    await database.DeleteAsync(change.Subject); // Blocks the lock!
};
```

4. **Thread-safe operations**: Use thread-safe data structures like `ConcurrentDictionary` with atomic operations (`TryRemove`, `TryAdd`) rather than check-then-act patterns.

> **Tip**: Multiple handlers can be ordered using `[RunsBefore]`, `[RunsAfter]`, `[RunsFirst]`, and `[RunsLast]` attributes. See [Service Ordering](interceptor.md#service-ordering) for details.

### Reference Counting

Each subject tracks how many **property references** point to it via `GetReferenceCount()`:

```csharp
var referenceCount = subject.GetReferenceCount();
// Returns the number of properties referencing this subject
// Context-only attachments do NOT increment this count
// Returns 0 for subjects created with context but not assigned to any property
```

The reference count is managed internally by the library:
- **Only property attachments** increment/decrement the reference count
- **Context-only attachments** (when subject is created with a context) do NOT affect the count
- `OnSubjectAttached`/`OnSubjectDetached` fire only on first entry / final exit from the graph
- `OnSubjectAttachedToProperty`/`OnSubjectDetachedFromProperty` fire on every property assignment/removal

The `SubjectLifecycleChange` record structure:

```csharp
public record struct SubjectLifecycleChange(
    IInterceptorSubjectContext Context,  // Context used to invoke handlers (parent's context)
    IInterceptorSubject Subject,         // The subject being attached/detached
    PropertyReference? Property,         // null for context-only changes
    object? Index,                       // for collection/dictionary items
    int ReferenceCount);                 // refs after this change
```

**Accessing services from handlers:**

```csharp
public void OnSubjectAttached(SubjectLifecycleChange change)
{
    // Use change.Context (invoking context) to access services
    var registry = change.Context.TryGetService<ISubjectRegistry>();
    // NOT: change.Subject.Context (child may not have services yet)
}
```

**Using reference count for specific scenarios:**

```csharp
// Example: Context inheritance only for first property attachment
public void OnSubjectAttachedToProperty(SubjectLifecycleChange change)
{
    if (change.ReferenceCount == 1)
    {
        // This is the first property referencing this subject
        AddFallbackContext(change.Subject, change.Property.Value.Subject.Context);
    }
}

public void OnSubjectDetachedFromProperty(SubjectLifecycleChange change)
{
    if (change.ReferenceCount == 0)
    {
        // Last property reference removed
        RemoveFallbackContext(change.Subject);
    }
}
```

This enables proper cleanup when subjects lose all property references and leave the lifecycle.

### Lifecycle Event Examples

The following examples illustrate how lifecycle events fire in different attachment scenarios.

#### Context-First Attachment

When a subject is first attached via context, then assigned to a property:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithLifecycle()
    .WithContextInheritance();

var parent = new Person(context);

// Step 1: Create child with context directly
var child = new Person(context);

// Step 2: Assign child to property
parent.Mother = child;

// Step 3: Remove child from property
parent.Mother = null;
```

| Step | Action | Handler Method | Property | RefCount |
|------|--------|----------------|----------|----------|
| 1 | `new Person(context)` | `OnSubjectAttached` | null | 0 |
| 2a | `parent.Mother = child` | `OnSubjectAttachedToProperty` | Mother | 1 |
| 3a | `parent.Mother = null` | `OnSubjectDetached` | null | 0 |
| 3b | *(continue)* | `OnSubjectDetachedFromProperty` | Mother | 0 |

- **Step 1**: Context-only attachment (no property reference)
- **Step 2a**: Property attachment increments `ReferenceCount` to 1
- **Step 3a**: Context-only detach fires via `ContextInheritanceHandler`
- **Step 3b**: Property detach completes

#### Property-Direct Attachment

When a subject is attached directly via property (most common scenario):

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithLifecycle()
    .WithContextInheritance();

var parent = new Person(context);
var child = new Person();

// Step 1: Assign child to Mother (first attachment)
parent.Mother = child;

// Step 2: Assign same child to Father (second attachment)
parent.Father = child;

// Step 3: Remove from Mother (partial detachment)
parent.Mother = null;

// Step 4: Remove from Father (final detachment)
parent.Father = null;
```

| Step | Action | Handler Method | Property | RefCount |
|------|--------|----------------|----------|----------|
| 1a | `parent.Mother = child` | `OnSubjectAttached` | Mother | 1 |
| 1b | *(continue)* | `OnSubjectAttachedToProperty` | Mother | 1 |
| 2 | `parent.Father = child` | `OnSubjectAttachedToProperty` | Father | 2 |
| 3 | `parent.Mother = null` | `OnSubjectDetachedFromProperty` | Mother | 1 |
| 4a | `parent.Father = null` | `OnSubjectDetached` | null | 0 |
| 4b | *(continue)* | `OnSubjectDetachedFromProperty` | Father | 0 |

- **Step 1**: First property attachment triggers both `OnSubjectAttached` and `OnSubjectAttachedToProperty`
- **Steps 2-3**: Intermediate changes only trigger reference handlers
- **Step 4**: Final detachment triggers both handlers (context-only detach via `ContextInheritanceHandler`)

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

## Thread Safety and Synchronization

**Parent Tracking**: `GetParents()` is thread-safe and returns an immutable snapshot (`IReadOnlyCollection<SubjectParent>`) of the parent references.

**Observable**: Thread-safe through `Subject.Synchronize()`, but observers may receive events on different threads.

**Queue Threading Model**:
- **Enqueue (producer side)**: Fully thread-safe. Can be called concurrently from multiple threads without any synchronization.
- **TryDequeue (consumer side)**: Designed for single-threaded consumption per subscription. Each subscription must have only one consumer thread calling `TryDequeue`.
- **Multiple Subscriptions**: Each subscription is independent with its own isolated queue. Different subscriptions can be consumed by different threads concurrently.
- **Guarantees**: The implementation is deadlock-free, never loses updates, and ensures all enqueued items are processed before disposal completes.

**Change Sources**: Use `SubjectChangeContext.WithSource()` to mark changes as coming from external sources:

```csharp
using (SubjectChangeContext.WithSource(mqttSource))
{
    subject.Temperature = newValue;
    // change.Source will be mqttSource, not null
}
```

For setting values from external sources with timestamps, use the `SetValueFromSource()` extension method:

```csharp
propertyReference.SetValueFromSource(
    source: mqttSource,
    changedTimestamp: DateTimeOffset.Now,
    receivedTimestamp: DateTimeOffset.Now,
    valueFromSource: newValue);
```

This prevents feedback loops where changes from external sources are written back to those same sources.

## Integration with Other Packages

The Tracking package is foundational and used by:

- **Registry**: Requires `WithLifecycle()` for subject/property registration
- **Hosting**: Requires `WithLifecycle()` for hosted service management  
- **Sources**: Uses the high-performance queue via `WithPropertyChangeQueue()` for synchronization
- **Validation**: Can trigger validation on property changes
- **Blazor**: Uses `WithPropertyChangeObservable()` for UI updates

See the individual package documentation for integration details.
