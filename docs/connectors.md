# Connectors

The `Namotion.Interceptor.Connectors` package provides infrastructure for bridging your subject graph to external systems, syncing property values in and out over protocols like OPC UA, MQTT, or WebSocket. Every connector falls into one of two categories, defined by **data ownership**:

| Type                   | Data Owner      | Typical Role                            | Base                                                 |
|------------------------|-----------------|-----------------------------------------|------------------------------------------------------|
| **Source** (Client)    | External system | Client connecting to an external system | `SubjectSourceBase` (`ISubjectSource`)               |
| **Connector** (Server) | Local model     | Exposing subjects to external clients   | `BackgroundService` (optionally `ISubjectConnector`) |

In practice, sources act as network clients and servers act as network servers, but this is a convention, not a requirement. The defining distinction is which side owns the data.

### Protocol-specific documentation

- [WebSocket](connectors-websocket.md) - Bidirectional WebSocket protocol for real-time synchronization
- [MQTT](connectors-mqtt.md) - MQTT client/server integration for IoT scenarios
- [OPC UA](connectors-opcua.md) - OPC UA client/server integration for industrial automation ([Client](connectors-opcua-client.md) | [Server](connectors-opcua-server.md) | [Mapping](connectors-opcua-mapping.md))
- [Subject Updates](connectors-subject-updates.md) - Wire format for serializing subject state

## Sources

A **source** represents an external authoritative system where the data originates. The local model is a **replica** that synchronizes with this external source of truth.

**Examples**: OPC UA client connecting to a PLC, MQTT client subscribing to a broker, database client, REST API consumer

**Single-owner rule**: Each property can be associated with at most one source. Sources are responsible for claiming and releasing ownership of the properties they manage. This happens initially by scanning the subject graph during startup, and dynamically when the model changes structurally (subjects attached or detached via lifecycle events). Dynamic ownership changes require the external system to support adding and removing subscriptions at runtime. You can retrieve the source that currently owns a property with `TryGetSource()`, for example to check connection status or access protocol-specific features.

### Data Flow

#### Inbound (External → Subject)

When the external system sends new values:

1. External system sends update
2. Source receives the update
3. Source calls `propertyWriter.Write()`
4. Subject property is updated

#### Outbound (Subject → External)

When you change a property value in code, the **local model is updated immediately**. These are regular C# property setters. The change is then picked up by the change queue and written to the attached source **asynchronously** in the background:

1. Property setter writes the new value to the backing field (immediate)
2. Change notification is enqueued
3. Background service dequeues the change and calls `WriteChangesAsync()` on the source
4. Source sends the update to the external system

This means the local model and the external system can be temporarily inconsistent. If the source write fails (network error, validation on the remote system), the local model already has the new value. The write retry queue handles transient failures by buffering and retrying, but the local model is always ahead of the external system.

For **servers**, the pattern is similar: local writes are applied immediately, then eventually pushed to connected clients.

#### Source-First Writes with Transactions

