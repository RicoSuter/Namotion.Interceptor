# Connectors

The `Namotion.Interceptor.Connectors` package provides infrastructure for connecting subject graphs to external systems. It includes **sources** (for synchronizing FROM external systems) and shared components used by both sources and servers.

## Architecture Overview

### Layers

1. **Your Application** - Contains `IInterceptorSubject` instances (your domain model/replica)
2. **Connectors** - Bridge between your subjects and external systems

### Connector Types

| Type | Interface | Direction | Description |
|------|-----------|-----------|-------------|
| **Source** | `ISubjectSource` | Bidirectional | Synchronizes FROM an external system (the external system is the source of truth) |
| **Server** | `ISubjectConnector` | Outbound | Exposes subjects TO external systems (the C# object is the source of truth) |

### ISubjectSource Operations

| Method | Direction | Description |
|--------|-----------|-------------|
| `LoadInitialStateAsync()` | External → Subject | Fetches complete state from external system |
| `StartListeningAsync()` | External → Subject | Subscribes to real-time updates |
| `WriteChangesAsync()` | Subject → External | Sends local changes to external system |

### Server Operations

| Operation | Direction | Description |
|-----------|-----------|-------------|
| Publish changes | Subject → External | Broadcasts property updates to connected clients |
| Receive commands | External → Subject | Handles write requests from external clients |

## ISubjectConnector

Minimal marker interface for components that connect subjects to external systems:

```csharp
public interface ISubjectConnector
{
    /// <summary>
    /// The root subject being connected to an external system.
    /// </summary>
    IInterceptorSubject RootSubject { get; }
}
```

This interface is:
- **Required** for sources (`ISubjectSource : ISubjectConnector`)
- **Optional** for servers (they can implement it for type consistency)

> **Note**: Path providers are implementation details. Sources expose `IsPropertyIncluded()` which internally delegates to their path provider. A source/server might not use a path provider at all.

## Sources (ISubjectSource)

A **source** represents an external authoritative system where the data originates. The C# object is a **replica** that synchronizes with this external source of truth.

### Key Characteristics

- Loads initial state FROM the external system
- Writes changes TO the external system (which may fail)
- Provides write retry queue to buffer changes during disconnection
- Uses `SubjectSourceBackgroundService` for lifecycle management

**Cardinality**: Each property can have at most one source (single source of truth)

**Examples**: OPC UA client connecting to a PLC, database client, REST API consumer

### ISubjectSource Interface

```csharp
public interface ISubjectSource : ISubjectConnector
{
    /// <summary>
    /// Checks whether the property is handled by this source.
    /// Implementations typically delegate to an internal IPathProvider.
    /// </summary>
    bool IsPropertyIncluded(RegisteredSubjectProperty property);

    /// <summary>
    /// Maximum batch size for write operations (0 = no limit).
    /// </summary>
    int WriteBatchSize { get; }

    /// <summary>
    /// Start listening for external changes.
    /// </summary>
    Task<IDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Write property changes back to the external system.
    /// </summary>
    ValueTask WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Load initial state from the external system.
    /// </summary>
    Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);
}
```

### Data Flow

#### Inbound (External → Subject)

When the external system sends new values:

1. External System sends update
2. Source receives the update
3. Source calls `propertyWriter.Write()`
4. Subject property is updated

#### Outbound (Subject → External)

When you change a property value in code:

1. Subject property is changed
2. `WriteChangesAsync()` is called with the change
3. Source sends update to external system

### Initialization Sequence

Sources use a buffer-flush-load-replay pattern during initialization and reconnection:

1. **Buffer**: During `StartListeningAsync()`, inbound updates are buffered
2. **Flush**: Pending writes from the retry queue are flushed to the external system
3. **Load**: `LoadInitialStateAsync()` fetches complete state from external system
4. **Replay**: Buffered updates are replayed in order after initial state is applied

This ensures:
- Updates received during initialization are not lost
- Updates are applied in the correct order relative to the initial state
- No visible state toggle (flush before load avoids showing stale server values)

### Inbound Update Error Handling

When applying inbound updates (writing data from the external system to the local subject model), if an individual update fails (the action throws an exception), the error is logged and **the update is dropped**. There is no retry mechanism for inbound updates.

This is by design:
- Individual update failures don't block other updates from being applied
- Monitor logs for `Failed to apply subject update` errors to detect issues

This differs from outbound changes (writing from local model to external system), which use a retry queue to handle transient failures.

## Servers

Servers expose subject properties to external systems. Unlike sources, they don't synchronize FROM an external system - the C# object IS the source of truth.

**Examples**:
- `OpcUaSubjectServerBackgroundService` - Exposes subjects as OPC UA nodes
- `MqttSubjectServerBackgroundService` - Publishes subject changes to MQTT topics

Servers typically:
- Are `BackgroundService` implementations
- Optionally implement `ISubjectConnector` for type consistency
- Use the same `IPathProvider` infrastructure for path mapping

## Path Providers

Path providers map between subject property paths and external system paths. They are defined in `Namotion.Interceptor.Registry.Paths`.

### IPathProvider Interface

```csharp
public interface IPathProvider
{
    /// <summary>
    /// Should this property be included in paths?
    /// </summary>
    bool IsPropertyIncluded(RegisteredSubjectProperty property);

    /// <summary>
    /// Get the path segment for a property.
    /// Returns null if no explicit mapping exists.
    /// </summary>
    string? GetPropertySegment(RegisteredSubjectProperty property);

    /// <summary>
    /// Find a property by its path segment.
    /// </summary>
    RegisteredSubjectProperty? GetPropertyFromSegment(RegisteredSubject subject, string segment);
}
```

### Built-in Providers

- **DefaultPathProvider** - Uses property names exactly as defined
- **JsonCamelCasePathProvider** - Converts property names to camelCase for JSON APIs
- **AttributeBasedPathProvider** - Uses `[Path]` attributes for custom mapping

### [Path] Attribute

Use `[Path]` attributes to map properties to custom external paths:

```csharp
[InterceptorSubject]
public partial class Sensor
{
    [Path("temp")]
    public partial decimal Temperature { get; set; }

    [Path("hum")]
    public partial decimal Humidity { get; set; }
}
```

### [Children] Attribute

Marks a dictionary property as an implicit child container for path resolution:

```csharp
[InterceptorSubject]
public partial class Storage
{
    [Children]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }
}
```

With `[Children]`:
- Path `files/readme.md` resolves to `Children["files"].Children["readme.md"]`
- Direct properties take precedence over child keys
- Built into `PathProviderBase.GetPropertyFromSegment`

## Updates

The `Updates/` folder contains serialization infrastructure for subject state:

- **SubjectUpdate** - Serializable representation of a subject's state
- **SubjectPropertyUpdate** - Serializable representation of a property change
- **ISubjectUpdateProcessor** - Filter/transform updates before serialization

These are used by both sources and servers (e.g., ASP.NET Core controllers, SignalR hubs).

## Setup

### Adding a Source

```csharp
// OPC UA Client Source
builder.Services.AddOpcUaSubjectClient<Sensor>("opc.tcp://localhost:4840", "opc", rootName: "Root");

// MQTT Client Source
builder.Services.AddMqttSubjectClient<Sensor>(config =>
{
    config.BrokerHost = "localhost";
    config.BrokerPort = 1883;
});
```

### Custom Source Implementation

```csharp
public class DatabaseSource : ISubjectSource
{
    private readonly IInterceptorSubject _root;
    private readonly IPathProvider _pathProvider;  // Internal - not on interface

    public DatabaseSource(IInterceptorSubject root, IPathProvider pathProvider)
    {
        _root = root;
        _pathProvider = pathProvider;
    }

    public IInterceptorSubject RootSubject => _root;
    public int WriteBatchSize => 100;

    // IsPropertyIncluded delegates to internal path provider
    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
        => _pathProvider.IsPropertyIncluded(property);

    public async Task<IDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter,
        CancellationToken cancellationToken)
    {
        // Set up change notifications from database
        // Use propertyWriter.Write() to apply inbound updates
        return subscription;
    }

    public async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var data = await LoadFromDatabaseAsync(cancellationToken);
        return () => ApplyToSubject(_root, data);
    }

    public async ValueTask WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        // Write changes to database
        // Throw exception for failures - entire batch will be retried
    }
}
```

Register your custom source:

```csharp
builder.Services.AddSingleton<ISubjectSource, DatabaseSource>();
```

## Write Retry Queue

The `SubjectSourceBackgroundService` provides an optional write retry queue that buffers writes during disconnection:

```csharp
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
- Ring buffer semantics: oldest writes dropped when capacity reached
- FIFO flush order when connection restored
- Automatic retry when `WriteChangesAsync` throws an exception
- In-memory only: queued writes are lost on process restart

## Thread Safety

Properties can receive concurrent writes from multiple origins:
- **Source**: Inbound updates from the external system
- **Servers**: Background services exposing the property
- **Local code**: Application services, UI handlers, etc.

Individual property updates are atomic and thread-safe without requiring additional synchronization.

When implementing `ISubjectSource`, use the provided `SubjectPropertyWriter` to write inbound updates. This handles buffering during initialization and ensures correct ordering.
