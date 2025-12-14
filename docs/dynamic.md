# Dynamic

The `Namotion.Interceptor.Dynamic` package enables creating interceptor subjects from interfaces at runtime without requiring compile-time code generation. This is particularly useful for scenarios where you need to create trackable objects from external interfaces, plugin architectures, or when working with dynamically loaded assemblies.

Unlike the main interceptor approach that uses source generation with `[InterceptorSubject]` classes, the Namotion.Interceptor.Dynamic package uses Castle DynamicProxy to create runtime implementations of interfaces that fully participate in the interceptor ecosystem.

## Setup

The Dynamic package works with existing interceptor contexts without additional setup:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithRegistry()
    .WithFullPropertyTracking();

// Create dynamic subjects from interfaces
var subject = DynamicSubjectFactory.CreateDynamicSubject(context, typeof(IMotor), typeof(ISensor));
```

## Creating dynamic subjects from interfaces

Define your interfaces with the properties you need:

```csharp
public interface IMotor
{
    int Speed { get; set; }
}

public interface ISensor
{
    int Temperature { get; set; }
}
```

Create a dynamic subject that implements multiple interfaces:

```csharp
// Create a subject that implements both interfaces
var subject = DynamicSubjectFactory.CreateDynamicSubject(context, typeof(IMotor), typeof(ISensor));

// Cast to specific interfaces and use normally
var motor = (IMotor)subject;
var sensor = (ISensor)subject;

motor.Speed = 100;
sensor.Temperature = 25;

Console.WriteLine($"Speed: {motor.Speed}"); // Output: Speed: 100
Console.WriteLine($"Temperature: {sensor.Temperature}"); // Output: Temperature: 25
```

## Registry integration

Dynamic subjects automatically register their properties with the registry system:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithRegistry();

var subject = DynamicSubjectFactory.CreateDynamicSubject(context, typeof(IMotor), typeof(ISensor));

// Access registry information
var registeredSubject = subject.TryGetRegisteredSubject()!;
var properties = registeredSubject.Properties;

// Properties from all interfaces are available
Assert.Contains(properties, p => p.Name == "Speed");
Assert.Contains(properties, p => p.Name == "Temperature");
```

This enables the same registry features as compile-time subjects, including property metadata, attributes, and dynamic property addition.

## Full interceptor support

Dynamic subjects participate fully in the interceptor pipeline, supporting all interceptor types:

```csharp
var logs = new List<string>();

var context = InterceptorSubjectContext
    .Create()
    .WithService(_ => new TestInterceptor("logger", logs), _ => false);

var subject = DynamicSubjectFactory.CreateDynamicSubject(context, typeof(IMotor));
var motor = (IMotor)subject;

motor.Speed = 100; // Triggers write interceptors
var speed = motor.Speed; // Triggers read interceptors
```

## Property initialization and defaults

Dynamic subjects automatically handle property initialization:

- **Value types** get their default values (0, false, etc.)
- **Reference types** get null as initial values
- **Properties are lazy-initialized** when first accessed

```csharp
public interface IConfiguration
{
    string Name { get; set; }
    int Port { get; set; }
    bool IsEnabled { get; set; }
}

var config = (IConfiguration)DynamicSubjectFactory.CreateDynamicSubject(null, typeof(IConfiguration));

// Default values are automatically provided
Console.WriteLine(config.Name);      // Output: null
Console.WriteLine(config.Port);      // Output: 0  
Console.WriteLine(config.IsEnabled); // Output: false
```

## Performance considerations

Dynamic subjects use Castle DynamicProxy for runtime implementation:

- **Property access** has additional overhead compared to compile-time subjects
- **Property metadata** is cached and reused across instances
- **Interface discovery** happens once during subject creation
- **Memory usage** is higher due to proxy generation and property value boxing

For performance-critical scenarios, prefer compile-time subjects with `[InterceptorSubject]` when possible.
