# Connectors

The `Namotion.Interceptor.Connectors` package enables binding subject properties to external data sources like MQTT, OPC UA, or custom providers. It provides a powerful abstraction layer that automatically synchronizes property values with external systems while maintaining full type safety and change tracking.

## Upstream vs Downstream Connectors

The library distinguishes between two connector types based on data ownership:

**Cardinality rules:**
- Zero or one upstream connector per property (single source of truth)
- Zero or many downstream connectors per property (multiple publish channels)

### Upstream Connectors (`ISubjectUpstreamConnector`)

The C# object is a **replica** of external data:

- Loads initial state FROM the external source (not authoritative)
- Writes TO an external system that may be unavailable
- Write failures are expected (network issues, server down)
- Provides write retry queue to buffer changes during disconnection

Examples: OPC UA client connecting to a PLC, database client, REST API consumer

### Downstream Connectors (`ISubjectDownstreamConnector`)

The C# object IS the **source of truth**:

- No initial state to load (it defines the state)
- "Writes" are publishing/notifying subscribers
- Subscribers that miss updates must reconnect
- No retry queue needed - server continues regardless of client availability

Examples: OPC UA server exposing sensor data, MQTT broker publishing values

### Why This Distinction?

Failure handling is inherently asymmetric in client-server relationships. A client must gracefully handle server unavailability by buffering writes for retry. A server doesn't block on client availability - it publishes data and clients subscribe or reconnect as needed.

This design maps to common patterns: master/replica replication, publisher/subscriber messaging, and request/response protocols.

## Setup

Connectors are enabled through their specific extension methods. Each connector type (MQTT, OPC UA, etc.) provides its own registration:

```csharp
// MQTT
builder.Services.AddMqttServerConnector<Sensor>("mqtt");
```

The Connectors package builds on the Registry to discover properties and their source path mappings.

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

## Built-in connector implementations

### MQTT integration

Bind subjects to MQTT topics for IoT scenarios:

```csharp
builder.Services.AddMqttServerConnector<Sensor>("mqtt");
```

Properties with `[SourcePath("mqtt", "topic")]` are automatically synchronized with MQTT messages.

### OPC UA integration

Connect to industrial automation systems:

```csharp
builder.Services.AddOpcUaServerConnector<Sensor>("opc", rootName: "Root");
// or
builder.Services.AddOpcUaClientConnector<Sensor>("opc.tcp://localhost:4840", "opc", rootName: "Root");
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

## Custom connector implementations

Implement `ISubjectConnector` to create custom data source integrations:

```csharp
public class SampleConnector : ISubjectConnector
{
    public SampleConnector(IInterceptorSubject root)
    {
        _root = root;
    }

    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
    {
        return property.ReflectionAttributes
            .OfType<SourcePathAttribute>()
            .Any(a => a.SourceName == "sample");
    }

    public async Task<IDisposable?> StartListeningAsync(
        ConnectorUpdateBuffer updateBuffer,
        CancellationToken cancellationToken)
    {
        // Set up source change notifications
        return subscription;
    }

    public async Task<Action?> LoadCompleteSourceStateAsync(
        CancellationToken cancellationToken)
    {
        // Load initial values from source
        var sourceState = ...;
        return () => { /* Apply sourceState to _root */ };
    }

    public ValueTask WriteToSourceAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        // Write changes back to source
        // Throw exception for failures - the entire batch will be retried
        return ValueTask.CompletedTask;
    }
}
```

Register your custom connector:

```csharp
builder.Services.AddSingleton<ISubjectConnector, DatabaseConnector>();
```

## Connector path providers

Connector path providers determine how property paths are resolved for different connector types:

Built-in providers include:

- **DefaultConnectorPathProvider** - Uses paths exactly as specified in attributes
- **JsonCamelCaseConnectorPathProvider** - Converts property names to camelCase for JSON APIs

## Thread Safety with Concurrent Connectors

When multiple connectors update properties concurrently, the library provides automatic thread-safety at the property field access level. Individual property updates are atomic and thread-safe without requiring additional synchronization in your connector implementation.

**Connector responsibility**: While the library ensures thread-safe property access, **connectors are responsible for maintaining correct update ordering** according to their protocol semantics. When implementing `ISubjectConnector`, use the provided `ConnectorUpdateBuffer` to apply updates, which handles buffering during initialization and prevents race conditions where newer values could be overwritten by delayed older updates.

Custom connector implementations should ensure that the temporal ordering of external events is preserved when applying property updates. This is critical for maintaining data consistency when events arrive out of order or concurrently from the same connector.

## Write Retry Queue

The `SubjectUpstreamConnectorBackgroundService` provides an optional write retry queue that buffers writes during disconnection and automatically flushes them when the connection is restored. This feature is available to upstream connector implementations.

```csharp
// Configure write retry queue size when setting up an upstream connector
services.AddHostedService(sp =>
{
    var connector = sp.GetRequiredService<ISubjectUpstreamConnector>();
    var context = sp.GetRequiredService<IInterceptorSubjectContext>();
    var logger = sp.GetRequiredService<ILogger<SubjectUpstreamConnectorBackgroundService>>();

    return new SubjectUpstreamConnectorBackgroundService(
        connector,
        context,
        logger,
        bufferTime: TimeSpan.FromMilliseconds(8),
        retryTime: TimeSpan.FromSeconds(10),
        writeRetryQueueSize: 1000); // Enable write retry queue
});
```

**Behavior:**
- Ring buffer semantics: oldest writes dropped when capacity reached
- FIFO flush order when connection restored
- Automatic retry when `WriteToSourceAsync` throws an exception

