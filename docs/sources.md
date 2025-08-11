# Sources

The Sources package enables binding subject properties to external data sources like MQTT, OPC UA, or custom providers. It provides a powerful abstraction layer that automatically synchronizes property values with external systems while maintaining full type safety and change tracking.

## Setup

Enable sources in your interceptor context:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithRegistry()
    .WithSources();
```

The Sources package builds on the Registry to discover properties and their source path mappings.

## Source path attributes

Use `[SourcePath]` attributes to map properties to external data sources:

```csharp
[InterceptorSubject]
public partial class Sensor
{
    [SourcePath("mqtt", "temperature")]
    [SourcePath("opc", "Temperature")]
    public partial decimal Temperature { get; set; }

    [SourcePath("mqtt", "humidity")]
    [SourcePath("opc", "Humidity")]
    public partial decimal Humidity { get; set; }

    [Derived]
    [SourcePath("mqtt", "status")]
    public string Status => Temperature > 25 ? "Hot" : "Normal";
}
```

Multiple `[SourcePath]` attributes allow the same property to be synchronized with different external systems using their respective naming conventions.

## Built-in source implementations

### MQTT integration

Bind subjects to MQTT topics for IoT scenarios:

```csharp
builder.Services.AddMqttSubjectServer<Sensor>("mqtt");
// or
builder.Services.AddMqttSubjectClient("mqtt://localhost:1883", "mqtt");
```

Properties with `[SourcePath("mqtt", "topic")]` are automatically synchronized with MQTT messages.

### OPC UA integration

Connect to industrial automation systems:

```csharp
builder.Services.AddOpcUaSubjectServer<Sensor>("opc", rootName: "Root");
// or  
builder.Services.AddOpcUaSubjectClient<Sensor>("opc.tcp://localhost:4840", "opc", rootName: "Root");
```

Properties with `[SourcePath("opc", "NodeName")]` map to OPC UA nodes in the address space.

### GraphQL integration

Enable real-time subscriptions for web applications:

```csharp
builder.Services
    .AddGraphQLServer()
    .AddInMemorySubscriptions()
    .AddSubjectGraphQL<Sensor>();
```

Properties become queryable and subscribable through GraphQL, with automatic change notifications.

## Custom source implementations

Implement `ISubjectSource` to create custom data source integrations:

```csharp
public class DatabaseSource : ISubjectSource
{
    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return property.ReflectionAttributes
            .OfType<SourcePathAttribute>()
            .Any(a => a.SourceName == "database");
    }

    public async Task<IDisposable?> StartListeningAsync(
        ISubjectMutationDispatcher dispatcher, 
        CancellationToken cancellationToken)
    {
        // Set up database change notifications
        return subscription;
    }

    public async Task<Action<IInterceptorSubject>> LoadInitialStateAsync(
        CancellationToken cancellationToken)
    {
        // Load initial values from database
        return subject => { /* apply loaded state */ };
    }
}
```

Register your custom source:

```csharp
builder.Services.AddSingleton<ISubjectSource, DatabaseSource>();
```

## Source path providers

Source path providers determine how property paths are resolved for different source types:

Built-in providers include:

- **DefaultSourcePathProvider** - Uses paths exactly as specified in attributes
- **JsonCamelCaseSourcePathProvider** - Converts property names to camelCase for JSON APIs
