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
- Context inheritance for child subjects
- Derived property change detection
- Property changed observable (Rx-based)
- Property changed queue (high performance)

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
            $"from '{change.GetOldValue<object>()}' to '{change.GetNewValue<object>()}'.");
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
        $"from '{change.GetOldValue<object>()}' to '{change.GetNewValue<object>()}'.");
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
    Console.WriteLine($"{change.Property.Name}: {change.GetOldValue<object>()} → {change.GetNewValue<object>()}");
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

Track when subjects are attached to or detached from the subject graph:

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
var child = new Person(context) { Name = "Child" };

person.Children = [child]; // AttachSubject called for child
person.Children = [];      // DetachSubject called for child

public class MyLifecycleHandler : ILifecycleHandler
{
    public void AttachSubject(SubjectLifecycleChange change)
    {
        Console.WriteLine($"Attached: {change.Subject} via property {change.Property?.Value.Name}");
    }

    public void DetachSubject(SubjectLifecycleChange change)
    {
        Console.WriteLine($"Detached: {change.Subject} via property {change.Property?.Value.Name}");
    }
}
```

**Lifecycle tracking is used by:**
- **Hosting package**: Start/stop `IHostedService` implementations when attached/detached
- **Registry package**: Track subjects and properties in the registry
- **Sources package**: Subscribe/unsubscribe from external data sources
- **Derived property detection**: Initialize derived properties on attach

## Parent-Child Relationship Tracking

Tracks parent-child relationships in the subject graph, enabling upward navigation:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithParents();

var car = new Car(context);
var tire = new Tire(context);

car.Tires = [tire];

var parents = tire.GetParents(); // Returns [(car, "Tires", 0)]
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

var recorder = context.GetService<ReadPropertyRecorder>();

var accessedProperties = new HashSet<PropertyReference>();
using (recorder.StartRecording(accessedProperties))
{
    var fullName = person.FullName; // Records FirstName and LastName
}

// accessedProperties now contains references to FirstName and LastName
```

This is primarily used internally by the derived property change detection system but can also be used for custom scenarios.

## Thread Safety and Synchronization

**Observable**: Thread-safe through `Subject.Synchronize()`, but observers may receive events on different threads.

**Queue Threading Model**:
- **Enqueue (producer side)**: Fully thread-safe. Can be called concurrently from multiple threads without any synchronization.
- **TryDequeue (consumer side)**: Designed for single-threaded consumption per subscription. Each subscription must have only one consumer thread calling `TryDequeue`.
- **Multiple Subscriptions**: Each subscription is independent with its own isolated queue. Different subscriptions can be consumed by different threads concurrently.
- **Guarantees**: The implementation is deadlock-free, never loses updates, and ensures all enqueued items are processed before disposal completes.

**Change Sources**: Use `SubjectMutationContext.ApplyChangesWithSource()` to mark changes as coming from external sources:

```csharp
SubjectMutationContext.ApplyChangesWithSource(mqttSource, () =>
{
    subject.Temperature = newValue;
    // change.Source will be mqttSource, not null
});
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
