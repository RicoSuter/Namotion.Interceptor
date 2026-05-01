# Registry

The `Namotion.Interceptor.Registry` package provides a powerful tracking system that automatically discovers and manages interceptor subjects and their properties. It enables advanced features like property attributes (metadata attached to properties), dynamic property discovery, and hierarchical subject relationships. Unlike reflection-based approaches, the registry maintains full type safety while providing dynamic capabilities.

![Registry Domain](registry-domain.png)

## Setup

Enable registry tracking in your interceptor context:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithRegistry(); // Enables automatic subject and property tracking

var car = new Car(context);
```

The registry integrates with other interceptor features and automatically includes context inheritance to ensure child subjects participate in the registry.

## Accessing registry subjects and properties

Every subject provides access to its registry information:

```csharp
var tire = new Tire(context);
var registeredTire = tire.TryGetRegisteredSubject();

foreach (var prop in registeredTire.Properties)
{
    Console.WriteLine($"{prop.Name} ({prop.Type.Name})");
}
```

## Enumerate property attributes

The registry makes it easy to find metadata associated with properties:

```csharp
// Get all attributes for a specific property
var property = registered.TryGetProperty("Pressure");
foreach (var attribute in property!.Attributes)
{
    Console.WriteLine($"{attribute.AttributeName}: {attribute.GetValue()}");
}
```

Attributes inherit from properties and can hold any value a property can: a scalar, a complex object, a subject reference, a subject collection, or a subject dictionary (check `attribute.CanContainSubjects`, `attribute.IsSubjectReference`, `attribute.IsSubjectCollection`, or `attribute.IsSubjectDictionary`). Consumers that enumerate attributes decide per case how to handle each shape. `GetAllProperties()` recurses through `property.Children` but does not descend into `attribute.Children`; attribute-held subject trees are opt-in and must be walked explicitly.

## Define attributes using properties

Property attributes solve the common problem of where to store metadata about your properties. Instead of external configuration or attributes that disappear at runtime, you can define metadata as actual properties:

```csharp
[InterceptorSubject]
public partial class Tire
{
    public partial decimal Pressure { get; set; }

    [PropertyAttribute(nameof(Pressure), "Minimum")]
    public partial decimal Pressure_Minimum { get; set; }

    [PropertyAttribute(nameof(Pressure), "Maximum")] 
    public partial decimal Pressure_Maximum { get; set; }
}
```

This pattern provides several benefits:

- **Type Safety**: Metadata is strongly typed
- **Trackable**: Changes to metadata are tracked like any other property
- **Bindable**: Metadata can be bound to external sources (MQTT, OPC UA, etc.)
- **Discoverable**: Metadata is accessible at runtime through the registry

## Dynamic property and attribute creation

The registry allows you to dynamically add properties and attributes to registered subjects at runtime. This enables building flexible systems that can extend object models programmatically.

### Add properties

Use `AddProperty` to create new trackable properties on a subject:

```csharp
var registered = subject.TryGetRegisteredSubject();

// Add a simple property with getter and setter
registered.AddProperty("DynamicValue", typeof(string),
    getValue: s => _dynamicStorage["DynamicValue"],
    setValue: (s, v) => _dynamicStorage["DynamicValue"] = v);

// Add a read-only property
registered.AddProperty("ReadOnlyInfo", typeof(DateTime),
    getValue: s => DateTime.Now,
    setValue: null);
```

Use `AddDerivedProperty` to create computed properties that automatically track dependencies:

```csharp
// Add a derived property that depends on other properties
registered.AddDerivedProperty("Status", typeof(string),
    getValue: s => s.IsActive ? "Running" : "Stopped",
    setValue: null);
