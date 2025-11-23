# Sources

The `Namotion.Interceptor.Sources` package enables binding subject properties to external data sources like MQTT, OPC UA, or custom providers. It provides a powerful abstraction layer that automatically synchronizes property values with external systems while maintaining full type safety and change tracking.

## Client vs Server Sources

The library distinguishes between two source types based on data ownership:

**Cardinality rules:**
- Zero or one client source per property (single source of truth)
- Zero or many server sources per property (multiple publish channels)

### Client Sources (`ISubjectSource`)

The C# object is a **replica** of external data (local is a CLIENT of an external authoritative system):

- Loads initial state FROM the external source (not authoritative)
- Writes TO an external system that may be unavailable
- Write failures are expected (network issues, server down)
- Provides write retry queue to buffer changes during disconnection

Examples: OPC UA client connecting to a PLC, database client, REST API consumer

### Server Sources (`ISubjectSource`)

The C# object IS the **source of truth** (local is an authoritative SERVER):

- No initial state to load (it defines the state)
- "Writes" are publishing/notifying subscribers
- Subscribers that miss updates must reconnect
- No retry queue needed - server continues regardless of client availability

Examples: OPC UA server exposing sensor data, MQTT broker publishing values

### Why This Distinction?

Failure handling is inherently asymmetric in client-server relationships. A client must gracefully handle server unavailability by buffering writes for retry. A server doesn't block on client availability - it publishes data and clients subscribe or reconnect as needed.

This design maps to common patterns: primary/replica replication, publisher/subscriber messaging, and request/response protocols.

## Inbound vs Outbound Data Flow

The library uses consistent terminology to distinguish data flow directions:

| Direction | Term | Type | Description |
|-----------|------|------|-------------|
| **Inbound** | Update | `SubjectPropertyWriter.Write()` | Data FROM external system TO subject |
| **Outbound** | Change | `SubjectPropertyChange` | Data FROM subject TO external system |

- **Inbound updates**: External system notifies connector → connector calls `propertyWriter.Write()` → subject property updated
- **Outbound changes**: Subject property changed → `WriteChangesAsync()` called → connector writes to external system

## Buffer-Load-Replay Pattern (Client Sources)

Client connectors use a buffer-load-replay pattern during initialization to ensure zero data loss:

1. **Buffer**: During `StartListeningAsync()`, inbound updates are buffered (not applied immediately)
2. **Load**: `LoadInitialStateAsync()` fetches complete state from external system
3. **Replay**: Buffered updates are replayed in order after initial state is applied

This pattern ensures that updates received during initialization are not lost and are applied in the correct order relative to the initial state.

**Note**: This pattern only applies to client sources (`ISubjectSource`). Server connectors don't implement `LoadInitialStateAsync()` since they are the source of truth and don't need to load external state.

### Inbound Update Error Handling

When applying inbound updates (writing data from the external system to the local subject model), if an individual update fails (the action throws an exception), the error is logged and **the update is dropped**. There is no retry mechanism for inbound updates.

This is by design:
- The external source is expected to send the value again if synchronization is needed
- Individual update failures don't block other updates from being applied
- Monitor logs for `Failed to apply subject update` errors to detect issues

This differs from outbound changes (writing from local model to external system), which use a retry queue to handle transient failures.

## Setup

Sources are enabled through their specific extension methods. Each source type (MQTT, OPC UA, etc.) provides its own registration:

```csharp
// MQTT
builder.Services.AddMqttServer<Sensor>("mqtt");
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

## Built-in source implementations

### MQTT integration

Bind subjects to MQTT topics for IoT scenarios:

```csharp
builder.Services.AddMqttServer<Sensor>("mqtt");
```

Properties with `[SourcePath("mqtt", "topic")]` are automatically synchronized with MQTT messages.

### OPC UA integration

Connect to industrial automation systems:

```csharp
builder.Services.AddOpcUaServer<Sensor>("opc", rootName: "Root");
// or
builder.Services.AddOpcUaClientSource<Sensor>("opc.tcp://localhost:4840", "opc", rootName: "Root");
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
public class SampleConnector : ISubjectSource
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
        SubjectPropertyWriter propertyWriter,
        CancellationToken cancellationToken)
    {
        // Set up external change notifications
        // Use propertyWriter.Write() to apply inbound updates
        return subscription;
    }

    public async Task<Action?> LoadInitialStateAsync(
        CancellationToken cancellationToken)
    {
        // Load initial values from external system
        var externalState = ...;
        return () => { /* Apply externalState to _root */ };
    }

    public ValueTask WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        // Write changes to external system
        // Throw exception for failures - the entire batch will be retried
        return ValueTask.CompletedTask;
    }
}
```

Register your custom source:

```csharp
builder.Services.AddSingleton<ISubjectSource, DatabaseConnector>();
```

## Source path providers

Source path providers determine how property paths are resolved for different source types:

Built-in providers include:

- **DefaultSourcePathProvider** - Uses paths exactly as specified in attributes
- **JsonCamelCaseSourcePathProvider** - Converts property names to camelCase for JSON APIs

## Thread Safety with Concurrent Connectors

When multiple sources update properties concurrently, the library provides automatic thread-safety at the property field access level. Individual property updates are atomic and thread-safe without requiring additional synchronization in your source implementation.

**Source responsibility**: While the library ensures thread-safe property access, **connectors are responsible for maintaining correct update ordering** according to their protocol semantics. When implementing `ISubjectSource`, use the provided `SubjectPropertyWriter` to write inbound updates, which handles buffering during initialization and prevents race conditions where newer values could be overwritten by delayed older updates.

Custom source implementations should ensure that the temporal ordering of external events is preserved when applying property updates. This is critical for maintaining data consistency when events arrive out of order or concurrently from the same connector.

## Write Retry Queue

The `SubjectSourceBackgroundService` provides an optional write retry queue that buffers writes during disconnection and automatically flushes them when the connection is restored. This feature is available to client source implementations.

```csharp
// Configure write retry queue size when setting up a client source
services.AddHostedService(sp =>
{
    var connector = sp.GetRequiredService<ISubjectSource>();
    var context = sp.GetRequiredService<IInterceptorSubjectContext>();
    var logger = sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>();

    return new SubjectSourceBackgroundService(
        connector,
        context,
        logger,
        bufferTime: TimeSpan.FromMilliseconds(8),
        retryTime: TimeSpan.FromSeconds(10),
        writeRetryQueueSize: 1000); // Enable write retry queue
});
```

**Behavior:**
- Ring buffer semantics: oldest writes dropped when capacity reached (configurable via `writeRetryQueueSize` to balance memory usage vs buffering capacity)
- FIFO flush order when connection restored
- Automatic retry when `WriteToSourceAsync` throws an exception
- In-memory only: queued writes are lost on process restart (by design for simplicity; implement persistent queue wrapper if needed for critical data)
- No dead letter queue: permanent write failures are logged but not preserved (implement custom error handling via source wrapper if needed for auditing)