If you need the external source to accept the change *before* updating the local model, use source transactions. This inverts the write order so the source confirms before the local model changes. See [Write Consistency Guarantees](#write-consistency-guarantees) for a comparison of both approaches and [Transactions](tracking-transactions.md) for full details.

### Initialization

Sources use a buffer-load-replay pattern during initialization and reconnection:

1. **Buffer**: During source startup (the base calls `StartListeningAsync` on the source after `StartBuffering`), inbound updates are buffered
2. **Load**: `LoadInitialStateAsync()` fetches complete state from external system
3. **Replay**: Buffered updates are replayed in order after initial state is applied
4. **Optimistic retry re-apply**: Queued writes from the retry queue are compared against current property values and re-applied locally if the source hasn't changed them (see [Write Retry Queue](#write-retry-queue))

This ensures:
- Updates received during initialization are not lost
- Updates are applied in the correct order relative to the initial state
- Stale queued writes don't overwrite newer source values

### Write Consistency Guarantees

Property writes to sources follow a **local-first** model: the local property is updated immediately, and the change is sent to the source asynchronously. This means the local model and the source can be temporarily out of sync.

| Scenario | Local Model | Source | Outcome |
|---|---|---|---|
| Write succeeds | Updated immediately | Updated via async write | In sync |
| Write fails, retry succeeds | Updated immediately | Updated on retry | Eventually in sync |
| Disconnect + reconnect, source unchanged | Initial state restores source state, retry re-applies change | Receives change via fresh write | In sync |
| Disconnect + reconnect, source changed | Initial state applies source's new value, retry dropped | Unchanged (source wins) | In sync |

In all cases, the local model and source converge after reconnection. However, in the last scenario the local model temporarily shows a value that the source never accepted. Users may briefly see the local value before it snaps back to the source's value on reconnect.

#### Confirmed writes with transactions

If you need the source to accept a change **before** updating the local model, use [source transactions](tracking-transactions.md). With transactions, the source is written first during commit. The local model is only updated if the source accepts the change. If the source is unreachable, the commit fails and the local model remains unchanged.

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithTransactions()
    .WithSourceTransactions();

using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
sensor.Temperature = 42.0m; // Captured, NOT applied locally yet
await tx.CommitAsync(ct);   // Writes to source first, then applies locally
                            // If source rejects → local model unchanged
```

| Approach | Local update | Source guarantee | On disconnect |
|---|---|---|---|
| Without transactions | Immediate | Eventual (async + retry) | Optimistic re-apply or snap-back |
| With source transactions | After source confirms | Confirmed before local apply | Commit fails, local unchanged |

Choose based on your consistency requirements: local-first for responsiveness, transactions for confirmed delivery.

### Write Retry Queue

`SubjectSourceBase` provides a write retry queue that buffers writes during disconnection. Each connector exposes the queue size through its own configuration (for example, `OpcUaClientConfiguration.WriteRetryQueueSize`); when implementing a custom source, pass `writeRetryQueueSize` to the `SubjectSourceBase` constructor (default: 1000, pass 0 to disable).

**Behavior:**
- Ring buffer semantics: oldest writes dropped when capacity reached
- Automatic retry when `WriteChangesAsync` fails during normal operation
- Optimistic re-apply on reconnection: after loading initial state, queued changes are compared against the current (post-reconnection) property values. Only changes where the source hasn't modified the property are re-applied locally and sent to the source as fresh writes. Changes where the source value diverged are dropped (source wins).
- In-memory only: queued writes are lost on process restart

### Inbound Update Error Handling

When applying inbound updates (writing data from the external system to the local subject model), if an individual update fails (the action throws an exception), the error is logged and **the update is dropped**. There is no retry mechanism for inbound updates.

This is by design:
- Individual update failures don't block other updates from being applied
- Monitor logs for `Failed to apply subject update` errors to detect issues
- Write failures to internal models are treated as non-transient because property writes are deterministic: they either succeed or fail consistently, so retrying would not help (this includes custom validation failures)

This differs from outbound changes (writing from local model to external system), which use a retry queue to handle transient failures.

### Implementing a Source

All sources inherit from `SubjectSourceBase`, a `BackgroundService` that owns the full pump lifecycle. You override three hooks:

| Hook | Data Flow | Description |
|------|-----------|-------------|
| `StartListeningAsync` | External → Subject | Connect to the external system and start receiving changes via `propertyWriter.Write()` |
| `LoadInitialStateAsync` | External → Subject | Fetch complete state snapshot for initialization |
| `WriteChangesAsync` | Subject → External | Send local property changes to the external system |

The base class handles everything else: retry loop with backoff, buffering during initialization, change queue processing, write batching, and the write retry queue.

#### Pump Lifecycle

Each iteration of the sealed `ExecuteAsync` runs the following sequence. On failure, the base disposes the listen lifetime, waits `retryTime` (default 10s), and restarts from the top. Only `OperationCanceledException` when the host stopping token is cancelled exits the loop. All other exceptions (including internal protocol timeouts) trigger a retry.

```
ExecuteAsync (retry loop)
 ├── StartBuffering()
 ├── StartListeningAsync()        ← your hook: connect + spawn monitor
 ├── LoadInitialStateAndResume()   ← calls your LoadInitialStateAsync, then replays buffer
 ├── ReapplyRetryQueue()           ← optimistic re-apply of queued writes
 └── ProcessAsync()                ← runs ChangeQueueProcessor, calls your WriteChangesAsync
```

#### ISubjectSource Interface

`SubjectSourceBase` implements `ISubjectSource`; its abstract `WriteChangesAsync` and `LoadInitialStateAsync` satisfy the interface members directly.

```csharp
public interface ISubjectSource : ISubjectConnector
{
    int WriteBatchSize { get; }
    ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken);
    Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);
}
```

Direct interface implementation without the base class is supported for advanced scenarios, but the implementer is then responsible for its own listening loop, buffering, and outbound dispatch.

#### Custom Source Example

Derive from `SubjectSourceBase` and override the three hooks. The base owns the pump lifecycle (buffer, listen, load initial state, run change queue, retry on failure) and satisfies `ISubjectSource` directly through its public abstract members.

```csharp
public sealed class DatabaseSource : SubjectSourceBase
{
    private readonly IInterceptorSubject _root;

