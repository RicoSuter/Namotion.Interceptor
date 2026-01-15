[![NuGet](https://img.shields.io/nuget/v/Namotion.Interceptor.svg)](https://www.nuget.org/packages/Namotion.Interceptor)

> **Note**: This library is in active development. APIs may change between versions.

# Namotion.Interceptor for .NET

Namotion.Interceptor is a .NET library for building reactive applications with automatic property tracking and change propagation. It uses **C# 13 partial properties** and **source generation** to transform your classes into observable object graphs - without runtime reflection or proxy objects.

Simply mark your classes with `[InterceptorSubject]` and declare properties as `partial`. The source generator handles the rest: creating interception logic, change detection, derived property updates, and lifecycle management. Your domain models remain clean POCOs while gaining powerful reactive capabilities.

The library supports **bidirectional synchronization** with external systems like MQTT brokers, OPC UA servers, and databases. When a property changes locally, connectors automatically propagate the change to external systems. When external data arrives, your object model updates and triggers change notifications - enabling real-time data synchronization for industrial automation, IoT applications, and reactive services.

![](features.png)

## Why Namotion.Interceptor?

- **Reactive object graphs** - Property changes automatically propagate to derived properties and external systems
- **Zero runtime reflection** - All interception logic is generated at compile-time for maximum performance
- **Bidirectional synchronization** - Connect your object model to MQTT brokers, OPC UA servers, or databases with minimal code
- **Clean domain models** - Your classes stay as POCOs with simple attributes

## Core Concepts

### Interceptor Subjects

An **interceptor subject** is a class marked with `[InterceptorSubject]` that uses C# 13 partial properties. The source generator transforms your partial properties into fully tracked properties that route through an interception pipeline - no runtime reflection required.

For detailed patterns and best practices, see the [Subject Design Guidelines](docs/subject-guidelines.md).

```csharp
// Your code:
[InterceptorSubject]
public partial class Car
{
    public partial string Name { get; set; }
    public partial Tire[] Tires { get; set; }
}

// Generated code (simplified):
public partial class Car : IInterceptorSubject
{
    private string _name;

    public partial string Name
    {
        get => Context.GetPropertyValue("Name", () => _name);
        set => Context.SetPropertyValue("Name", value, v => _name = v);
    }

    // Constructor, metadata, IInterceptorSubject implementation...
}
```

### Derived Properties

Mark computed properties with `[Derived]` for automatic dependency tracking. When any property used in the calculation changes, the derived property is automatically re-evaluated and fires its own change event:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";
}

person.FirstName = "John";
// Fires: FirstName changed, FullName changed

person.LastName = "Doe";
// Fires: LastName changed, FullName changed
```

The system records which properties are read during derived property evaluation, then automatically recalculates when any dependency changes. This works across the entire object graph.

### Interception Pipeline

Property reads and writes flow through a configurable chain of interceptors. Each interceptor receives a `next` delegate and can run code **before** and **after** calling it. The "after" code runs in reverse order, creating a nested pipeline:

**Write Pipeline** (`IWriteInterceptor`):
```
person.Name = "John"
    │
    ▼
┌─ Interceptor 1 ─────────────────────────────┐
│  (before next)  validate, transform, etc.   │
│      │                                      │
│      ▼                                      │
│  ┌─ Interceptor 2 ───────────────────────┐  │
│  │  (before next)  equality check        │  │
│  │      │                                │  │
│  │      ▼                                │  │
│  │  ┌─ Interceptor 3 ─────────────────┐  │  │
│  │  │  (before next)                  │  │  │
│  │  │      │                          │  │  │
│  │  │      ▼                          │  │  │
│  │  │    _name = "John"  ← field set  │  │  │
│  │  │      │                          │  │  │
│  │  │      ▼                          │  │  │
│  │  │  (after next)                   │  │  │
│  │  └────────────────────────────────-┘  │  │
│  │      │                                │  │
│  │      ▼                                │  │
│  │  (after next)  fire change event      │  │
│  └───────────────────────────────────────┘  │
│      │                                      │
│      ▼                                      │
│  (after next)  notify observers             │
└─────────────────────────────────────────────┘
```

**Read Pipeline** (`IReadInterceptor`):
```
var name = person.Name
    │
    ▼
┌─ Interceptor 1 ─────────────────────────────┐
│  (before next)  record access, etc.         │
│      │                                      │
│      ▼                                      │
│  ┌─ Interceptor 2 ───────────────────────┐  │
│  │  (before next)                        │  │
│  │      │                                │  │
│  │      ▼                                │  │
│  │    return _name  ← field read         │  │
│  │      │                                │  │
│  │      ▼                                │  │
│  │  (after next)  transform value        │  │
│  └───────────────────────────────────────┘  │
│      │                                      │
│      ▼                                      │
│  (after next)                               │
└─────────────────────────────────────────────┘
    │
    ▼
  "John"
```

See [Interceptors documentation](docs/interceptor.md) for details.

## Requirements

- **.NET 9.0** or later (extensions and integrations)
- **.NET Standard 2.0** (core library only)
- **C# 13** with partial properties support
- IDE with source generator support (Visual Studio 2022, Rider, VS Code with C# extension)

## Samples

### Basic usage

Define a interceptable class:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";

    public Person()
    {
        FirstName = string.Empty;
        LastName = string.Empty;
    }
}
```

Create a context and start tracking:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking();

var person = new Person(context);
```

### Change Tracking

Subscribe to property changes across your object graph:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking();

context
    .GetPropertyChangeObservable()
    .Subscribe(change =>
    {
        Console.WriteLine(
            $"Property '{change.Property.Name}' changed " +
            $"from '{change.OldValue}' to '{change.NewValue}'.");
    });

var person = new Person(context)
{
    FirstName = "John",
    // Property 'FirstName' changed from '' to 'John'.
    // Property 'FullName' changed from ' ' to 'John '.

    LastName = "Doe"
    // Property 'LastName' changed from '' to 'Doe'.
    // Property 'FullName' changed from 'John ' to 'John Doe'.
};
```

### Subject Lifecycle Tracking

Track when objects enter or leave your object graph:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string Name { get; set; }
    public partial Person[] Children { get; set; }

    public Person()
    {
        Name = "n/a";
        Children = [];
    }
}

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithService(() => new LogLifecycleHandler());

