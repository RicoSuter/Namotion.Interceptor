# OPC UA Client

> Part of the [OPC UA integration](connectors-opcua.md). See also: [Server](connectors-opcua-server.md) | [Mapping Guide](connectors-opcua-mapping.md)

Connect to an OPC UA server to synchronize C# objects with OPC UA nodes. The client automatically establishes connections, subscribes to node changes, and synchronizes values bidirectionally.

## Setup

```csharp
[InterceptorSubject]
public partial class Machine
{
    [Path("opc", "Temperature")]
    public partial decimal Temperature { get; set; }

    [Path("opc", "Speed")]
    public partial decimal Speed { get; set; }
}

builder.Services.AddSingleton(machine);
builder.Services.AddOpcUaSubjectClientSource<Machine>(
    serverUrl: "opc.tcp://plc.factory.com:4840",
    connectorName: "opc",
    rootPath: ["MyMachine"]);

// ...
var host = builder.Build();
await host.StartAsync();
Console.WriteLine(machine.Temperature); // Read property which is synchronized with OPC UA server
machine.Speed = 100; // Writes to OPC UA server
```

For multiple client sources, use `AddKeyedOpcUaSubjectClientSource` with a name and resolve via `[FromKeyedServices("name")]` (see [Diagnostics](#diagnostics)).

**Parameters:**
- `serverUrl` - The OPC UA server endpoint (e.g., `"opc.tcp://localhost:4840"`)
- `connectorName` - The connector name used to match `[Path]` attributes (e.g., `"opc"` matches `[Path("opc", "Temperature")]`)
- `rootPath` - Optional path segments to the root node to start browsing from under the Objects folder (e.g., `["MyMachine"]`)

Two DI overloads are available: the simple generic shown above and a full configuration overload (shown below).

## Resolving the Client Source

After registration, resolve `IOpcUaSubjectClientSource` to access diagnostics, the underlying session, and node ID lookups.

**DI (unnamed registration):**

```csharp
var source = serviceProvider.GetRequiredService<IOpcUaSubjectClientSource>();
```

**DI (keyed registration):**

```csharp
var source = serviceProvider.GetRequiredKeyedService<IOpcUaSubjectClientSource>("server1");
```

**From a property reference** (useful deep in business code where you hold a `PropertyReference` but not the DI container):

```csharp
if (property.TryGetSource(out var subjectSource) &&
    subjectSource is IOpcUaSubjectClientSource source)
{
    // use source.Diagnostics, source.CurrentSession, etc.
}
```

For direct instantiation (without DI), `CreateOpcUaClientSource` returns `IOpcUaSubjectClientSource` directly.

## Configuration

For advanced scenarios, use the full configuration API to customize connection behavior, subscription settings, and dynamic property discovery.

```csharp
builder.Services.AddOpcUaSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<MyRoot>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://localhost:4840",
        TypeResolver = new OpcUaTypeResolver(logger),
        ValueConverter = new OpcUaValueConverter(),
        SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),

        // Optional
        RootName = "Machines",
        DefaultNamespaceUri = "http://factory.com/machines",
        ApplicationName = "MyOpcUaClient",
        ReconnectInterval = TimeSpan.FromSeconds(5),

        // Subscription settings (null = use OPC UA library defaults)
        DefaultSamplingInterval = 0,      // 0 = exception-based (immediate), null = server decides
        DefaultPublishingInterval = 100,
        DefaultQueueSize = 10,
        MaxItemsPerSubscription = 1000,

        // Data change filter (null = use OPC UA library defaults)
        DefaultDataChangeTrigger = null,  // StatusValue (report on value change)
        DefaultDeadbandType = null,       // None (report all changes)
        DefaultDeadbandValue = null,      // 0.0

        // Performance tuning
        BufferTime = TimeSpan.FromMilliseconds(10),
        RetryTime = TimeSpan.FromSeconds(1)
    });
```

**Dynamic Property Discovery:**

```csharp
ShouldAddDynamicProperty = async (node, ct) =>
{
    return node.BrowseName.Name.StartsWith("Sensor");
}
```

**Dynamic Attribute Discovery:**

When loading variable nodes, any child properties (HasProperty references) not found in the C# model can be added as dynamic attributes:

```csharp
ShouldAddDynamicAttribute = async (node, ct) =>
{
    // Add standard OPC UA metadata attributes dynamically
    return node.BrowseName.Name is "EURange" or "EngineeringUnits";
}
```

By default, all unknown attributes are added. Set to `null` to disable dynamic attribute discovery.

### Configuration Reference

Beyond the settings shown above, the following properties are available on `OpcUaClientConfiguration`:

**Connection & Session:**

| Property | Default | Description |
|----------|---------|-------------|
| `ApplicationName` | "Namotion.Interceptor.Client" | Application name for identification and certificate generation |
| `UseSecurity` | false | Enable signing and encryption (see [Transport Security](#transport-security)) |
| `CreateUserIdentity` | null | Async factory for user credentials (see [Authentication](#authentication)) |
| `CertificateStoreBasePath` | "pki" | Base directory for certificate stores |
| `SessionFactory` | null | Custom session factory (uses `DefaultSessionFactory` when null) |
| `TelemetryContext` | NullTelemetryContext | Telemetry integration for logging and diagnostics |
| `Mapper` | OpcUaCompositeMapper | `IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>` that maps C# properties to OPC UA nodes (see [Mapping Guide](connectors-opcua-mapping.md)) |

**Subscription Tuning:**

| Property | Default | Description |
|----------|---------|-------------|
| `DefaultPublishingInterval` | 0 | Publishing interval in ms (0 = server default) |
| `SubscriptionKeepAliveCount` | 10 | Keep-alive count for subscriptions |
| `SubscriptionLifetimeCount` | 100 | Lifetime count (must be >= 3x keep-alive count) |
| `SubscriptionPriority` | 0 | Subscription priority (0 = server default) |
| `SubscriptionMaxNotificationsPerPublish` | 0 | Max notifications per publish (0 = server default) |
| `MinPublishRequestCount` | 3 | Minimum outstanding publish requests |
| `SubscriptionSequentialPublishing` | false | Process messages in order (reduces throughput) |

**Polling Fallback:**

| Property | Default | Description |
|----------|---------|-------------|
| `EnablePollingFallback` | true | Fall back to polling when subscriptions fail |
| `PollingInterval` | 1s | Polling interval (min 100ms) |
| `PollingBatchSize` | 100 | Items per polling read batch |
| `PollingDisposalTimeout` | 10s | Timeout for polling cleanup during disposal |
| `PollingCircuitBreakerThreshold` | 5 | Consecutive failures before circuit breaker opens |
| `PollingCircuitBreakerCooldown` | 30s | Cooldown after circuit breaker opens |

**Read After Write:**

| Property | Default | Description |
|----------|---------|-------------|
| `EnableReadAfterWrite` | true | Schedule reads after writes for non-exception-based items |
| `ReadAfterWriteBuffer` | 50ms | Buffer added to revised interval before read-back |

**Browsing:**

| Property | Default | Description |
|----------|---------|-------------|
| `MaxReferencesPerNode` | 0 | Max references per browse request (0 = server default) |

## Security

### Transport Security

```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://plc.factory.com:4840",
    UseSecurity = true,  // Enable signing and encryption (recommended for production)
    // ... other settings
};
```

When `UseSecurity = true`, the client prefers secure endpoints with message signing and encryption. The default is `false` for development convenience.

### Authentication

By default, the client connects with anonymous authentication. Use `CreateUserIdentity` to provide credentials:

```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://plc.factory.com:4840",
    CreateUserIdentity = _ => Task.FromResult(new UserIdentity("user", "password")),
    // ... other settings
};
```

For credentials from a secret manager or vault:

```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://plc.factory.com:4840",
    CreateUserIdentity = async cancellationToken =>
    {
        var secret = await vault.GetSecretAsync("opcua-credentials", cancellationToken);
        return new UserIdentity(secret.Username, secret.Password);
    },
    // ... other settings
};
```

For certificate-based authentication:

```csharp
CreateUserIdentity = _ =>
{
    var certificate = X509Certificate2.CreateFromPemFile("client.pem", "client.key");
    return Task.FromResult(new UserIdentity(certificate));
}
```

### Custom Application Configuration

Override `CreateApplicationInstanceAsync()` in a derived configuration class for full control over OPC UA application settings (certificates, transport quotas, security policies):

```csharp
public class MyOpcUaClientConfiguration : OpcUaClientConfiguration
{
    public override async Task<ApplicationInstance> CreateApplicationInstanceAsync()
    {
        var application = await base.CreateApplicationInstanceAsync();

        // Customize the application configuration
        var config = application.ApplicationConfiguration;
        config.SecurityConfiguration.AutoAcceptUntrustedCertificates = false;
        config.TransportQuotas.MaxMessageSize = 64_000_000;

        return application;
    }
}
```

## Monitoring & Subscriptions

### The subscription pipeline: sampling vs publishing

A subscription delivers data in two stages, governed by two different intervals at two different scopes:

```
 PLC tag / variable
      │
      │  sampling interval     (per monitored item)
      ▼  server samples the source, applies deadband/trigger
 per-item queue                (QueueSize, DiscardOldest)
      │
      │  publishing interval   (per subscription)
      ▼  server batches all queued notifications from all items
    client
```

- **Sampling interval** (`DefaultSamplingInterval`, per monitored item): how often the *server* reads the underlying source for a new value. Sets data freshness and change resolution.
- **Publishing interval** (`DefaultPublishingInterval`, per subscription): how often the *server* packages the notifications queued across *all* items in the subscription and sends them in one response. Sets network and batching overhead.

One subscription has a single publishing interval shared by many monitored items, each with its own sampling interval and queue.

**How they combine with queue size.** Between publishes, each item keeps sampling into its queue:

- Sample fast, publish slower, deeper queue (e.g. sampling 100 ms, publishing 1000 ms, `QueueSize = 10`): the server captures every 100 ms change and delivers up to 10 of them in a single round-trip, keeping fidelity while cutting network chatter.
- Same setup with `QueueSize = 1`: intermediate changes coalesce and you receive only the latest value per publish.
- Publishing faster than sampling gains nothing; sampling faster than the source changes only wastes server CPU.

**Both are requests, not guarantees.** The client asks for these values; the server *revises* them to what it can honor (it cannot sample faster than its scan cycle) and returns the revised sampling interval, publishing interval, and queue size. This client reads the revised sampling interval back and, for the `SamplingInterval = 0` case, uses it to schedule read-after-writes (see [Read After Write Fallback](#read-after-write-fallback)).

For choosing sampling interval `0` vs `>0` specifically (exception-based vs sampling), see the next section.

### Sampling vs Exception-Based Monitoring

OPC UA supports two monitoring modes for value changes:

**Sampling-based** (`SamplingInterval > 0`): The server checks the value at fixed intervals and reports if it changed since the last sample. Fast changes that occur between samples may be missed.

**Exception-based** (`SamplingInterval = 0`): The server reports immediately whenever the value changes. This requires the server to support exception-based monitoring (indicated by `MinimumSamplingInterval = 0` on the node).

**When to use exception-based monitoring:**
- Discrete variables (boolean flags, state indicators) where you need to catch every transition
- Handshake patterns (client writes `true`, PLC sets back to `false`)
- Any value where missing a transition would cause system failures

```csharp
// Request exception-based monitoring for a discrete variable
[OpcUaNode("StartCommand", SamplingInterval = 0)]
public partial bool StartCommand { get; set; }
```

**Note:** Even with `SamplingInterval = 0`, the server may revise this based on its capabilities. Check the server's `RevisedSamplingInterval` to verify exception-based monitoring is active.

### Discrete vs Analog Variables

Industrial automation distinguishes between two types of variables:

| Type | Characteristics | Examples | OPC UA Monitoring |
|------|-----------------|----------|-------------------|
| **Analog** | Continuous values, gradual changes, sampling is fine | Temperature, pressure, speed | Sampling-based (`SamplingInterval > 0`) with optional deadband |
| **Discrete** | Binary on/off, every transition matters | Handshake flags, command triggers, state indicators | Exception-based (`SamplingInterval = 0`) |

For **discrete variables**, missing a transition can cause system failures. A classic example is the handshake pattern: client writes `1`, PLC acknowledges by writing `0`. If sampling misses the `1->0` transition (because both samples see `0`), the client never knows the PLC acknowledged.

OPC UA's sampling-based monitoring compares each sample against the *previous sample*, not against what the client knows. When a value changes faster than the sampling rate (e.g., `0->1->0` between samples), the server sees `0` at both sample points and reports no change.

```
Sampling-based (SamplingInterval = 100ms):
t=0ms:   Sample value=0, baseline=0 -> no change reported
t=50ms:  Client writes 1 -> value=1 (no sample happens)
t=60ms:  PLC writes 0 -> value=0 (no sample happens)
t=100ms: Sample value=0, baseline=0 -> no change reported
```

This is spec-compliant behavior per [OPC UA Part 4](https://reference.opcfoundation.org/Core/Part4/v104/docs/5.12.1.2), not a bug.

### Data Change Filters

Control which value changes generate notifications:

| Trigger | Reports when... |
|---------|-----------------|
| `Status` | Status code changes |
| `StatusValue` | Status or value changes (default) |
| `StatusValueTimestamp` | Status, value, or timestamp changes |

| Deadband Type | Filters out... |
|---------------|----------------|
| `None` | Nothing - reports all changes (default) |
| `Absolute` | Changes smaller than the absolute threshold |
| `Percent` | Changes smaller than the percentage of range |

```csharp
// Analog sensor - only report changes > 0.5 units
[OpcUaNode("Temperature",
    DeadbandType = DeadbandType.Absolute,
    DeadbandValue = 0.5)]
public partial double Temperature { get; set; }
```

### Read After Write Fallback

When a server doesn't support exception-based monitoring, the library provides an automatic read-after-write fallback.

**Solution hierarchy:**
1. **Best: Exception-based monitoring** (`SamplingInterval = 0`) - Server reports every change immediately
2. **Fallback: Read-after-write** - When the server revises `SamplingInterval = 0` to non-zero, automatically read values after writes

The library automatically detects when `SamplingInterval = 0` was revised to a non-zero value (common with legacy PLCs or servers that don't support exception-based monitoring). For these properties, after a successful write, it schedules a read-back to catch server-side changes that sampling would miss.

```csharp
// Mark as discrete variable - request exception-based monitoring
[OpcUaNode("CommandTrigger", SamplingInterval = 0)]
public partial bool CommandTrigger { get; set; }
```

**Configuration:**
```csharp
var config = new OpcUaClientConfiguration
{
    // Enable/disable read-after-write fallback (default: true)
    EnableReadAfterWrite = true,

    // Buffer added to revised interval before reading back (default: 50ms)
    ReadAfterWriteBuffer = TimeSpan.FromMilliseconds(50)
};
```

**Behavior:**
- Only triggers for properties where `SamplingInterval = 0` was revised to > 0
- Multiple rapid writes are coalesced into a single read
- Reads are batched for efficiency
- Circuit breaker prevents repeated failures from overwhelming the server
- Logged when activated so you can verify which properties need this fallback

**Limitations:**
If the server's minimum sampling rate is slower than the value changes, no client-side workaround can help. In that case, consider:
- Configuring the OPC UA server to support faster sampling or exception-based monitoring
- Changing the PLC code to use a counter-based pattern instead of boolean handshakes
- Using OPC UA Methods for command/response patterns (requires PLC support)

### Subscription Configuration

Configure monitored item behavior at global or per-property level:

| Setting | Default | Description |
|---------|---------|-------------|
| `DefaultSamplingInterval` | null | Sampling interval in ms (null = server decides, 0 = exception-based) |
| `DefaultQueueSize` | null | Values to buffer (null = library default of 1) |
| `DefaultDiscardOldest` | null | Discard oldest when queue full (null = library default of true) |
| `DefaultDataChangeTrigger` | null | When to report changes (null = StatusValue) |
| `DefaultDeadbandType` | null | Deadband filter type (null = None) |
| `DefaultDeadbandValue` | null | Deadband threshold (null = 0.0) |

All settings can be overridden per-property using `[OpcUaNode]` attribute.

## Resilience

### Write Retry Queue During Disconnection

Write retry queue behavior (ring buffer, optimistic re-apply on reconnection, source wins on conflict) is provided by `SubjectSourceBase`. See [Connectors: Write Retry Queue](connectors.md#write-retry-queue). Configure via `WriteRetryQueueSize`:

```csharp
builder.Services.AddOpcUaSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://plc.factory.com:4840",
        WriteRetryQueueSize = 1000 // Buffer up to 1000 writes (default, 0 to disable)
    });

// Writes are automatically queued during disconnection
machine.Speed = 100; // Queued if disconnected, written immediately if connected
```

### Polling Fallback for Unsupported Nodes

The library automatically falls back to periodic polling when OPC UA nodes don't support subscriptions. This ensures all properties remain synchronized even with legacy servers or special node types.

```csharp
builder.Services.AddOpcUaSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://plc.factory.com:4840",
        EnablePollingFallback = true, // Default
        PollingInterval = TimeSpan.FromSeconds(1), // Default: 1 second
        PollingBatchSize = 100 // Default: 100 items per batch
    });
```

**Automatic behavior:**
- Nodes automatically switch to polling when subscriptions fail
- Batched reads for efficiency (reduces network overhead)
- Same value change detection as subscriptions (only updates on actual changes)
- No configuration required - works out of the box

### Auto-Healing of Failed Monitored Items

The library automatically retries failed subscription items that may succeed later, such as when server resources become available.

```csharp
builder.Services.AddOpcUaSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://plc.factory.com:4840",
        SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(5) // Default: 5 seconds
    });
```

**Smart retry logic:**
- Retries transient errors: `BadTooManyMonitoredItems`, `BadOutOfService`, `BadMonitoringModeUnsupported`
- Skips permanent errors: `BadNodeIdUnknown`
- Health checks run at configurable intervals (minimum: 5 seconds)
- Items that permanently don't support subscriptions automatically fall back to polling

**Configuration validation:**
- `SubscriptionHealthCheckInterval` minimum of 5 seconds enforced
- `PollingInterval` minimum of 100 milliseconds enforced
- Fail-fast with clear error messages on invalid configuration

### Initial Load Failure Recovery

When the initial subject load encounters a transient browse failure (e.g., `BadServerHalted` on a single node), the loader rolls back any partial state it produced and lets `SubjectSourceBase` retry from a clean slate after `RetryTime`.

Only structural failures (browse and type resolution) trigger this rollback-and-retry. The initial *value* read is best-effort: a node that returns a transient or not-ready status such as `BadWaitingForInitialData` is skipped and backfilled by the subscription, rather than aborting the load and forcing a reconnect.

**Guarantees on failure:**
- No source ownership claims are committed
- No staged sub-subjects leak into the registry
- Root subject reference properties remain unassigned (no half-loaded sub-graphs)
- Monitored items from the failed attempt are discarded

**Retry:** the next `StartListeningAsync` attempt runs against a clean state.
No special caller handling required.

**Partial-deferral trade-off (dynamic properties only):** dynamic property slots added to root during discovery remain as empty slots after failure; they're reused on retry. For statically-typed root subjects (most production usage) no such slots exist and failure leaves root completely unchanged.

### Connection Loss Recovery

The client handles connection loss through two cooperating mechanisms: the OPC UA SDK's built-in `SessionReconnectHandler` (automatic) and a health check loop that performs manual reconnection when the SDK handler fails or is insufficient.

**All reconnection paths guarantee eventual consistency** through a full server state read. The client never relies solely on subscription notifications for state synchronization after a reconnection.

#### Reconnection Flows

**Flow A: SDK reconnection with subscription transfer**

When the connection drops, the OPC UA SDK's `SessionReconnectHandler` attempts to reconnect automatically. If it succeeds and transfers existing subscriptions to the new session:

1. Keep-alive detects dead connection and triggers the SDK reconnect handler
2. SDK creates a new session and transfers subscriptions via `TransferSubscriptions`
3. `OnReconnectComplete` accepts the new session and sets a `NeedsFullStateSync` flag
4. The health check loop detects the flag on the next iteration:
   - Starts buffering incoming subscription notifications
   - Reads ALL property values from the server
   - Applies server values, replays the buffer, resumes normal operation
5. Full consistency is restored

**Flow B: SDK reconnection fails or returns a preserved session**

If the SDK handler cannot transfer subscriptions (e.g., server restarted and old subscriptions are gone), or if it returns the same session object ("preserved session"), the client abandons the session entirely:

1. `OnReconnectComplete` calls `AbandonCurrentSession()` (session nulled, transport killed)
2. The health check loop detects the dead session and triggers manual reconnection
3. Manual reconnection creates a fresh session, new subscriptions, and performs a full state read
4. Full consistency is restored

Preserved sessions are always abandoned because subscriptions may have been silently deleted by the server (subscription lifetime expired during the disconnect period), leaving no mechanism for future data change notifications.

**Flow C: No SDK handler active (session dead without reconnection in progress)**

If the session dies without the SDK handler being active (e.g., after `KillAsync`, `ClearSessionAsync`, or a previous failed reconnection):

1. The health check loop detects the dead session
2. Triggers manual reconnection: fresh session, new subscriptions, full state read
3. Full consistency is restored

#### Stall Detection

When the SDK's reconnection handler gets stuck (e.g., server never responds), the client automatically detects the stall and forces a reconnection reset. Configure via `MaxReconnectDuration` (default: 30 seconds). If the SDK reconnection hasn't succeeded within this duration, a full session reset and manual reconnection is triggered.

#### Consistency Guarantees and Trade-offs

**Guarantee**: After any reconnection completes, all client property values will match the server's current state. The full state read (buffer + read all values + replay + resume pattern) ensures no property is left stale, regardless of what happened to subscription notifications during the disconnect.

**Trade-off: brief stale data window after SDK reconnection (Flow A)**

Between the SDK's `OnReconnectComplete` (step 3) and the health check's full state read (step 4), there is a window of up to one health check interval (~5 seconds by default) where:

- Subscription notifications from the transfer may apply partially (some notifications may have been lost due to server-side queue overflow or subscription lifetime expiration)
- Property values may temporarily reflect incomplete state

This window is bounded and self-correcting: the health check's full state read overwrites all values with the server's current state. For most industrial applications, this brief window is acceptable. If tighter consistency is required, reduce `SubscriptionHealthCheckInterval`.

**Eventual consistency for writes**: Client-to-server writes during disconnection are buffered in the write retry queue (see above). On reconnection, after loading the server's current state, queued changes are optimistically re-applied only if the server hasn't changed the property (source wins on conflict). Combined with the full state read for server-to-client values, this provides bidirectional eventual consistency.

### Resilience Configuration

For 24/7 production use, the default configuration provides robust resilience:

| Setting | Default | Description |
|---------|---------|-------------|
| `KeepAliveInterval` | 5s | How quickly disconnections are detected |
| `ReconnectInterval` | 5s | Time between SDK reconnection attempts |
| `ReconnectHandlerTimeout` | 60s | Max time the reconnect handler attempts before giving up |
| `MaxReconnectDuration` | 30s | Max time for SDK reconnection before forcing manual reset |
| `SessionTimeout` | 120s | Server-side session lifetime (should be > OperationTimeout) |
| `OperationTimeout` | 30s | Timeout for individual OPC UA operations |
| `SubscriptionHealthCheckInterval` | 5s | Interval for health checks and post-reconnection state sync |
| `WriteRetryQueueSize` | 1000 | Updates buffered during disconnection |
| `SessionDisposalTimeout` | 5s | Max wait for graceful session close |
| `SubscriptionSequentialPublishing` | false | Process subscription messages in order (see Thread Safety) |

## Extensibility

For custom type conversions (used by both client and server), see [Custom Value Converter](connectors-opcua.md#custom-value-converter).

### Custom Type Resolver

Extend `OpcUaTypeResolver` to customize how the client infers C# types from OPC UA node metadata during dynamic property discovery. This is useful when you want specific OPC UA nodes to map to custom C# classes.

```csharp
public class CustomTypeResolver : OpcUaTypeResolver
{
    public override async Task<Type?> TryGetTypeForNodeAsync(
        ISession session, ReferenceDescription reference, CancellationToken ct)
    {
        if (reference.BrowseName.Name.StartsWith("CustomDevice"))
            return typeof(MyCustomDevice);
        return await base.TryGetTypeForNodeAsync(session, reference, ct);
    }
}
```

### Custom Subject Factory

Extend `OpcUaSubjectFactory` to control how subject instances are created when the client discovers OPC UA object nodes. This allows custom initialization logic or dependency injection when creating subjects.

```csharp
public class CustomSubjectFactory : OpcUaSubjectFactory
{
    public override async Task<IInterceptorSubject> CreateSubjectAsync(
        RegisteredSubjectProperty property, ReferenceDescription node,
        ISession session, CancellationToken ct)
    {
        if (property.Type == typeof(Machine))
        {
            var machine = new Machine(property.Subject.Context);
            machine.Name = node.BrowseName.Name;
            return machine;
        }
        return await base.CreateSubjectAsync(property, node, session, ct);
    }
}
```

## Advanced Usage

### Complex Hierarchies

The library automatically handles nested object hierarchies, traversing through properties that reference other interceptor subjects. This enables modeling complex industrial systems with multiple levels of composition.

```csharp
[InterceptorSubject]
public partial class Factory
{
    [Path("opc", "Lines")]
    public partial ProductionLine[] Lines { get; set; }
}

[InterceptorSubject]
public partial class ProductionLine
{
    [Path("opc", "Machines")]
    public partial Machine[] Machines { get; set; }
}
```

The library automatically traverses the entire hierarchy.

### Dynamic Properties

When the client discovers OPC UA nodes not defined in your C# model, they can be added as dynamic properties at runtime. Access these properties through the [registry](registry.md) after the client completes its initial load. See also [Dynamic property and attribute creation](registry.md#dynamic-property-and-attribute-creation).

```csharp
var registered = machine.TryGetRegisteredSubject();
var dynamicProperty = registered.Properties.FirstOrDefault(p => p.Name == "UnknownSensor");
if (dynamicProperty != null)
{
    var value = dynamicProperty.Reference.GetValue();
}
```

#### Type Resolution

The `OpcUaTypeResolver` maps OPC UA nodes to CLR types during dynamic discovery:

- **Object nodes** become `DynamicSubject` (named sub-properties on the parent subject).
- **Object nodes with `[numeric]` convention** (e.g., `People[0]`, `People[1]`) become `DynamicSubject[]` collections.
- **Object nodes with `[string]` convention** (e.g., `Devices[SensorA]`) become `IReadOnlyDictionary<string, DynamicSubject>` dictionaries.

The bracket convention is produced by this library's OPC UA server when exposing C# collections and dictionaries. Standard OPC UA servers typically use named children and are always treated as single subjects.
- **Variable nodes** are mapped to CLR types based on their OPC UA DataType. The resolver uses `session.TypeTree` to walk the type hierarchy, so custom DataType subtypes (e.g., a server-specific `LocalizedText` variant) are correctly resolved to their base built-in type.

| OPC UA BuiltInType | CLR Type | Notes |
|---|---|---|
| Boolean, SByte, Byte, Int16, ... | bool, sbyte, byte, short, ... | Direct mapping |
| String | string | |
| DateTime | DateTime | |
| LocalizedText | LocalizedText | |
| Enumeration | int | Mapped to underlying Int32 |
| Number | double | Abstract numeric base type |
| Integer | long | Abstract signed integer |
| UInteger | ulong | Abstract unsigned integer |
| ExtensionObject | ExtensionObject | Complex structured types |
| XmlElement | string | |
| Variant, Null | (skipped) | Type cannot be determined |

Override `TryGetTypeForNodeAsync` on `OpcUaTypeResolver` to customize type mapping for specific nodes.

#### Subject Deduplication

When the same OPC UA node appears at multiple paths in the address space (e.g., `Identification` referenced from both `MyMachine` and `MachineryBuildingBlocks`), the client reuses the same subject instance. Reuse applies to single references as well as collection and dictionary elements: any property that resolves to the same `NodeId` during a load is bound to the existing subject, which receives a single set of monitored items. The same applies within a single browse call: if a server exposes one target through multiple reference types (e.g., both `HasComponent` and `HasProperty`), the duplicate browse references are filtered so the underlying node is processed exactly once per parent, at both the property and attribute level.

Round-trip identity is preserved for the common cross-parent DAG: if the server-side C# model has a single instance reachable from two different parent paths, the client materializes one instance bound to both parent properties. The case where two properties on the **same parent** reference the same instance under different names does not round-trip because OPC UA stores the BrowseName on the target node rather than on the reference. See [connectors-opcua-server.md](connectors-opcua-server.md#subject-deduplication) for the full discussion of the server-side behavior and its limitations.

## Write Error Handling

When a batch write to the OPC UA server partially fails, the client throws an `OpcUaWriteException`. The exception distinguishes between transient failures (connectivity issues, timeouts that may succeed on retry) and permanent failures (invalid nodes, access denied; should not be retried). The write retry queue (see [Resilience](#write-retry-queue-during-disconnection)) handles transient failures automatically during disconnection, but writes that fail while connected surface this exception.

## Diagnostics

`IOpcUaSubjectClientSource.Diagnostics` exposes a live facade. Resolve it once and poll (see [Resolving the Client Source](#resolving-the-client-source)).

Categories: connection (`IsConnected`, `IsReconnecting`, `SessionId`, `LastConnectedAt`), subscriptions (`SubscriptionCount`, `MonitoredItemCount`), throughput (`IncomingChangesPerSecond`, `OutgoingChangesPerSecond`), reconnection history (`TotalReconnectionAttempts`, `SuccessfulReconnections`, `FailedReconnections`, `AbandonedReconnections`, `LastError`), [polling fallback](#polling-fallback-for-unsupported-nodes) (`PollingItemCount`), [read-after-write](#read-after-write-fallback) (`PendingReadAfterWrites`). All properties are thread-safe for reading.

## Direct Session Access

For scenarios the connector does not cover natively (OPC UA **Methods**, **Alarms & Conditions**), `IOpcUaSubjectClientSource.CurrentSession` exposes the underlying `ISession` (see [Resolving the Client Source](#resolving-the-client-source) for how to obtain the source):

```csharp
if (source.CurrentSession is { } session)
{
    var outputs = await session.CallAsync(parentNodeId, methodId, inputArgs, cancellationToken);
}
```

**Lifecycle contract:** read `CurrentSession` immediately before each use. It may be `null` during reconnection and the instance changes after a manual reconnect (Flow C), a transferred-subscription failure (Flow B), or a stall reset. Never cache the reference. For long-running session-bound state (e.g. an A&C `Subscription`), subscribe to `CurrentSessionChanged` (below) or recreate on demand when calls fail with `BadSessionIdInvalid` / `BadSessionNotActivated`.

### Reacting to session swaps with `CurrentSessionChanged`

For consumers holding session-bound state (typically A&C subscriptions), `CurrentSessionChanged` fires on every transition (including to/from `null`). Method-call consumers usually do not need it: they re-read `CurrentSession` per call and surface a stale session as a failure on the next call. The event is for consumers that have no such inbound traffic.

```csharp
opcUaSource.CurrentSessionChanged += (_, args) =>
{
    if (args.PreviousSession is not null)
    {
        // Synchronous local cleanup only; transport may already be closed.
        DisposeMyAlarmsSubscription(args.PreviousSession);
    }

    if (args.CurrentSession is not null)
    {
        // Async work fire-and-forget so the handler returns quickly.
        var newSession = args.CurrentSession;
        _ = Task.Run(async () =>
        {
            try { await RecreateMyAlarmsSubscriptionAsync(newSession); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to recreate A&C subscription"); }
        });
    }
};
```

The event fires on the connector's own thread but **outside** the reconnection lock, in transition order, so a slow handler will not stall reconnection. Use `PreviousSession` only for synchronous local cleanup (its transport may already be closed). For async work on `CurrentSession` use fire-and-forget (`_ = Task.Run(...)`) and tolerate the session being swapped again before the task completes; the next `CurrentSessionChanged` event will surface the new state. Handler exceptions are caught and logged, but per standard event semantics a throwing subscriber skips later subscribers, so isolate exceptions in your own handler if multiple must run.

## Node ID Resolution

`IOpcUaSubjectClientSource.TryGetNodeId` resolves the OPC UA `NodeId` bound to a tracked property. This is useful when making raw `ISession` calls that require a `NodeId`:

```csharp
if (source.TryGetNodeId(machine.GetPropertyReference(nameof(Machine.Temperature)), out var nodeId))
{
    // Use nodeId with source.CurrentSession for direct OPC UA operations
}
```

Returns `false` if the property is not owned by this source or has not been resolved yet (e.g. before initial connection).

## Thread Safety

The library ensures thread-safe operations across all OPC UA interactions. Property operations are synchronized via `SyncRoot` when interceptors are present, subscription callbacks use thread-safe concurrent queues, and multiple OPC UA clients can connect concurrently.

Write queue operations use `Interlocked` operations for thread-safe counter updates and flush operations are protected by semaphores to prevent concurrent flush issues.

**Update ordering:**
By default (`SubscriptionSequentialPublishing = false`), subscription callbacks may be processed in parallel for higher throughput. This means that for the same property, if two rapid updates arrive in different publish responses, they could theoretically be applied out of order. Each update carries a `SourceTimestamp` from the server, but the library does not enforce timestamp-based ordering.

For most use cases (sensor values, status updates), this is acceptable since you typically want the latest value. If your application requires strict ordering guarantees, set `SubscriptionSequentialPublishing = true` to process all subscription messages sequentially at the cost of reduced throughput.

To prevent feedback loops when external sources update properties, use `SubjectChangeContext.WithSource()` to mark the change source:

```csharp
using (SubjectChangeContext.WithSource(opcUaSource))
{
    subject.Temperature = newValue;
}
```

## Lifecycle

The OPC UA client hooks into the interceptor lifecycle system (see [Subject Lifecycle Tracking](tracking.md#subject-lifecycle-tracking)) to clean up resources when subjects are detached.

- When a subject is detached, monitored items in `SubscriptionManager._monitoredItems` are removed
- Polling items in `PollingManager._pollingItems` are also cleaned up
- Property data (OPC UA node IDs) associated with the subject is cleared
- OPC UA subscription items remain on the server until session ends
- Cleanup is skipped during reconnection to avoid interfering with subscription transfer

See also [Lifecycle Limitations](connectors-opcua.md#lifecycle-limitations) that apply to both client and server.

## Internal Design

### Class Dependency Graph

```
OpcUaSubjectClientSource (SubjectSourceBase: BackgroundService + ISubjectSource)
 ├── owns ReconnectionMetrics              (standalone, thread-safe counters)
 ├── owns IncomingThroughput               (standalone, ThroughputCounter)
 ├── owns OutgoingThroughput               (standalone, ThroughputCounter)
 ├── owns SubscriptionHealthMonitor        (standalone)
 ├── owns OpcUaSubjectLoader               (back-ref to source)
 ├── owns OpcUaClientDiagnostics           (back-ref to source, read-only facade)
 ├── creates SessionManager                (back-ref to source)
 │    ├── creates SubscriptionManager      (back-ref to source)
 │    │    ├── uses PollingManager
 │    │    └── uses ReadAfterWriteManager
 │    ├── creates PollingManager           (back-ref to source)
 │    └── creates ReadAfterWriteManager
 └── creates OutboundWriter
      ├── receives SessionManager
      └── receives ThroughputCounter
```

### Responsibilities

| Class | Role |
|-------|------|
| `OpcUaSubjectClientSource` | Orchestrator. Inherits `SubjectSourceBase` (which owns the pump skeleton: buffer, listen, load initial state, run change queue, retry on failure). Adds the OPC UA-specific health check loop, reconnection logic, and the `ISubjectSource` contract. |
| `SessionManager` | Manages the OPC UA session lifecycle (create, reconnect, dispose). Owns `SubscriptionManager`, `PollingManager`, and `ReadAfterWriteManager`. |
| `SubscriptionManager` | Creates and manages OPC UA subscriptions and monitored items. Routes incoming data change notifications. |
| `OutboundWriter` | Writes property changes to the OPC UA server. Tracks outgoing throughput. |
| `PollingManager` | Polling fallback for nodes that don't support subscriptions. Includes a circuit breaker. |
| `ReadAfterWriteManager` | Schedules read-backs after writes for nodes where exception-based monitoring was revised to sampling. |
| `SubscriptionHealthMonitor` | Retries failed monitored items that may succeed later (transient server errors). |
| `OpcUaSubjectLoader` | Browses the OPC UA address space and maps nodes to C# properties. Staged: queues source claims and root-level value assignments during discovery; commits atomically on success, rolls back staged subjects on failure. |
| `OpcUaClientDiagnostics` | Read-only public facade that aggregates diagnostics from all internal components. |
| `ReconnectionMetrics` | Thread-safe counters for reconnection tracking (attempts, successes, failures, abandoned). |
| `ThroughputCounter` | Lock-free 60-second sliding window rate counter for incoming/outgoing changes per second. |

### Key Design Decisions

**Single-threaded health loop.** `OpcUaSubjectClientSource` runs a single `RunHealthCheckLoopAsync` task that checks session health, triggers reconnection, and detects stalls. The loop is spawned from `StartListeningAsync` via `BackgroundTaskLifetime.Start`, so it is started and stopped together with the listener. The pump skeleton itself lives in `SubjectSourceBase`. All reconnection coordination flows through this loop.

**Back-reference pattern.** Several classes (`SessionManager`, `SubscriptionManager`, `PollingManager`) receive a reference to `OpcUaSubjectClientSource` to access shared state (metrics, throughput counters, error tracking). `OutboundWriter` demonstrates the preferred alternative: receiving only the specific dependencies it needs via constructor parameters.

**Diagnostics as a facade.** `OpcUaClientDiagnostics` navigates through `OpcUaSubjectClientSource` and `SessionManager` to expose a flat public API. It allocates `PollingDiagnostics` and `ReadAfterWriteDiagnostics` wrappers on demand to avoid exposing internal types.

**Staged load.** `OpcUaSubjectLoader` uses an `OpcUaLoadContext` (per-call, `IDisposable`) that queues source claims and root-level `SetValueFromSource` calls during discovery. On success, `Apply()` commits them atomically (claims first so observers see fully-owned leaves before root attachments). On exception, `Dispose()` walks staged subjects in reverse order and detaches them from their parent contexts, cascading through the lifecycle handler to unregister them from the registry. Combined with `SubjectSourceBase`'s retry policy, this turns transient browse failures into a clean retry rather than permanent partial state. The mechanism is unrelated to `Namotion.Interceptor.Tracking`'s `SubjectTransaction` (which captures property-change scopes for the tracking layer).