    public DatabaseSource(
        IInterceptorSubject root,
        IInterceptorSubjectContext context,
        ILogger<DatabaseSource> logger)
        : base(context, logger)
    {
        _root = root;
    }

    public override IInterceptorSubject RootSubject => _root;

    public override int WriteBatchSize => 100;

    protected override async Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter,
        CancellationToken cancellationToken)
    {
        var connection = await OpenDatabaseConnectionAsync(cancellationToken);

        return BackgroundTaskLifetime.Start(
            cancellationToken,
            _logger,
            ct => ListenForChangesAsync(propertyWriter, connection, ct),
            () => connection.DisposeAsync());
    }

    public override async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var data = await LoadFromDatabaseAsync(cancellationToken);
        return () => ApplyToSubject(_root, data);
    }

    public override async ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteToDatabaseAsync(changes, cancellationToken);
            return WriteResult.Success;
        }
        catch (Exception ex)
        {
            return WriteResult.Failure(changes, ex);
        }
    }
}
```

**Constructor parameters**: `bufferTime` (default 8ms) controls the change queue batching window. Changes within this window are coalesced into a single `WriteChangesAsync` call. `retryTime` (default 10s) controls the delay between retry attempts when `StartListeningAsync` or the pump loop fails.

**WriteResult**: Return `WriteResult.Success` when all changes were written. Return `WriteResult.Failure(changes, exception)` when all changes failed, or `WriteResult.PartialFailure(changes, exception)` when some succeeded. The base class enqueues the failed changes into the write retry queue automatically.

#### Registering a Source

Sources are `BackgroundService` implementations, so they need to be registered both as a singleton and as `IHostedService` so the host starts them. The recommended pattern is to provide extension methods that encapsulate this. Use a convenience overload that resolves the subject by type, and a flexible overload with a `subjectSelector` for custom resolution:

```csharp
// Convenience: resolves subject by type from DI
public static IServiceCollection AddDatabaseSource<TSubject>(
    this IServiceCollection services,
    string connectionString)
    where TSubject : IInterceptorSubject
{
    return services.AddDatabaseSource(
        sp => sp.GetRequiredService<TSubject>(),
        connectionString);
}