var person = new Person(context);
// Attached: Person

person.Children = [new Person { Name = "Child1" }, new Person { Name = "Child2" }];
// Attached: Child1
// Attached: Child2

person.Children = [];
// Detached: Child1
// Detached: Child2
```

### Bidirectional Synchronization with External Systems

The core tracking capabilities enable powerful integrations. Here's an example with MQTT:

```csharp
[InterceptorSubject]
public partial class Sensor
{
    [Path("mqtt", "temperature")]
    public partial decimal Temperature { get; set; }

    [Path("mqtt", "humidity")]
    public partial decimal Humidity { get; set; }

    public Sensor()
    {
        Temperature = 0;
        Humidity = 0;
    }
}
```

Configure the MQTT client in your application:

```csharp
builder.Services.AddMqttSubjectClient<Sensor>(
    brokerHost: "mqtt.example.com",
    sourceName: "mqtt",
    brokerPort: 1883,
    topicPrefix: "sensors/room1");
```

Now property changes synchronize automatically:

```csharp
var sensor = serviceProvider.GetRequiredService<Sensor>();

// Changes publish to MQTT broker
sensor.Temperature = 25.5m;

// External MQTT messages update the property
// (and trigger change notifications)
```

Similar patterns work with OPC UA, databases, and other external systems. See the [MQTT](docs/connectors/mqtt.md), [OPC UA](docs/connectors/opcua.md), and [Connectors](docs/connectors.md) documentation for details.

## Extensibility

Namotion.Interceptor is designed to be extended:

| Extension Point | What it enables | Documentation |
|-----------------|-----------------|---------------|
| **Read/Write Interceptors** | Add cross-cutting concerns like logging, caching, or transformation to property access | [Interceptors](docs/interceptor.md) |
| **Lifecycle Handlers** | React to objects entering or leaving the object graph (cleanup, initialization) | [Tracking](docs/tracking.md) |
| **Custom Connectors** | Synchronize with any external system (databases, message queues, APIs) | [Connectors](docs/connectors.md) |
| **Property Validation** | Implement custom validation logic beyond data annotations | [Validation](docs/interceptor.md) |
| **Dynamic Subjects** | Create trackable objects from interfaces at runtime without source generation | [Dynamic](docs/dynamic.md) |

## Packages

### Core

| Package | Description | Docs |
|---------|-------------|------|
| **Namotion.Interceptor** | Property interception with compile-time source generation | [Interceptors](docs/interceptor.md) |
| **Namotion.Interceptor.Generator** | Source generator (included automatically) | |

### Foundation

| Package | Description | Docs |
|---------|-------------|------|
| **Namotion.Interceptor.Tracking** | Change tracking, derived properties, lifecycle events, transactions | [Tracking](docs/tracking.md) |
| **Namotion.Interceptor.Registry** | Runtime property discovery, metadata, and dynamic properties | [Registry](docs/registry.md) |
| **Namotion.Interceptor.Validation** | Property validation with data annotation support | |
| **Namotion.Interceptor.Dynamic** | Create subjects from interfaces at runtime | [Dynamic](docs/dynamic.md) |
| **Namotion.Interceptor.Hosting** | Hosted service lifecycle management | |

### Connectors

| Package | Description | Docs |
|---------|-------------|------|
| **Namotion.Interceptor.Connectors** | Base infrastructure for external system integration | [Connectors](docs/connectors.md) |
| **Namotion.Interceptor.Mqtt** | Bidirectional MQTT synchronization | [MQTT](docs/connectors/mqtt.md) |
| **Namotion.Interceptor.OpcUa** | OPC UA client and server integration | [OPC UA](docs/connectors/opcua.md) |

### Integrations

| Package | Description | Docs |
|---------|-------------|------|
| **Namotion.Interceptor.AspNetCore** | ASP.NET Core integration for web APIs | |
| **Namotion.Interceptor.Blazor** | Automatic re-rendering on property changes | [Blazor](docs/blazor.md) |
| **Namotion.Interceptor.GraphQL** | GraphQL subscriptions for real-time updates | |

## Documentation

- [Subject Design Guidelines](docs/subject-guidelines.md) - Property patterns and best practices
- [Interceptors and Contexts](docs/interceptor.md) - Core interception pipeline
- [Tracking](docs/tracking.md) - Change tracking, transactions, lifecycle
- [Registry](docs/registry.md) - Runtime property discovery and metadata
- [Connectors](docs/connectors.md) - External system integration

## Samples

For more examples, see the `Samples` directory in the solution.
