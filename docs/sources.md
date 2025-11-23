# Sources

The `Namotion.Interceptor.Sources` package enables binding subject properties to external data sources. It provides a powerful abstraction layer that automatically synchronizes property values with external systems while maintaining full type safety and change tracking.

## What is a Source?

A **source** (`ISubjectSource`) represents an external authoritative system where the data originates. The C# object is a **replica** that synchronizes with this external source of truth.

Key characteristics:
- Loads initial state FROM the external system
- Writes changes TO the external system (which may fail)
- Provides write retry queue to buffer changes during disconnection
- Uses `SubjectSourceBackgroundService` for lifecycle management

**Cardinality**: Each property can have at most one source (single source of truth)

**Examples**: OPC UA client connecting to a PLC, database client, REST API consumer

> **Note**: When the C# object IS the source of truth and you want to expose it to external systems (like an OPC UA server or MQTT broker), use standalone background services like `OpcUaServerBackgroundService` or `MqttServerBackgroundService`. These are not sources because they don't synchronize from an external system.

## Data Flow

Data flows bidirectionally between your C# subject and the external system:

### Inbound (External → Subject)

When the external system sends new values, the source receives them and applies them to the subject using `SubjectPropertyWriter.Write()`.

```
External System → Source → propertyWriter.Write() → Subject Property Updated
```

### Outbound (Subject → External)

When you change a property value in code, the source writes it back to the external system via `WriteChangesAsync()`.

```
Subject Property Changed → WriteChangesAsync() → Source → External System
```

## Buffer-Load-Replay Pattern

Sources use a buffer-load-replay pattern during initialization to ensure zero data loss:

1. **Buffer**: During `StartListeningAsync()`, inbound updates are buffered (not applied immediately)
2. **Load**: `LoadInitialStateAsync()` fetches complete state from external system
3. **Replay**: Buffered updates are replayed in order after initial state is applied

This pattern ensures that updates received during initialization are not lost and are applied in the correct order relative to the initial state.

### Inbound Update Error Handling

When applying inbound updates (writing data from the external system to the local subject model), if an individual update fails (the action throws an exception), the error is logged and **the update is dropped**. There is no retry mechanism for inbound updates.

This is by design:
- Individual update failures don't block other updates from being applied
- Monitor logs for `Failed to apply subject update` errors to detect issues

This differs from outbound changes (writing from local model to external system), which use a retry queue to handle transient failures.

## Setup

Sources are enabled through their specific extension methods:

```csharp
// OPC UA Client Source
builder.Services.AddOpcUaClientSource<Sensor>("opc.tcp://localhost:4840", "opc", rootName: "Root");
```

The Sources package builds on the Registry to discover properties and their source path mappings.

## Source path attributes

Use `[SourcePath]` attributes to map properties to external data sources:

```csharp
[InterceptorSubject]
public partial class Sensor
{
    [SourcePath("opc", "Temperature")]
    public partial decimal Temperature { get; set; }

    [SourcePath("opc", "Humidity")]
    public partial decimal Humidity { get; set; }
}
```

## Built-in source implementations

### OPC UA Client

Connect to industrial automation systems as a client:

```csharp
builder.Services.AddOpcUaClientSource<Sensor>("opc.tcp://localhost:4840", "opc", rootName: "Root");
```

Properties with `[SourcePath("opc", "NodeName")]` map to OPC UA nodes in the external server's address space.

## Custom source implementations

Implement `ISubjectSource` to create custom data source integrations:

```csharp
public class SampleSource : ISubjectSource
{
    public SampleSource(IInterceptorSubject root)
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
builder.Services.AddSingleton<ISubjectSource, DatabaseSource>();
```

## Source path providers

Source path providers determine how property paths are resolved for different source types:

Built-in providers include:

- **DefaultSourcePathProvider** - Uses paths exactly as specified in attributes
- **JsonCamelCaseSourcePathProvider** - Converts property names to camelCase for JSON APIs

## Thread Safety with Concurrent Sources

When multiple sources update properties concurrently, the library provides automatic thread-safety at the property field access level. Individual property updates are atomic and thread-safe without requiring additional synchronization in your source implementation.

**Source responsibility**: While the library ensures thread-safe property access, **sources are responsible for maintaining correct update ordering** according to their protocol semantics. When implementing `ISubjectSource`, use the provided `SubjectPropertyWriter` to write inbound updates, which handles buffering during initialization and prevents race conditions where newer values could be overwritten by delayed older updates.

Custom source implementations should ensure that the temporal ordering of external events is preserved when applying property updates. This is critical for maintaining data consistency when events arrive out of order or concurrently from the same source.

## Write Retry Queue

The `SubjectSourceBackgroundService` provides an optional write retry queue that buffers writes during disconnection and automatically flushes them when the connection is restored.

```csharp
// Configure write retry queue size when setting up a source
services.AddHostedService(sp =>
{
    var source = sp.GetRequiredService<ISubjectSource>();
    var context = sp.GetRequiredService<IInterceptorSubjectContext>();
    var logger = sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>();

    return new SubjectSourceBackgroundService(
        source,
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
- Automatic retry when `WriteChangesAsync` throws an exception
- In-memory only: queued writes are lost on process restart (by design for simplicity; implement persistent queue wrapper if needed for critical data)
- No dead letter queue: permanent write failures are logged but not preserved (implement custom error handling via source wrapper if needed for auditing)