// Flexible: caller controls subject resolution
public static IServiceCollection AddDatabaseSource(
    this IServiceCollection services,
    Func<IServiceProvider, IInterceptorSubject> subjectSelector,
    string connectionString)
{
    var key = Guid.NewGuid().ToString();
    services.AddKeyedSingleton(key, (sp, _) => new DatabaseSource(
        subjectSelector(sp),
        sp.GetRequiredService<IInterceptorSubjectContext>(),
        sp.GetRequiredService<ILogger<DatabaseSource>>()));
    services.AddSingleton<IHostedService>(sp =>
        sp.GetRequiredKeyedService<DatabaseSource>(key));
    return services;
}
```

Using a unique keyed registration internally allows multiple sources of the same type to be registered independently (e.g., two `DatabaseSource` instances pointing at different databases for different subject trees).

The built-in connectors follow the same pattern:

```csharp
// OPC UA Client Source
builder.Services.AddOpcUaSubjectClientSource<Sensor>("opc.tcp://localhost:4840", "opc", rootName: "Root");

// MQTT Client Source
builder.Services.AddMqttSubjectClientSource<Sensor>(
    brokerHost: "localhost",
    pathProviderName: "mqtt");
```

#### BackgroundTaskLifetime

`BackgroundTaskLifetime` manages a background task tied to the listen lifetime. It creates a linked `CancellationTokenSource`, spawns the task, and on disposal cancels the token, awaits the task, and then invokes an optional cleanup callback. All built-in sources (OPC UA, MQTT, WebSocket) use it for their monitor/health-check loops.

```csharp
return BackgroundTaskLifetime.Start(
    cancellationToken,               // parent token from StartListeningAsync
    _logger,
    ct => RunMyBackgroundLoop(ct),   // the task body
    () => connection.DisposeAsync()); // optional: cleanup when disposed
```

Return the `BackgroundTaskLifetime` from `StartListeningAsync`. The base class disposes it automatically on retry or shutdown.

#### Write Retry Queue Configuration

Pass `writeRetryQueueSize` to the `SubjectSourceBase` constructor to configure the queue capacity:

```csharp
public sealed class DatabaseSource : SubjectSourceBase
{
    public DatabaseSource(
        IInterceptorSubjectContext context,
        ILogger<DatabaseSource> logger)
        : base(
            context,
            logger,
            bufferTime: TimeSpan.FromMilliseconds(8),
            retryTime: TimeSpan.FromSeconds(10),
            writeRetryQueueSize: 1000) // Enable write retry queue (0 to disable)
    {
    }

    // ... overrides
}
```

#### SourceOwnershipManager

Sources claim ownership of properties in two phases: initially inside `StartListeningAsync` by scanning the subject graph (e.g., using a path provider to determine which properties to include), and dynamically at runtime when subjects are attached to or detached from the object graph. The `SourceOwnershipManager` class simplifies this by handling:
- Property ownership tracking (which properties this source is responsible for)
- Automatic cleanup when subjects are detached from the object graph
- Safe ownership claims that prevent conflicts with other sources

To enumerate the members a source should claim, walk the registry with a nested loop: `subject.GetAllProperties()` yields properties across child subjects, and `property.GetAllAttributes()` yields the attributes attached to each one.

Values on properties and attributes vary in shape. Sources decide per case:

- **Scalars** (strings, numbers, timestamps) — serialize directly.
- **Complex objects** (records, POCOs, collections, dictionaries) — provide a custom serializer or skip.
- **Trackable subjects** (`member.CanContainSubjects == true`) — either skip or recurse via `member.Children`.

The example below skips all subject-containing members via `!CanContainSubjects`.

```csharp
public sealed class DatabaseSource : SubjectSourceBase
{
    private readonly IInterceptorSubject _root;
    private readonly SourceOwnershipManager _ownership;

