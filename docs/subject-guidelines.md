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

C# does not allow an override to change accessor visibility, so a restricted setter has to be declared on the base and repeated on the override:

```csharp
[InterceptorSubject]
public partial class Bird
{
    public virtual partial string Name { get; protected set; }
}

[InterceptorSubject]
public partial class Parrot : Bird
{
    public override partial string Name { get; protected set; }
}
```

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

## Constructor Dependency Injection

Subjects can receive DI-injected services via constructor parameters alongside `IInterceptorSubjectContext`. When you define a constructor that accepts additional parameters, the source generator detects the user-defined constructor and does not generate an additional one.

### Pattern

```csharp
[InterceptorSubject]
public partial class ShellyDevice
{
    public ShellyDevice(
        IHttpClientFactory httpClientFactory,
        ILogger<ShellyDevice> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }
}
```

### How It Works

1. **ActivatorUtilities resolution**: When the subject is instantiated via DI (e.g., through `AddSubject`), `ActivatorUtilities.CreateInstance` resolves all constructor parameters from the service provider. Services like `IHttpClientFactory`, `ILogger<T>`, and any other registered services are injected automatically.

2. **Interaction with AddSubject**: `AddSubject<T>` applies its context unconditionally after construction, a private context or the one `contextResolver` returns (see [Hosting](hosting.md#addsubjectt)), so the subject is attached regardless of its constructor shape. A constructor taking an `IInterceptorSubjectContext` is still used when one exists, but it confers no advantage: a subject with only DI parameters is attached just the same.

   `configure` always runs before the attach `AddSubject` itself performs, so on every constructor shape the subject is fully configured before anything can start it. Without a `contextResolver` the subject is constructed and configured before it joins any context, so those assignments are never intercepted. With one, a start deferral spans construction and `configure`, and what differs is interception: a generated context constructor attaches during construction, so `configure` runs against an attached subject and its assignments are intercepted and tracked, while every shape that does not attach during construction, including one that declares an `IInterceptorSubjectContext` parameter and never attaches with it, is still unattached when `configure` runs and those assignments are not intercepted. See [Hosting](hosting.md#addsubjectt) for the full picture.

### Examples in the Codebase

- **ShellyDevice** (`Namotion.Devices.Shelly`): Injects `IHttpClientFactory` and `ILogger<ShellyDevice>` for HTTP communication with the device.
- **HueBridge** (`Namotion.Devices.Philips.Hue`): Injects `ILogger<HueBridge>` only. It creates its own `HttpClient` rather than taking an `IHttpClientFactory`, because the bridge needs a handler that accepts its self-signed certificate.
- **OpcUaSubjectServer** (`Namotion.Interceptor.OpcUa`): Injects OPC UA server configuration and telemetry services.

A subject that can also be created as a copy, by a deserializer or a connector mirroring a remote one, takes no services in its constructor. It resolves them in its service instead, as the device pattern below shows.

## Implementing Hosted Subjects for DI

> See [Hosting](hosting.md) for foundational concepts on hosted subjects and the hosting lifecycle.

A device library gives its subject a hosted service rather than making the subject one, so the same type can be the real device in one process and a passive copy mirrored by a connector in another. Who runs the service and who must not is in [A Subject With a Service](hosting.md#a-subject-with-a-service). A subject whose only purpose is a background loop can still extend `BackgroundService`, and the DI extension method below is the same for both.

### Device Pattern

The subject is data: a parameterless constructor, configuration and state properties, and operations. Everything that talks to the device lives in an internal service the subject creates:

```csharp
[InterceptorSubject]
public partial class Thermostat : ISubjectHostedServiceFactory
{
    private ThermostatPoller? _activeService;

    // Read by a derived getter but not device data, so a field: see the rules below.
    internal ThermostatInformation? Information;

    public partial string? HostAddress { get; set; }

    public partial bool IsConnected { get; internal set; }

    public partial decimal? Temperature { get; internal set; }

    [Derived]
    public string? FirmwareVersion => Information?.FirmwareVersion;

    public Thermostat()
    {
        HostAddress = null;
        IsConnected = false;
        Temperature = null;
    }

    public Task SetTargetTemperatureAsync(decimal temperature, CancellationToken cancellationToken)
        => (Volatile.Read(ref _activeService) ?? throw new InvalidOperationException("The thermostat is not running."))
            .SetTargetTemperatureAsync(temperature, cancellationToken);

    IHostedService ISubjectHostedServiceFactory.CreateHostedService(IServiceProvider serviceProvider)
        => new ThermostatPoller(this, serviceProvider.GetService<IHttpClientFactory>());

    internal void SetActiveService(ThermostatPoller service) => Volatile.Write(ref _activeService, service);

    // Compare and exchange, so a service that finishes after its successor started does not clear it.
    internal void ClearActiveService(ThermostatPoller service) => Interlocked.CompareExchange(ref _activeService, null, service);
}

internal sealed record ThermostatInformation(string FirmwareVersion);

internal sealed class ThermostatPoller : BackgroundService
{
    private readonly Thermostat _device;
    private readonly HttpClient _client;

    public ThermostatPoller(Thermostat device, IHttpClientFactory? httpClientFactory)
    {
        _device = device;
        _client = httpClientFactory?.CreateClient() ?? new HttpClient();
    }

    public Task SetTargetTemperatureAsync(decimal temperature, CancellationToken cancellationToken)
        => _client.PutAsJsonAsync($"http://{_device.HostAddress}/target", temperature, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _device.SetActiveService(this);
        try
        {
            _device.Information = await _client.GetFromJsonAsync<ThermostatInformation>($"http://{_device.HostAddress}/info", stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                _device.Temperature = await _client.GetFromJsonAsync<decimal>($"http://{_device.HostAddress}/temperature", stoppingToken);
                _device.IsConnected = true;
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
        finally
        {
            _device.ClearActiveService(this);
            _device.IsConnected = false;
        }
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
```

What the pattern requires:

- **No services in the subject's constructor.** Whatever creates the subject as data has none to give. `CreateHostedService` receives the activator's service provider, which may resolve nothing, so resolve with `GetService` and fall back.
- **Service state is a field, never a property.** The generator registers every non-static property, internal ones included, as subject data, so state the service keeps for derived getters that is not device data goes in a field.
- **Operations live on the subject and forward to the running service**, throwing when none runs.
- **The service clears connection state on stop**, so a stopped device does not read as connected and operations gated on it are disabled.
- **The service keeps its own retry loop** and reports a lost connection through the subject's state. A fault escaping `ExecuteAsync` stops the service until the subject next enters the graph.
- **Child subjects that have a service are activated by their creator.** A service that creates such children activates each one with `child.ActivateHostedService(serviceProvider)`, passing on the provider `CreateHostedService` received, because a context that opted out of automatic activation runs only what the creator activated.
- **A subject that needs more in a private context implements `IPrivateContextConfigurator`**, for example with `context.WithRegistry()` when its service resolves registered properties. See [Running one without a host](hosting.md#running-one-without-a-host) for when it is called.
- **A service that replaces child devices detaches each replaced child's activation**, with `child.DetachHostedService(attachment)` on the attachment `ActivateHostedService` returned. A child only dropped from the graph keeps its activation, and a self-contained host keeps tracking it until it stops.

### DI Extension Method

Provide an `AddX` and `AddKeyedX` pair over `AddSubject<T>` and `AddKeyedSubject<T>`, passing `contextResolver` through so the caller picks the mode:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace MyLibrary;

public static class ThermostatServiceCollectionExtensions
{
    /// <summary>
    /// Registers a thermostat and runs it. Without <paramref name="contextResolver"/> it runs in a
    /// private context; with it, it joins the resolved context, which must have hosting.
    /// </summary>
    public static IServiceCollection AddThermostat(
        this IServiceCollection services,
        Action<Thermostat>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver = null)
        => services.AddSubject(configure, contextResolver);

    /// <summary>Registers one of several thermostats under a key. See <see cref="AddThermostat"/>.</summary>
    public static IServiceCollection AddKeyedThermostat(
        this IServiceCollection services,
        object? serviceKey,
        Action<Thermostat>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver = null)
        => services.AddKeyedSubject(serviceKey, configure, contextResolver);
}
```

Do not register the same subject with `AddHostedService<T>` as well, because that is a second owner and a second start.

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
services.AddThermostat();

// With configuration
services.AddThermostat(thermostat => thermostat.HostAddress = "192.168.1.20");

// Several, each in a private context
services.AddKeyedThermostat("living-room", thermostat => thermostat.HostAddress = "192.168.1.20");
services.AddKeyedThermostat("bedroom", thermostat => thermostat.HostAddress = "192.168.1.21");

// Without dependency injection
var thermostat = new Thermostat { HostAddress = "192.168.1.20" };
await using var running = await thermostat.StartAsync(cancellationToken);
```

### Context Support (Optional)

No constructor parameter is needed for the context. `AddSubject` applies its context after construction, so a subject with a parameterless constructor, or one taking only DI services, is attached just the same:

```csharp
public MySubject(IMyDriver driver, ILogger<MySubject> logger)
{
    // No context parameter. AddSubject attaches the subject after construction,
    // and runs its configure callback before that attach.
}
```

Declare an `IInterceptorSubjectContext` parameter only when the constructor genuinely needs the context, for example to build child subjects.

What changes if it does is what gets intercepted, not what a start can see, and only with a `contextResolver`: without one, `AddSubject` constructs the subject detached whatever its shape. The generated context constructor attaches the subject itself, so by the time `AddSubject` runs `configure` the subject is already in the graph and its assignments are intercepted and tracked. Without such a constructor `AddSubject` attaches after `configure`, so those assignments are not intercepted. No start observes a half written subject either way, because `AddSubject` holds a start deferral across construction and `configure` on a shared context. A constructor that declares the parameter but never calls `AddFallbackContext` with it behaves like one that never declared it. Without a `contextResolver` the constructor receives an empty placeholder context that is removed after `configure`, so use it only to build children, and read services through the subject's own context once it is attached.

### Restart Contract

A subject implementing `IHostedService` is restarted in place, on the same instance, whenever it leaves the graph and re-enters it, so `ExecuteAsync` must tolerate being run more than once. See [Subject as Hosted Service](hosting.md#subject-as-hosted-service) for what that requires of the implementation.
