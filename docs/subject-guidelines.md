# POCO Design Guidelines

## Introduction

This guide helps you design POCOs (Plain Old CLR Objects) that work correctly with Namotion.Interceptor. The library uses C# 13 partial properties and source generation to add property interception at compile-time.

**The golden rule**: Mark all stored properties as `partial` and initialize them in constructors. Most C# patterns work naturally - this guide focuses on **what to watch out for** and **what doesn't work**.

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

C# doesn't allow `partial` on explicit interface implementations, so an explicitly implemented property can never be intercepted.

```csharp
public interface INamed { string Name { get; set; } }

[InterceptorSubject]
public partial class Entity : INamed
{
    // ❌ Won't compile (CS0754)
    // partial string INamed.Name { get; set; }
    
    // ✅ Use implicit implementation to get interception
    public partial string Name { get; set; }
}
```

A **non-partial** explicit implementation is supported and does appear in the subject's property metadata, keyed by the member's simple name, but it is not intercepted. Use it for values that are fixed or computed rather than tracked:

```csharp
public interface IHuman { Gender Gender { get; } }
public interface IMale : IHuman { Gender IHuman.Gender => Gender.Male; }

[InterceptorSubject]
public partial class John : IMale
{
    // "Gender" is in the metadata and reads as Gender.Male. IHuman.Gender has no setter here;
    // even a writable explicit implementation would not be intercepted, since C# does not allow
    // "partial" on an explicit interface implementation.
}
```

