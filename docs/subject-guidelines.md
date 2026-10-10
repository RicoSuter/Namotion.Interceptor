# POCO Design Guidelines

## Introduction

This guide helps you design POCOs (Plain Old CLR Objects) that work correctly with Namotion.Interceptor. The library uses C# 13 partial properties and source generation to add property interception at compile-time.

**The golden rule**: Mark all stored properties as `partial` and initialize them in constructors. Most C# patterns work naturally - this guide focuses on **what to watch out for** and **what doesn't work**.

For conventions on which code may change which property, see [Modeling Recommendations](#modeling-recommendations).

## Quick Start

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }
    public partial int Age { get; set; }
    
    [Derived]
    public string FullName => $"{FirstName} {LastName}";
    
    public Person()
    {
        // Must initialize in constructor (no field initializers on partial properties)
        FirstName = string.Empty;
        LastName = string.Empty;
        Age = 0;
    }
}
```

## ⚠️ Critical: Collections Must Be Replaced, Not Mutated

**This is the most common mistake.** Property interceptors only fire when you **assign** to a property. Mutating a collection in-place doesn't call the setter.

```csharp
[InterceptorSubject]
public partial class Team
{
    public partial Person[] Members { get; set; }
    public partial Dictionary<string, Person> Roles { get; set; }
    
    public Team()
    {
        Members = [];
        Roles = new Dictionary<string, Person>();
    }
}

var team = new Team(context);

// ❌ WRONG - In-place mutations not tracked
team.Members[0] = newPerson;        // Doesn't call setter
team.Roles["leader"] = person;      // Doesn't call setter
team.Roles.Add("member", person);   // Doesn't call setter

// ✅ CORRECT - Replace entire collection
team.Members = [person1, person2];
team.Members = [..team.Members, person3];  // Spread into new array
team.Roles = new Dictionary<string, Person>(team.Roles) { ["leader"] = person };
```

**Why?** Property interceptors hook into the setter. Collection mutations bypass the setter entirely.

All of the following work when replaced entirely (assigning a new value to the property):

**Supported collection types:**
- `T[]`
- `List<T>`
- `ICollection<T>` / `IReadOnlyCollection<T>` / `IReadOnlyList<T>`
- `IEnumerable<T>`
- `ImmutableArray<T>`
- `ArrayList`

**Supported dictionary types:**
- `Dictionary<K, V>`
- `IDictionary<K, V>` / `IReadOnlyDictionary<K, V>`
- `Hashtable`

### Lifecycle Tracking for Nested Subjects

When properties contain other `[InterceptorSubject]` instances, attach/detach is automatic:

```csharp
dept.Employees = [person1, person2];  // person1, person2 attached to graph
dept.Employees = [person3];           // person1, person2 detached; person3 attached
```

With `WithContextInheritance()`, attached subjects inherit the parent's context.

## ⚠️ Initialize All Properties in Constructors

Partial properties **cannot** have field initializers. Initialize in the constructor.

```csharp
[InterceptorSubject]
public partial class Entity
{
    public partial Guid Id { get; set; }
    public partial string Name { get; set; }
    
    // ❌ Won't compile
    // public partial Guid Id { get; set; } = Guid.NewGuid();
    
    // ✅ Initialize in constructor
    public Entity()
    {
        Id = Guid.NewGuid();
        Name = string.Empty;
    }
}
```

**Why initialize everything?** Uninitialized properties get default values (`null`, `Guid.Empty`, etc.) which can cause issues with registry and change tracking.

The generator creates a context constructor that chains to your parameterless constructor:
```csharp
// Generated:
public Entity(IInterceptorSubjectContext context) : this() { /* setup */ }
```

## What Doesn't Work

### Intercepted Explicit Interface Implementation

C# does not allow `partial` on an explicit interface implementation (CS0754), so an explicitly implemented property can never be intercepted. Implement the interface implicitly instead:

```csharp
public interface INamed { string Name { get; set; } }

