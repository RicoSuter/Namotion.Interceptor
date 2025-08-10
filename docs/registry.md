# Registry

The Registry package provides a powerful tracking system that automatically discovers and manages interceptor subjects and their properties. It enables advanced features like property attributes (metadata attached to properties), dynamic property discovery, and hierarchical subject relationships.

![Registry Domain](registry-domain.png)

## Core Concepts

The registry automatically tracks subjects and their references, building a comprehensive map of your object graph. This enables several powerful patterns:

**Property Attributes**: Special properties that attach metadata to other properties. Instead of storing metadata in separate dictionaries or configurations, you can define it directly as properties on your subjects.

**Dynamic Discovery**: The registry provides runtime access to all properties, their types, relationships, and metadata. This enables building dynamic UIs, APIs, or validation systems that adapt to your object model.

**Hierarchical Tracking**: The registry understands parent-child relationships between subjects, allowing you to navigate and query the entire object graph programmatically.

**Type Safety**: Unlike reflection-based approaches, the registry maintains full type safety while providing dynamic capabilities.

## Setup

Enable registry tracking in your interceptor context:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithRegistry(); // Enables automatic subject and property tracking

var sensor = new Sensor(context);
```

The registry integrates with other interceptor features and automatically includes context inheritance to ensure child subjects participate in the registry.

## Property Attributes Pattern

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

## Dynamic Property Creation

The registry allows you to dynamically add properties and attributes to registered subjects at runtime. This enables building flexible systems that can extend object models programmatically.

### Adding Properties

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

### Adding Derived Properties

Use `AddDerivedProperty` to create computed properties that automatically track dependencies:

```csharp
// Add a derived property that depends on other properties
registered.AddDerivedProperty("Status", typeof(string),
    getValue: s => s.IsActive ? "Running" : "Stopped",
    setValue: null);
```

Derived properties automatically participate in change tracking and will update when their dependencies change.

### Adding Property Attributes

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

### Adding Derived Attributes

Use `AddDerivedAttribute` to create computed metadata that updates automatically:

```csharp
// Add a derived attribute that computes the maximum based on current value
pressureProperty.AddDerivedAttribute("DynamicMax", typeof(decimal),
    getValue: s => ((decimal)pressureProperty.Reference.GetValue()) * 1.5m,
    setValue: null);
```

This pattern is useful for creating adaptive metadata that changes based on the current state of your properties.

## Working with the Registry

### Accessing Registry Data

Every subject provides access to its registry information:

```csharp
var tire = new Tire(context);
var registered = tire.TryGetRegisteredSubject();

// Discover all properties
foreach (var prop in registered.Properties)
    Console.WriteLine($"{prop.Name} ({prop.Type.Name})");
```

### Finding Property Attributes

The registry makes it easy to find metadata associated with properties:

```csharp
// Get all attributes for a specific property
var attributes = registered.GetPropertyAttributes("Pressure");
foreach (var attribute in attributes)
{
    Console.WriteLine($"{attribute.AttributeMetadata.AttributeName}: {attr.Reference.GetValue()}");
}
```

### Property-First Approach

You can also retrieve a property first, then access its attributes:

```csharp
var property = registered.Properties.First(p => p.Name == "Pressure");
var attributes = registered.GetPropertyAttributes(property.Name);
// Work with property and its attributes...
```

## Custom Property Initializers

Implement `ISubjectPropertyInitializer` to automatically add metadata when properties are decorated with specific attributes:

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

## Integration with Other Features

The registry serves as a foundation for many advanced scenarios:

**Dynamic UIs**: Build user interfaces that automatically adapt to your object model by discovering properties and their metadata at runtime.

**External System Integration**: Use property attributes to store source paths, validation rules, or other integration metadata that can be discovered and used by other interceptor packages.

**Validation**: Store validation metadata as property attributes that can be processed by validation systems.

**Documentation**: Attach display names, descriptions, and other documentation directly to properties as discoverable metadata.

## Thread Safety

The registry provides thread-safe access to all data and includes a synchronization mechanism for updates:

```csharp
var registry = context.GetRequiredService<ISubjectRegistry>();
registry.ExecuteSubjectUpdate(() => {
    // All updates here are synchronized
    tire.Pressure = 2.5m;
    tire.Pressure_Minimum = 2.0m;
});
```

This ensures consistent state when multiple threads are modifying subjects or when the registry needs to update its internal tracking structures.