Attributes belong on the interface member, not the implementation. See [Interface Default Properties](generator.md#interface-default-properties) in the generator documentation.

### Abstract Properties

Abstract properties can't be partial.

```csharp
public abstract class Base
{
    // ❌ Won't compile
    // public abstract partial string Name { get; set; }
    
    // ✅ Use virtual instead
    public virtual partial string Name { get; set; }
}
```

## Patterns That Work

### Virtual and Override

```csharp
[InterceptorSubject]
public partial class Animal
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Dog : Animal
{
    // Override to change accessor visibility
    public override partial string Name { get; protected set; }
}
```

`new` and `sealed` are also supported on a partial property, and are repeated on the generated half automatically. `new` is the fix for the CS0108 warning that accompanies NI0005 (see [Diagnostics](generator.md#diagnostics) in the generator documentation).

### Interface Default Properties

Interface default implementations are automatically included in property tracking. Mark computed properties with `[Derived]` for change notification:

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
    // Fahrenheit is automatically tracked from the interface
}
```

### Required and Init

```csharp
[InterceptorSubject]
public partial class Config
{
    public required partial string ConnectionString { get; set; }
    public partial string Environment { get; init; }
    
    public Config() { Environment = "Development"; }
}
```

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

A subject can derive from another subject, and properties declared anywhere in the hierarchy are
intercepted. The plumbing that interception needs (the context, the property table, the sync root and
the helper methods the generated accessors call) is emitted once, in the class at the root of the
hierarchy, and every subject below it inherits it.

```csharp
[InterceptorSubject]
public partial class PersonBase
{
    public partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Employee : PersonBase
{
    public partial string Department { get; set; }
}
```

Writing `Name` on an `Employee` goes through the interceptor chain exactly like writing `Department`.
A plain class with no attribute may sit between two subjects, and a subject may be `sealed` at any
level. Nothing below is needed for this case.

The rest of this section covers the two hand-written directions: a base class you write yourself that
hosts generated subclasses, and a subclass you write yourself under a generated base class.

### Writing a base class by hand

A class can host generated subclasses when it exposes all of the following. A generated subject
satisfies this by construction, so this only matters for a base class you write yourself.

| Member | Needed by |
|--------|-----------|
| implements `IInterceptorSubject` | everything else |
| implements `IRaisePropertyChanged`, on the base class or on the subject | the subject not re-declaring `PropertyChanged` and `RaisePropertyChanged` |
| `protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)` | generated getters |
| `protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)` | generated setters |
| `protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)` | generated method wrappers |
| `protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()` | the subject's own `IInterceptorSubject.Properties` |
| `public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties` | merging the subject's properties with the base class ones |

Details that are easy to get wrong:

- Members may be more accessible than listed, and a member the base class itself inherits from
  further up counts. A generic base class is checked with its type arguments substituted.
- `InvokeMethod`'s last parameter must really be `params`. The generated call site passes arguments in
  expanded form, and the check tests for `params` explicitly, so the same parameter types without it
  fail the contract. The subject then falls back to emitting its own plumbing and NI0012 is reported,
  which `TreatWarningsAsErrors` turns into a build error.
- `DefaultProperties` may be a static property or a static field, but its type has to be
  `IReadOnlyDictionary<string, SubjectPropertyMetadata>` or something that implements it. A static of
  that name with any other type is reported rather than accepted.
- `GetInstanceProperties` may likewise return something that implements the dictionary interface, such
  as `FrozenDictionary<string, SubjectPropertyMetadata>?`, but it has to be a reference type. The
  generated code combines the two as `GetInstanceProperties() ?? DefaultProperties`, and `??` rejects a
  value type on its left, so a struct implementing the interface fails the contract even though the
  same struct is accepted for `DefaultProperties`, which is only concatenated.
- The `IRaisePropertyChanged` row is the only one that is not needed for the generated code to
  compile. A base class that satisfies everything else but not that one still produces code that
  compiles, with the subject declaring its own change notification plumbing, but it fails the
  contract all the same: the subject re-emits the whole plumbing block and NI0012 is reported, which
  `TreatWarningsAsErrors` turns into a build error.

What happens when a base class does not satisfy the contract depends on `DefaultProperties`. If it is
present and usable, the subject falls back to emitting its own plumbing and the generator reports
NI0012: the code compiles and behaves as it did before the plumbing became shared, which means
properties declared on that base class are not intercepted. If `DefaultProperties` is missing or
unusable as well, the generator reports NI0011 and generates nothing for the subject.

Here is a base class that satisfies the whole contract:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

public class TrackedEntityBase : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
{
    private IInterceptorExecutor? _context;
    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

    public event PropertyChangedEventHandler? PropertyChanged;

    void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    IInterceptorSubjectContext IInterceptorSubject.Context
        => InterceptorExecutor.GetOrCreate(ref _context, this);

    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();

    object IInterceptorSubject.SyncRoot { get; } = new object();

    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
        => GetInstanceProperties() ?? DefaultProperties;

    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
        => _properties = ((IInterceptorSubject)this).Properties
            .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
            .ToFrozenDictionary();

    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

    protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

    protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
        => _context is not null ? _context.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

    protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue,
        Action<IInterceptorSubject, TProperty> setValue)
    {
        if (_context is null)
        {
            setValue(this, newValue);
            return true;
        }

        return _context.SetPropertyValue(propertyName, newValue, currentValue, setValue);
    }

    protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod,
        params object?[] parameters)
        => _context is not null ? _context.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
}

[InterceptorSubject]
public partial class Machine : TrackedEntityBase
{
    public partial string SerialNumber { get; set; }
}
```

### Three things the compiler cannot check for you

The list above is checked by looking at member signatures, which cannot see what the members do.
Three requirements are behavioural, and a base class that gets one of them wrong passes every check
and then misbehaves at runtime.

1. **`AddProperties` must merge starting from `((IInterceptorSubject)this).Properties`**, not from its
   own `DefaultProperties` and not from its own backing field, and it must store the result in the
   field that `GetInstanceProperties()` returns. Merging from its own field drops the subclass's
   `DefaultProperties` on the first call, so the subject loses its own generated properties.
2. **The three helpers must route through the same executor that `IInterceptorSubject.Context`
   publishes for that instance.** A base class that keeps a second executor for the helpers still
   compiles, and reproduces the exact bug that per hierarchy plumbing was introduced to fix: writes
   look fine and no interceptor ever sees them.
3. **`IInterceptorSubject.Context` must return an `IInterceptorExecutor` built for that instance.**
   `InterceptorExecutor` binds to its subject when it is constructed, and other parts of the library
   cast `Context` to `IInterceptorExecutor` without checking, so a borrowed or shared context
   misroutes every property reference.

### Writing a subclass by hand