    public DatabaseSource(
        IInterceptorSubject root,
        IInterceptorSubjectContext context,
        ILogger<DatabaseSource> logger)
        : base(context, logger)
    {
        _root = root;
        // SourceOwnershipManager requires WithLifecycle() on context - throws if not configured
        _ownership = new SourceOwnershipManager(
            this,
            onReleasing: property =>
            {
                // Called before a property is released - clean up protocol-specific data
                property.RemovePropertyData("DatabaseRowId");
            },
            onSubjectDetaching: subject =>
            {
                // Called when a subject is detached from the object graph
                // Use this to clean up caches or subscriptions for the subject
                CleanupCachesForSubject(subject);
            });
    }

    public override IInterceptorSubject RootSubject => _root;

    protected override async Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter,
        CancellationToken cancellationToken)
    {
        var registeredSubject = _root.TryGetRegisteredSubject();
        if (registeredSubject is null) return null;

        foreach (var property in registeredSubject.GetAllProperties())
        {
            if (!property.CanContainSubjects)
                TryClaim(property);

            foreach (var attribute in property.GetAllAttributes())
            {
                if (!attribute.CanContainSubjects)
                    TryClaim(attribute);
            }
        }

        return subscription;

        void TryClaim(RegisteredSubjectProperty property)
        {
            // ClaimSource returns false if already owned by another source
            if (!_ownership.ClaimSource(property.Reference))
            {
                _logger.LogWarning(
                    "Property {Name} already owned by another source, skipping.",
                    property.Name);
                return;
            }

            // Set up database subscription for this property...
            property.Reference.SetPropertyData("DatabaseRowId", rowId);
        }
    }

    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => Task.FromResult<Action?>(null);

    public override ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
        => new(WriteResult.Success);

    public override void Dispose()
    {
        // Releases all owned properties and unsubscribes from lifecycle events
        _ownership.Dispose();
        base.Dispose();
    }
}
```

**Ownership methods:**

| Method | Description |
|--------|-------------|
| `ClaimSource(property)` | Returns `true` if ownership was claimed, `false` if already owned by another source |
| `ReleaseSource(property)` | Releases ownership of a single property |
| `Properties` | Read-only collection of currently owned properties |
| `Dispose()` | Releases all owned properties and unsubscribes from events |

**Lifecycle integration:** The `SourceOwnershipManager` constructor automatically subscribes to lifecycle events from the source's context. When subjects are detached from the object graph, owned properties are automatically released. This prevents memory leaks and stale subscriptions. The context must have lifecycle tracking configured via `WithLifecycle()`. If not configured, the constructor throws `InvalidOperationException`.

#### Low-Level Ownership API

For advanced scenarios, you can use the extension methods directly. These operations are thread-safe and atomic:

```csharp
// Set source ownership (returns false if already owned by different source)
bool claimed = property.SetSource(mySource);

// Remove source (only if it matches expected source)
bool removed = property.RemoveSource(expectedSource);

// Check current source
if (property.TryGetSource(out var source))
{
    // Property has a source
}
```

## Servers

A **server** exposes subject properties to external clients. Unlike sources, the local model is the source of truth. The server publishes outward rather than syncing inward.

**Examples**: `OpcUaSubjectServer` exposes subjects as OPC UA nodes, `MqttSubjectServer` publishes changes to MQTT topics, `WebSocketSubjectServer` streams updates over WebSocket connections.

There is no `SubjectServerBase`. Servers are implemented as `BackgroundService` classes that optionally implement `ISubjectConnector`. The infrastructure provides building blocks, but the server implementation is up to you.

### Responsibilities

A server implementation typically handles:

- **Starting the protocol server**: bind to a port, accept connections, restart on failure
- **Publishing property changes**: observe changes via `ChangeQueueProcessor` and push them to connected clients using the protocol's wire format
- **Handling inbound writes**: receive write requests from external clients and apply them to the local model (typically via `SetValueFromSource()` to prevent echo loops)
- **Lifecycle cleanup**: release caches and subscriptions when subjects are detached from the object graph

### Pattern

All built-in servers (OPC UA, MQTT, WebSocket) follow the same structure:

1. Extend `BackgroundService` for hosting lifecycle
2. Implement `ISubjectConnector` for type consistency and connector enumeration
3. Create a `ChangeQueueProcessor` in `ExecuteAsync` to subscribe to property changes before the protocol server starts accepting clients
4. Accept incoming client connections and route write requests to the local model via `SetValueFromSource()`
5. Use a retry/restart loop in `ExecuteAsync` to recover from protocol failures

The built-in server implementations serve as reference for building custom servers. See the protocol-specific documentation for details:
- [OPC UA Server](connectors-opcua-server.md)
- [MQTT](connectors-mqtt.md)
- [WebSocket](connectors-websocket.md)

### Registering a Server

Servers follow the same registration pattern as sources: register as singleton + `IHostedService`, typically via an extension method. The built-in connectors provide these:

```csharp
// OPC UA Server
builder.Services.AddOpcUaSubjectServer<Sensor>(sourceName: "opc", rootName: "Devices");