[InterceptorSubject]
public partial class Entity : INamed
{
    // ❌ Explicit implementations cannot be partial (CS0754)
    // partial string INamed.Name { get; set; }

    // ✅ Implicit implementation supports interception
    public partial string Name { get; set; }

    public Entity() => Name = string.Empty;
}
```

A **non-partial** explicit implementation is supported and does appear in the subject's property metadata, keyed by the member's simple name, but it is not intercepted. Use it for values that are fixed or computed rather than tracked, and put attributes on the interface member rather than on the implementation. See [Interface Default Properties](generator.md#interface-default-properties) in the generator reference for that shape and the diagnostics around it.

### Abstract Properties

A partial property cannot be `abstract` (CS0750). Declare it `virtual` on the base subject and `override` it below, as under [Virtual and Override](#virtual-and-override). See [Limitations](generator.md#limitations) in the generator reference.

```csharp
[InterceptorSubject]
public abstract partial class Entity
{
    // ❌ Abstract partial properties are not supported (CS0750)
    // public abstract partial string Name { get; set; }

    // ✅ The generator supplies accessors that derived subjects can override
    public virtual partial string Name { get; set; }

    public Entity() => Name = string.Empty;
}
```

## Patterns That Work

### Virtual and Override

```csharp
[InterceptorSubject]
public partial class Animal
{
    public virtual partial string Name { get; set; }

    public Animal() => Name = string.Empty;
}

[InterceptorSubject]
public partial class Dog : Animal
{
    // ❌ Hiding an ancestor subject property creates a second slot (NI0065)
    // public new partial string Name { get; set; }

    // ✅ Override the existing property
    public override partial string Name { get; set; }
}
```

`virtual` and `override` are the only way to re-declare a property an ancestor subject already exposes. Hiding it with `new` is rejected as NI0065, whether or not the new declaration is `partial`, because one metadata key cannot reach two backing fields. `new` and `sealed` are supported where the hidden member is not an ancestor subject's property, such as a property on a plain base class. See [New and Sealed Properties](generator.md#new-and-sealed-properties) and [Fixing NI0060, NI0061 and NI0065](generator.md#fixing-ni0060-ni0061-and-ni0065) in the generator reference for the broken shapes next to the ones that build.

### Interface Default Properties

Interface default implementations are automatically included in property metadata. Mark a computed one with `[Derived]` on the interface member to enable dependency tracking when full property tracking is configured:

```csharp
public interface ITemperatureSensor
{
    double Celsius { get; set; }

    [Derived]
    double Fahrenheit => Celsius * 9.0 / 5.0 + 32;
}

[InterceptorSubject]
public partial class Sensor : ITemperatureSensor
{
    public partial double Celsius { get; set; }

    public Sensor() => Celsius = 20;
}
```

With `WithFullPropertyTracking()`, changing `Celsius` also updates tracking for `Fahrenheit`. An adopted default is a fallback, so a property declared anywhere in a hierarchy beats it. See [Interface Default Properties](generator.md#interface-default-properties) for explicit implementations and attribute limitations, and [Property Precedence Across a Hierarchy](generator.md#property-precedence-across-a-hierarchy) for a three-level example.

### Required and Init

`required` and `init` work on partial properties. Initialize an `init` property in the constructor or an object initializer. A `required` property must be supplied at the construction site:

```csharp
[InterceptorSubject]
public partial class Config
{
    public required partial string ConnectionString { get; set; }
    public partial string Environment { get; init; }

    public Config() => Environment = "Development";
}
```

```csharp
// ❌ Missing required property (CS9035)
// var config = new Config();

// ✅ Supply the required value during construction
var config = new Config { ConnectionString = "Server=localhost" };

// ❌ An init-only property cannot be assigned after construction
// config.Environment = "Production";
```

See [Init-Only and Required Properties](generator.md#init-only-and-required-properties) for use with the generated context constructor.

### Nullable Reference Types

```csharp
#nullable enable