A hand-written class can derive from a generated subject and implement intercepted properties itself
by calling the same four protected members the generated code uses. They are generated implementation
detail: they are documented so this scenario is usable, not as a stable API.

Such a class has no generated `DefaultProperties`, so it has to register its own property metadata by
calling `((IInterceptorSubject)this).AddProperties(...)`. **That registration has to happen before the
first intercepted write**, not merely somewhere in the class. The base class's generated
`Subject(IInterceptorSubjectContext context)` constructor publishes the context before the subclass
constructor body runs, so a write in that body already reaches the interceptor chain, and the chain
throws `InvalidOperationException` when it looks up a property name that was never registered.

```csharp
using System;
using System.Collections.Generic;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Device
{
    public partial string Name { get; set; }
}

public class CustomDevice : Device
{
    private string _location = string.Empty;

    public CustomDevice(IInterceptorSubjectContext context) : base(context)
    {
        // Register before the first write: the base constructor has already published the context.
        ((IInterceptorSubject)this).AddProperties(
            new SubjectPropertyMetadata(
                nameof(Location),
                typeof(string),
                [],
                subject => ((CustomDevice)subject).Location,
                (subject, value) => ((CustomDevice)subject).Location = (string)value!,
                isIntercepted: true,
                isDynamic: false));

        Location = "unknown";
    }

    public string Location
    {
        get => GetPropertyValue(nameof(Location), static subject => ((CustomDevice)subject)._location);
        set => SetPropertyValue(nameof(Location), value, _location,
            static (subject, newValue) => ((CustomDevice)subject)._location = newValue);
    }
}
```

One thing to avoid anywhere below a subject: do not declare a member named `GetPropertyValue`,
`SetPropertyValue`, `InvokeMethod` or `GetInstanceProperties` for something else, and do not implement
`IInterceptorSubject.Context`, `Data`, `SyncRoot` or `AddProperties` yourself. Either one takes over
what the base class provides. Where a generated subject declares such a member, or sits below a class
that does, the generator reports NI0013 or NI0014.

A hand-written class with no subject below it is not scanned by the generator at all, so no NI0013
reaches it. The compiler covers that case instead. The four helpers are `protected`, so a member that
genuinely hides one of them is CS0108, and `TreatWarningsAsErrors` turns that into a build error: a
method with the same signature as one of the four, or a field, property or event named `InvokeMethod`
or `GetInstanceProperties`, the two helpers that are not generic. Add `new` where the hiding is
intended, or rename the member. An overload that differs in signature stays silent, and there it is
also harmless, because the generated calls sit in the subject's own file above such a class. See
[Hierarchy Hazards](generator.md#hierarchy-hazards) for why it matters.

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
5. **Explicit interfaces don't work** - Use implicit implementation
6. **Abstract doesn't work** - Use `virtual` instead

Most other C# patterns (nullable, required, init, virtual, override, data annotations) work naturally.

## Constructor Dependency Injection

Subjects can receive DI-injected services via constructor parameters alongside `IInterceptorSubjectContext`. When you define a constructor that accepts additional parameters, the source generator detects the user-defined constructor and does not generate an additional one.

### Pattern

```csharp
[InterceptorSubject]
public partial class HueBridge
{
    public HueBridge(
        IHttpClientFactory httpClientFactory,
        ILogger<HueBridge> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }
}
```

### How It Works

2. **ActivatorUtilities resolution**: When the subject is instantiated via DI (e.g., through `AddHostedSubject`), `ActivatorUtilities.CreateInstance` resolves all constructor parameters from the service provider. Services like `IHttpClientFactory`, `ILogger<T>`, and any other registered services are injected automatically.

3. **Interaction with AddHostedSubject**: The `AddHostedSubject<T>` method detects whether the subject type has a constructor accepting `IInterceptorSubjectContext`. If it does, the context is passed during construction. The `contextResolver` parameter allows overriding which context is provided.

### Examples in the Codebase

- **HueBridge** (`Namotion.Devices.Philips.Hue`): Injects `IHttpClientFactory` and `ILogger<HueBridge>` alongside the interceptor context for HTTP communication with the Hue Bridge API.
- **OpcUaSubjectServer** (`Namotion.Interceptor.OpcUa`): Injects OPC UA server configuration and telemetry services.

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
