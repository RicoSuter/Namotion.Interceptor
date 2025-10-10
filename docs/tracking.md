# Tracking

The `Namotion.Interceptor.Tracking` package provides comprehensive change tracking for interceptor subjects, including property value changes, derived property updates, subject lifecycle events, and parent-child relationships. It offers two high-performance mechanisms for observing changes: **Observable** (Rx-based) and **Channel** (System.Threading.Channels-based), with Channel being the preferred choice for high-throughput scenarios.

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
- Property changed channel (high-performance)

You can also enable features individually for more granular control.

## Change Tracking: Observable vs Channel

The Tracking package provides two mechanisms for monitoring property changes, each optimized for different use cases.

### Property Changed Observable (Rx-based)

The Observable approach uses Reactive Extensions (Rx) and is ideal for UI scenarios, complex query composition, and when you need rich operator support:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyChangedObservable();

context
    .GetPropertyChangedObservable()
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

### Property Changed Channel (High Performance)

The Channel approach uses `System.Threading.Channels` and is optimized for maximum throughput with minimal allocations. **This is the preferred mechanism for high-performance scenarios** such as background services, IoT data processing, and source synchronization:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyChangedChannel();

using var subscription = context.CreatePropertyChangedChannelSubscription();

await foreach (var change in subscription.Reader.ReadAllAsync(cancellationToken))
{
    Console.WriteLine(
        $"Property '{change.Property.Name}' changed " +
        $"from '{change.GetOldValue<object>()}' to '{change.GetNewValue<object>()}'.");
}
```

**Channel Performance Characteristics:**

1. **Zero-allocation value storage**: Primitive types (int, decimal, bool, etc.) and small structs are stored inline without boxing
2. **Lock-free queuing**: Uses unbounded channels with lock-free write operations
3. **Backpressure handling**: Natural flow control through channel capacity
4. **Single-reader optimization**: Designed for efficient single-consumer scenarios

**Channel Use Cases:**
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
    .WithPropertyChangedObservable();

context.GetPropertyChangedObservable().Subscribe(change =>
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

**Channel**: Lock-free writes, thread-safe reads. Each subscription gets its own channel for isolation.

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
- **Sources**: Uses `WithPropertyChangedChannel()` for high-performance synchronization
- **Validation**: Can trigger validation on property changes
- **Blazor**: Uses `WithPropertyChangedObservable()` for UI updates

See the individual package documentation for integration details.