```

Derived properties automatically participate in change tracking and will update when their dependencies change.

### Lifecycle tracking for dynamic properties

Dynamic properties (including derived) fully participate in lifecycle tracking when `WithLifecycle()` or `WithFullPropertyTracking()` is enabled. If a dynamic property holds a reference to another subject, that subject is automatically attached to the lifecycle graph with proper reference counting. For example, a `AddDerivedProperty<Tire>("FirstTire", ...)` that returns the first tire from a collection would give that tire a reference count of 2 — one from the collection property and one from the derived property.

When the underlying data changes, derived properties are re-evaluated and lifecycle tracking reconciles the old and new subjects automatically (attaching new subjects, detaching removed ones).

When a dynamic property is added, its initial value triggers a change event with `OldValue = null`, representing a transition from "property did not exist" to its initial value. This ensures interceptors (lifecycle, change tracking, etc.) correctly process the initial state.

### Add attributes

Use `AddAttribute` on any property to attach metadata dynamically:

```csharp
var pressureProperty = registered.Properties.First(p => p.Name == "Pressure");

// Add a unit attribute
pressureProperty.AddAttribute("Unit", typeof(string),
    getValue: s => "bar",
    setValue: null);

// Add validation attributes
pressureProperty.AddAttribute("MinValue", typeof(decimal),
    getValue: s => 0.0m,
    setValue: (s, v) => /* store min value */);
```

Use `AddDerivedAttribute` to create computed metadata that updates automatically:

```csharp
// Add a derived attribute that computes the maximum based on current value
pressureProperty.AddDerivedAttribute("DynamicMax", typeof(decimal),
    getValue: s => ((decimal)pressureProperty.GetValue()!) * 1.5m,
    setValue: null);
```

This pattern is useful for creating adaptive metadata that changes based on the current state of your properties.

## Custom property initializers

Implement `ISubjectPropertyInitializer` in a .NET attribute to automatically add metadata attributes when properties are created:

```csharp
public class UnitAttribute : Attribute, ISubjectPropertyInitializer
{
    private readonly string _unit;

    public UnitAttribute(string unit) => _unit = unit;

    public void InitializeProperty(RegisteredSubjectProperty property)
    {
        property.AddAttribute("Unit", typeof(string), _ => _unit, null);
    }
}

// Usage - automatically creates a Unit attribute
[Unit("°C")]
public partial decimal Temperature { get; set; }
```

This pattern allows you to define reusable metadata behaviors that are automatically applied when subjects are registered.

## Registered methods

Methods marked with `[SubjectMethod]` (or any attribute derived from it) are collected by the source generator and registered as first-class members alongside properties and attributes. Each method is represented by a `RegisteredSubjectMethod` that carries the signature, reflection attributes, and an invocation delegate. No runtime reflection.

### Mark methods as subject methods

Apply `[SubjectMethod]` directly, or derive your own attribute from `SubjectMethodAttribute` to carry domain-specific metadata. The source generator picks up either:

```csharp
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Tire
{
    public partial decimal Pressure { get; set; }

    [SubjectMethod]
    public void Inflate(decimal bar) => Pressure += bar;
}