[InterceptorSubject]
public partial class Employee
{
    public partial string FirstName { get; set; }      // Non-nullable
    public partial string? MiddleName { get; set; }    // Nullable
    
    public Employee()
    {
        FirstName = string.Empty;
        MiddleName = null;
    }
}
```

### Derived Properties

Mark computed properties with `[Derived]` for change tracking:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }
    
    [Derived]
    public string FullName => $"{FirstName} {LastName}";
    // When FirstName or LastName changes, FullName change is also fired
}
```

### Data Annotations

```csharp
[InterceptorSubject]
public partial class User
{
    [Required, MaxLength(50)]
    public partial string Username { get; set; }
    
    [EmailAddress]
    public partial string Email { get; set; }
    
    public User()
    {
        Username = string.Empty;
        Email = string.Empty;
    }
}

// Enable: context.WithDataAnnotationValidation();
```

### Property Attributes (Registry Feature)

```csharp
[InterceptorSubject]
public partial class Sensor
{
    public partial double Temperature { get; set; }
    
    [PropertyAttribute(nameof(Temperature), "Unit")]
    public partial string Temperature_Unit { get; set; }
    
    public Sensor()
    {
        Temperature = 20.0;
        Temperature_Unit = "°C";
    }
}
```

## Base Classes and Subclasses