// MQTT Server
builder.Services.AddMqttSubjectServer<Sensor>(pathProviderName: "mqtt", brokerPort: 1883);

// WebSocket Server (standalone)
builder.Services.AddWebSocketSubjectServer<Sensor>(config =>
{
    config.Port = 8080;
    config.Path = "/ws";
});
```

## Shared Infrastructure

### ISubjectConnector

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

> **Note**: Path providers are implementation details. A source/server may use a path provider internally to decide which properties to include and how to map them, or it may not use one at all.

### Path Providers

Path providers map between subject property paths and external system paths. They are defined in `Namotion.Interceptor.Registry.Paths`.

#### IPathProvider Interface

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
    string? TryGetPropertySegment(RegisteredSubjectProperty property);

    /// <summary>
    /// Find a property by its path segment.
    /// </summary>
    RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment);
}
```

#### Built-in Providers

- **DefaultPathProvider** - Uses property names exactly as defined
- **CamelCasePathProvider** - Converts property names to camelCase for JSON APIs
- **AttributeBasedPathProvider** - Uses `[Path]` attributes for custom mapping

#### [Path] Attribute

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

#### [InlinePaths] Attribute

Marks a dictionary property as a transparent container for path resolution:

```csharp
[InterceptorSubject]
public partial class ProductionLine
{
    public partial string Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, Machine> Machines { get; set; }
}

[InterceptorSubject]
public partial class Machine
{
    public partial string Status { get; set; }
    public partial decimal Temperature { get; set; }
}
```

With `[InlinePaths]`:
- Path `Line.CNC01.Status` resolves to `Line.Machines["CNC01"].Status`
- Direct properties take precedence over child keys. If a subject has both a direct property and a dictionary key with the same name, the property wins and the key is unreachable via that segment
- Only one property per class may be marked with `[InlinePaths]`; multiple properties throws `InvalidOperationException`
- Works with `AttributeBasedPathProvider` without requiring `[Path]` attribute on the dictionary
- Built into `PathProviderBase.TryGetPropertyFromSegment`

### Updates

The `Namotion.Interceptor.Connectors.Updates` namespace contains serialization infrastructure for subject state:

- **SubjectUpdate** - Serializable representation of a subject's state
- **SubjectPropertyUpdate** - Serializable representation of a property change
- **ISubjectUpdateProcessor** - Filter/transform updates before serialization

These are used by both sources and servers (e.g., ASP.NET Core controllers, SignalR hubs).

For details on the update format, collection synchronization, and apply logic, see [Subject Updates](connectors-subject-updates.md).

### Thread Safety

Properties can receive concurrent writes from multiple origins:
- **Source**: Inbound updates from the external system
- **Servers**: Background services exposing the property
- **Local code**: Application services, UI handlers, etc.

Individual property updates are atomic and thread-safe without requiring additional synchronization.

When overriding `StartListeningAsync`, use the provided `SubjectPropertyWriter` to write inbound updates. This handles buffering during initialization and ensures correct ordering.
