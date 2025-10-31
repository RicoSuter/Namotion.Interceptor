[![Nuget](https://img.shields.io/nuget/v/Namotion.Interceptor.svg)](https://apimundo.com/organizations/nuget-org/nuget-feeds/public/packages/Namotion.Interceptor/versions/latest)

**The library is currently in development and the APIs might change.**

# Namotion.Interceptor for .NET

Namotion.Interceptor is a .NET library designed to simplify the creation of trackable object models by automatically generating property interceptors (requires [C# 13 partial properties](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-13#more-partial-members)). All you need to do is annotate your model classes with a few simple attributes; they remain regular POCOs otherwise. The library uses source generation to handle the interception logic for you.

In addition to property tracking, Namotion.Interceptor offers advanced features such as automatic change detection (including derived properties), reactive source mapping (e.g., for GraphQL subscriptions or MQTT publishing), and other powerful capabilities that integrate seamlessly into your workflow.

![Feature Map](./features.png)

## Change tracking sample

First, define a proxied class:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";
}
```

Then create a context and start tracking changes:

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

person.FirstName = "Jane";
// Property 'FirstName' changed from 'John' to 'Jane'.
// Property 'FullName' changed from 'John Doe' to 'Jane Doe'.
```

## Subject attach and detach tracking sample

Implement a class with properties that reference other proxied objects:

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

    public override string ToString() => "Person: " + Name;
}
```

The context automatically tracks attachment and detachment of referenced proxies:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithService(() => new LogPropertyChangesHandler())
    .WithFullPropertyTracking(); // tracks property changes and subject attaches/detaches

var child1 = new Person { Name = "Child1" };
var child2 = new Person { Name = "Child2" };
var child3 = new Person { Name = "Child3" };

var person = new Person(context);
// Attach: Person: n/a

person.Children = [child1, child2];
// Attach: Person: Child1
// Attach: Person: Child2

person.Children = [child1, child2, child3];
// Attach: Person: Child3

person.Children = [];
// Detach: Person: Child1
// Detach: Person: Child2
// Detach: Person: Child3

public class LogPropertyChangesHandler : ILifecycleHandler
{
    public void Attach(LifecycleContext context) => 
        Console.WriteLine($"Attach: {context.Subject}");

    public void Detach(LifecycleContext context) => 
        Console.WriteLine($"Detach: {context.Subject}");
}
```

## More samples

For more samples, check out the "Samples" directory in the Visual Studio solution.

## Projects

### Namotion.Interceptor

Core library for property and method interception with source generation support. Provides the fundamental building blocks for creating intercepted objects.

**Key Components:**

- **`IInterceptorSubject`** - The fundamental interface that every intercepted object implements. Represents an observable object that participates in the interception system with context, data storage, and property metadata.

- **`IInterceptorSubjectContext`** - The central coordination hub that manages all interception behavior. Acts as a service container and orchestrates the interception pipeline with support for context hierarchies.

- **`IInterceptorExecutor`** - High-level execution engine that subjects use to perform intercepted property and method operations. Extends the context with actual execution capabilities.

- **`IReadInterceptor / IWriteInterceptor`** - Core interception interfaces that define middleware components for observing, modifying, or blocking property operations using a chain-of-responsibility pattern.

- **`SubjectPropertyMetadata`** - Property descriptor containing type information, access methods, and interception flags that tells the system how to work with each property.

### Namotion.Interceptor.Generator

Source generator that creates the interception logic for classes marked with `[InterceptorSubject]`. Automatically included when you install the main `Namotion.Interceptor` package.

**What it generates:**

The generator creates partial property implementations that route through the interception pipeline:

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
    public IInterceptorSubjectContext Context { get; }
    
    public partial string Name 
    { 
        get => (string)((IInterceptorExecutor)Context).GetPropertyValue("Name", () => _name);
        set => ((IInterceptorExecutor)Context).SetPropertyValue("Name", value, () => _name, v => _name = v);
    }
    
    // Constructor injection, metadata registration, etc.
}
```

**Key Features:**

- **Zero Runtime Reflection** - All interception logic is generated at compile time
- **Preserves IntelliSense** - Full IDE support for your partial properties
- **Metadata Generation** - Creates property metadata for the registry system
- **Constructor Injection** - Generates constructors that accept `IInterceptorSubjectContext`

**Build Integration:**

The generator runs during compilation and integrates with MSBuild, supporting incremental builds and design-time builds for optimal IDE experience.

### [Namotion.Interceptor.Dynamic](docs/dynamic.md)

Creates interceptor subjects from interfaces at runtime without requiring compile-time code generation. This enables dynamic object creation for plugin architectures, external interfaces, and scenarios where runtime flexibility is needed.

### Namotion.Interceptor.Hosting

Automatically start and stop subjects which implement `IHostedService` based on object graph attachment and detachment, with support for attaching and detaching hosted services to subjects.

Attach a hosted service to a subject:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithHostedServices(builder.Services);

var person = new Person(context);
var hostedService = new PersonBackgroundService(person);
person.AttachHostedService(hostedService);
```

**Methods:**

- `WithHostedServices()` → `Tracking.WithLifecycle()`

### [Namotion.Interceptor.Registry](docs/registry.md)

Tracks subjects and their properties with support for dynamic properties and property attributes. Enables runtime discovery of metadata and object graph navigation.

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithRegistry();

var registered = subject.TryGetRegisteredSubject();
```

**Methods:**

- `WithRegistry()` → `Tracking.WithContextInheritance()`

### [Namotion.Interceptor.Sources](docs/sources.md)

Enables binding subject properties to external data sources like MQTT, OPC UA, or custom providers.

### Namotion.Interceptor.Tracking

Provides comprehensive change tracking for subjects, including property changes, derived properties, and subject lifecycle events.

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking();

context.GetPropertyChangeObservable().Subscribe(change => {
    Console.WriteLine($"{change.Property.Name}: {change.OldValue} → {change.NewValue}");
});
```

**Methods:**

- `WithLifecycle()` - Subject attach and detach callbacks
- `WithFullPropertyTracking()` → `WithEqualityCheck()`, `WithContextInheritance()`, `WithDerivedPropertyChangeDetection()`, `WithPropertyChangeObservable()`
- `WithPropertyChangeObservable()` - Observable property change notifications
- `WithDerivedPropertyChangeDetection()` → `WithLifecycle()` - Automatic derived property updates
- `WithContextInheritance()` → `WithLifecycle()` - Child subjects inherit parent context
- `WithEqualityCheck()` - Only trigger changes when values actually change
- `WithParents()` - Track parent-child relationships
- `WithReadPropertyRecorder()` - Record property read operations

### Namotion.Interceptor.Validation

Validates subjects and properties using custom validation logic or data annotations.

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithDataAnnotationValidation();
```

**Methods:**

- `WithPropertyValidation()` - Custom property validation
- `WithDataAnnotationValidation()` → `WithPropertyValidation()` - Automatic data annotation validation

### Integration Packages

**Namotion.Interceptor.AspNetCore** - ASP.NET Core integration for exposing subjects as web APIs

**Namotion.Interceptor.Blazor** - Blazor components for data binding with interceptor subjects

**Namotion.Interceptor.GraphQL** - GraphQL integration with subscription support for real-time updates

**Namotion.Interceptor.Mqtt** - MQTT client/server integration for IoT scenarios

**Namotion.Interceptor.OpcUa** - OPC UA client/server integration for industrial automation