A subject can derive from another subject: mark both classes `[InterceptorSubject]` and `partial`, and properties declared anywhere in the hierarchy are intercepted, so writing a base-declared property on a derived instance goes through the interceptor chain exactly like writing one the derived class declares. A plain class with no attribute may sit between two subjects, and a subject may be `sealed` at any level. See [Inheritance](generator.md#inheritance) in the generator reference.

Writing either side of that relationship by hand is possible but demanding. It needs a contract this page does not cover; see [Hand-written base classes and subclasses](generator.md#hand-written-base-classes-and-subclasses) in the generator reference.

## Property Change Hooks

The source generator creates optional partial method hooks for each partial property, allowing you to execute custom logic before or after property changes.

### Generated Methods

For each partial property `PropertyName`, the generator creates:
- `partial void OnPropertyNameChanging(ref TProperty newValue, ref bool cancel)` - Called before setter runs
- `partial void OnPropertyNameChanged(TProperty newValue)` - Called after successful write

### Execution Order

```
Setter: OnChanging → (if not cancelled) Interceptors → Field Update → OnChanged → PropertyChanged event
Getter: Field Read → Interceptors → Return
```

See [Interceptor Pipeline](interceptor.md#interceptor-pipeline) for how interceptors work.

### Cancellation Example

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }

    partial void OnFirstNameChanging(ref string newValue, ref bool cancel)
    {
        if (string.IsNullOrWhiteSpace(newValue))
        {
            cancel = true;  // Reject empty names
            return;
        }
        newValue = newValue.Trim();  // Or coerce the value
    }
}
```

### Post-Change Side Effects

```csharp
[InterceptorSubject]
public partial class Sensor
{
    public partial double Temperature { get; set; }

    partial void OnTemperatureChanged(double newValue)
    {
        if (newValue > 100)
        {
            Logger.LogWarning("High temperature: {Temp}", newValue);
        }
    }
}
```

### When Hooks Are Called

- `OnChanging` is always called when the setter is invoked
- `OnChanged` is only called if:
  - The change was not cancelled (`cancel` remained `false`)
  - The interceptor chain performed the write (interceptors can skip writes)
- If `OnChanged` throws, the property value is already written but `PropertyChanged` won't fire

**Use property hooks when:**
- Logic is specific to a single property
- You need access to instance members
- You want to validate, transform, or cancel changes
- You need to react to successful property changes

**Use Interceptors when:**
- Logic applies to many properties/classes
- You need cross-cutting concerns (logging, validation)
- Logic should be configurable at runtime

## INotifyPropertyChanged Support

All generated classes automatically implement `INotifyPropertyChanged` for data binding compatibility with WPF, MAUI, Blazor, and other UI frameworks.

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
}

// Usage - no extra code needed
var person = new Person(context);
person.PropertyChanged += (s, e) => Console.WriteLine($"{e.PropertyName} changed");
person.FirstName = "Rico";  // Fires PropertyChanged event
```

### Performance

The `PropertyChanged?.Invoke(...)` pattern ensures zero overhead when no handlers are subscribed - only a null check occurs. The `PropertyChangedEventArgs` is not allocated unless the event has subscribers.

### When PropertyChanged Fires

The event fires only when a property actually changes:
- Not fired if `OnChanging` cancels the change
- Not fired if an interceptor skips the write

## Summary

1. **Mark all stored properties `partial`** - Tracking everything is safer
2. **Initialize in constructors** - No field initializers on partial properties
3. **Replace collections, don't mutate** - `arr = newArray`, not `arr[0] = x`
4. **Use `[Derived]`** for computed properties
5. **Explicit implementations are not intercepted** - Use implicit implementation for tracked writes
6. **Abstract doesn't work** - Use `virtual` instead

Most other C# patterns (nullable, required, init, virtual, override, data annotations) work naturally.

## Modeling Recommendations

These recommendations are not enforced, and a subject that ignores them still works. They separate the values a subject owns from the values its callers may change, so a model stays consistent as more code, user interfaces and connectors write to it.

```csharp
[InterceptorSubject]
public partial class Pump
{
    [Required, MaxLength(50)]
    public partial string Name { get; set; }

    [Range(0, 3000)]
    public partial double TargetSpeed { get; set; }

    public partial double CurrentSpeed { get; internal set; }

    public partial PumpStatus Status { get; private set; }

    [Derived]
    public bool CanStart => Status == PumpStatus.Stopped;

    public Pump()
    {
        Name = string.Empty;
        TargetSpeed = 0;
        CurrentSpeed = 0;
        Status = PumpStatus.Stopped;
    }

    public void Start(double speed)
    {
        if (!CanStart)
        {
            throw new InvalidOperationException("The pump is not stopped.");
        }

        TargetSpeed = speed;
        Status = PumpStatus.Starting;
    }
}
```

### Non-Public Setters for Owned State

State the subject owns, because its own logic computes it or its device measures it, gets `{ get; internal set; }`, so the subject, a parent subject and helpers in the same assembly such as a protocol client or response parser can write it, or `{ get; private set; }` when only the subject itself writes it. Other code then cannot overwrite a reading or a status it does not own, and a local write to a value a connector owns would only be overwritten by the next reading.

The generator supports every accessor modifier (see [Access Modifiers](generator.md#access-modifiers)), and the modifier only restricts C# callers. The generated property metadata calls the setter from inside the class, so applied subject updates, `SetValueFromSource` and transaction commits write the property normally, with hooks, interceptors and change notifications. A non-public setter also leaves `RegisteredSubjectProperty.HasSetter` `true`, so the property is not read-only to remote clients: the OPC UA server exposes it as writable (see [AccessLevel Configuration](connectors-opcua-mapping.md#accesslevel-configuration)), and the MQTT and WebSocket servers and the ASP.NET Core update endpoint apply client writes to it. Only the MCP [`set_property`](mcp.md#set_property) tool refuses a property without a public setter. Marking such a property read-only to remote clients is not configurable yet ([#102](https://github.com/RicoSuter/Namotion.Interceptor/issues/102)).

### Public Setters with Validation for Configuration and Desired Values

Values a caller is meant to change get a public setter: configuration such as a name, an address or a polling interval, and desired values such as a target speed or a setpoint. Validation attributes reject an invalid value at the write rather than leaving it for the logic that consumes it (see [Data Annotations](#data-annotations) and [Validation](validation.md)). Data annotations also validate values a connector applies from a source, so a reported value outside the range is rejected too. To validate only local input, replace the attribute with a custom validator that checks the write's `Origin` (see [Custom Validators](validation.md#custom-validators)).

### Methods for Commands

An action such as start, stop or reset is a method, not a property the caller sets. The method checks its preconditions and changes all related state together, so the subject never shows a combination its own logic would not produce. For a device-backed subject, the method sends the command and leaves measured state to the next reading. A value computed from other properties, including a command's precondition such as `CanStart`, is a `[Derived]` property rather than a stored copy that goes stale when one write path forgets to update it (see [Derived Properties](#derived-properties)).

When the writes must succeed or fail together, run them in a [transaction](tracking-transactions.md) with `TransactionFailureHandling.Rollback`; the commit still applies and notifies them one at a time, and its rollback is best effort (see [Failure Flows and Consistency](tracking-transactions.md#failure-flows-and-consistency)).

### Properties, Not Methods, Across Connectors

Connectors synchronize properties, not methods. A remote mirror of a subject observes its state, but calling a method on the mirror runs it against the mirror's local copy: its writes travel as plain property writes, and the owning side's preconditions and logic do not run. To let a remote side request an action, model a desired-value property such as `TargetSpeed` that the remote side writes. The owning side reacts to the desired value, for example with a control loop that drives the device toward it, and checks the preconditions there, because a remote write bypasses the command method. For actions that do not map to state, use an application-level operation mechanism.

## Constructor Dependency Injection

Subjects can receive DI-injected services via constructor parameters alongside `IInterceptorSubjectContext`. When you define a constructor that accepts additional parameters, the source generator detects the user-defined constructor and does not generate an additional one.

### Pattern

```csharp
[InterceptorSubject]
public partial class DeviceGateway
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeviceGateway> _logger;

    public DeviceGateway(
        IHttpClientFactory httpClientFactory,
        ILogger<DeviceGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }
}
```

### How It Works

1. **ActivatorUtilities resolution**: When the subject is instantiated via DI (e.g., through `AddHostedSubject`), `ActivatorUtilities.CreateInstance` resolves all constructor parameters from the service provider. Services like `IHttpClientFactory`, `ILogger<T>`, and any other registered services are injected automatically.

2. **Interaction with AddHostedSubject**: The `AddHostedSubject<T>` method detects whether the subject type has a constructor accepting `IInterceptorSubjectContext`. If it does, the context is passed during construction. The `contextResolver` parameter allows overriding which context is provided.

## Implementing Hosted Subjects for DI

> See [Hosting](hosting.md) for foundational concepts on hosted subjects and the hosting lifecycle.

When creating a subject library that extends `BackgroundService`, provide a DI extension method using `AddHostedSubject<T>` from `Namotion.Interceptor.Hosting`.

### DI Extension Method

```csharp
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace MyLibrary;

public static class MySubjectServiceCollectionExtensions
{
    /// <summary>
    /// Adds MySubject as a hosted service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure the subject.</param>
    /// <param name="contextResolver">
    /// Optional context resolver. Only used if subject has a constructor accepting IInterceptorSubjectContext.
    /// </param>
    public static IServiceCollection AddMySubject(
        this IServiceCollection services,
        Action<MySubject>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}
```

### Required Project References

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.*" />
  <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="9.*" />
  <ProjectReference Include="..\Namotion.Interceptor.Hosting\Namotion.Interceptor.Hosting.csproj" />
</ItemGroup>
```

### Usage

```csharp
// Minimal
services.AddMySubject();

// With configuration
services.AddMySubject(subject =>
{
    subject.Name = "Sensor 1";
    subject.PollingInterval = TimeSpan.FromSeconds(5);
});
```

### Context Support (Optional)

If your subject needs access to the `IInterceptorSubjectContext`, add an optional parameter to the constructor. `AddHostedSubject` will automatically detect and use it:

```csharp
public MySubject(IInterceptorSubjectContext? context = null, IMyDriver? driver = null)
{
    // Context is automatically passed if:
    // 1. Subject has this constructor parameter, AND
    // 2. Context is registered in DI or provided via contextResolver
}
```