// Derived attribute adds domain metadata; source generator still discovers it
[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute : SubjectMethodAttribute
{
    public string? DisplayName { get; set; }
}

[InterceptorSubject]
public partial class Compressor
{
    [Command(DisplayName = "Start Pump")]
    public void Start() { /* ... */ }
}
```

### Enumerate and invoke registered methods

Methods are exposed on both the subject (`IInterceptorSubject.Methods`) and the registered subject (`RegisteredSubject.Methods` / `TryGetMethod`):

```csharp
var registered = tire.TryGetRegisteredSubject()!;
foreach (var method in registered.Methods)
{
    Console.WriteLine($"{method.Name} ({method.ReturnType.Name})");
}

var inflate = registered.TryGetMethod("Inflate");
inflate?.Invoke([0.2m]);
```

`RegisteredSubjectMethod.Invoke` takes an `object?[]` of parameters in declared order. The subject instance is resolved automatically from `method.Parent.Subject`. It returns the method result, or `null` for void methods. Method-level .NET reflection attributes are available via `method.ReflectionAttributes`.

When `[SubjectMethod]` is combined with the `WithoutInterceptor` pattern (see [Generator / Subject Methods](generator.md#subject-methods)), `Invoke` routes through the generated public wrapper, so any registered `IMethodInterceptor` runs on registry-driven invocations — not only on direct calls. `SubjectMethodMetadata.IsIntercepted` reports whether dispatch flows through the interceptor chain.

### Method initializers

Implement `ISubjectMethodInitializer` on a .NET attribute to attach metadata to registered methods during subject attach. This is the parallel of `ISubjectPropertyInitializer`:

```csharp
public sealed class CommandAttribute : SubjectMethodAttribute, ISubjectMethodInitializer
{
    public string? DisplayName { get; set; }

    public void InitializeMethod(RegisteredSubjectMethod method)
    {
        method.AddAttribute("DisplayName", typeof(string), _ => DisplayName ?? method.Name, null);
    }
}
```

The registry invokes `InitializeMethod` once per registered method on attach. An `ISubjectMethodInitializer` registered as a context service applies to every method on every subject.

### Method attributes

Methods support the same attribute model as properties. Use `method.AddAttribute(...)` from a method initializer (as above), or declare a property-backed method attribute via `[MethodAttribute]`:

```csharp
[InterceptorSubject]
public partial class Compressor
{
    public partial CompressorStatus Status { get; set; }

    [SubjectMethod]
    public void Start() { /* ... */ }

    [Derived]
    [MethodAttribute(nameof(Start), "IsEnabled")]
    public bool Start_IsEnabled => Status == CompressorStatus.Stopped;
}
```

The `Start_IsEnabled` property is registered as an attribute on the `Start` method and is trackable and derived like any other property.

## Subject IDs

The registry provides a subject ID system that assigns stable string identifiers to subjects. This is useful for protocol-level lookups where connectors (e.g., WebSocket) need to identify subjects by string IDs.

### Assign and retrieve IDs

Use `GetOrAddSubjectId` to lazily generate a stable 22-character base62-encoded ID, or `SetSubjectId` to assign a known ID **before** the subject has one:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry();

var car = new Car(context);

// Option A: Generate a stable ID on first call, return the same ID on subsequent calls
var id = car.GetOrAddSubjectId(); // e.g. "5Gk3mR7pLqWx9nYvBtHz01"

// Option B: Assign a known ID (e.g., from an incoming protocol message)
// Must be called before the subject has an ID; reassignment throws.
var car2 = new Car(context);
car2.SetSubjectId("my-car-001");
```

### Look up subjects by ID

Use `ISubjectIdRegistry` to look up subjects by their assigned ID:

```csharp
var idRegistry = context.GetService<ISubjectIdRegistry>();
if (idRegistry.TryGetSubjectById("my-car-001", out var subject))
{
    // subject is the Car instance
}
```

### ID assignment rules

- **Immutable after first assignment**: Once a subject has an ID, calling `SetSubjectId` with a *different* ID throws `InvalidOperationException`. This prevents accidental ID conflicts in concurrent scenarios.
- **Same-ID is a no-op**: Calling `SetSubjectId` with the same ID that is already assigned is safe and does nothing.
- **Unique across subjects**: Calling `SetSubjectId` with an ID that is already in use by a different subject throws `InvalidOperationException`.

### Lifecycle integration

Subject IDs are automatically managed during the subject lifecycle:

- **On attach**: If a subject already has an ID (e.g., set before attachment), it is auto-registered in the reverse index. If the ID conflicts with an existing subject, registration is silently skipped to avoid aborting the lifecycle.
- **On detach**: The reverse index entry is automatically cleaned up.
- **Deferred reverse-index registration**: `SetSubjectId` and `GetOrAddSubjectId` only store the ID in the subject's `Data` dictionary until the subject is attached to the graph via the lifecycle. The reverse index (`TryGetSubjectById`) is populated by the lifecycle attach handler, preventing orphaned index entries for subjects that are never attached.

### Without a registry

Subject IDs also work without a registry configured — IDs are stored directly in the subject's `Data` dictionary. However, the reverse index lookup (`TryGetSubjectById`) requires a registry.
