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

var registration = builder.Services.AddOpcUaSubjectClientSource<Machine>(
    serverUrl: "opc.tcp://plc.factory.com:4840",
    sourceName: "opc",
    rootName: "MyMachine");

// Use in application
var machine = serviceProvider.GetRequiredService<Machine>();
await host.StartAsync();
Console.WriteLine(machine.Temperature); // Read property which is synchronized with OPC UA server
machine.Speed = 100; // Writes to OPC UA server
```

All `AddOpcUaSubjectClientSource` overloads return an `OpcUaClientRegistration` handle for accessing the client instance and diagnostics later (see [Diagnostics](#diagnostics)).

**Parameters:**
- `serverUrl` - The OPC UA server endpoint (e.g., `"opc.tcp://localhost:4840"`)
- `sourceName` - The connector name used to match `[Path]` attributes (e.g., `"opc"` matches `[Path("opc", "Temperature")]`)
- `rootName` - Optional root node name to start browsing from under the Objects folder

Three DI overloads are available: the simple generic shown above, one with a custom subject selector, and a full configuration overload (shown below).

## Configuration

For advanced scenarios, use the full configuration API to customize connection behavior, subscription settings, and dynamic property discovery.

```csharp
var registration = builder.Services.AddOpcUaSubjectClientSource(
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
        MaximumItemsPerSubscription = 1000,

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
| `NodeMapper` | CompositeNodeMapper | Maps C# properties to OPC UA nodes (see [Mapping Guide](connectors-opcua-mapping.md)) |

**Subscription Tuning:**

| Property | Default | Description |
|----------|---------|-------------|
| `DefaultPublishingInterval` | 0 | Publishing interval in ms (0 = server default) |
| `SubscriptionKeepAliveCount` | 10 | Keep-alive count for subscriptions |
| `SubscriptionLifetimeCount` | 100 | Lifetime count (must be >= 3x keep-alive count) |
| `SubscriptionPriority` | 0 | Subscription priority (0 = server default) |
| `SubscriptionMaximumNotificationsPerPublish` | 0 | Max notifications per publish (0 = server default) |
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
| `MaximumReferencesPerNode` | 0 | Max references per browse request (0 = server default) |

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

The library automatically queues write operations when the connection is lost, preventing data loss during brief network interruptions. On reconnection, queued writes are optimistically re-applied: after loading the server's current state, each queued change is compared against the current property value and only re-applied if the server hasn't changed it (source wins on conflict). This feature is provided by the `SubjectSourceBackgroundService` (see [Connectors — Write Retry Queue](connectors.md#write-retry-queue)).

```csharp
builder.Services.AddOpcUaSubjectClientSource(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://plc.factory.com:4840",
        WriteRetryQueueSize = 1000 // Buffer up to 1000 writes (default)
    });

// Writes are automatically queued during disconnection
machine.Speed = 100; // Queued if disconnected, written immediately if connected
```

**Configuration:**
- `WriteRetryQueueSize`: Maximum writes to buffer (default: 1000, set to 0 to disable)
- Ring buffer semantics: drops oldest when full, keeps latest values
- Optimistic re-apply after reconnection (source wins on conflict)

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

## Write Error Handling

When a batch write to the OPC UA server partially fails, the client throws an `OpcUaWriteException`. The exception distinguishes between transient failures (connectivity issues, timeouts that may succeed on retry) and permanent failures (invalid nodes, access denied; should not be retried). The write retry queue (see [Resilience](#write-retry-queue-during-disconnection)) handles transient failures automatically during disconnection, but writes that fail while connected surface this exception.

## Diagnostics

Access client diagnostics through the `IOpcUaSubjectClientSource` interface. When using DI, call `Resolve` on the registration handle returned by `AddOpcUaSubjectClientSource`:

```csharp
var registration = builder.Services.AddOpcUaSubjectClientSource<Machine>(
    serverUrl: "opc.tcp://plc.factory.com:4840",
    sourceName: "opc");

// After building the host:
IOpcUaSubjectClientSource source = registration.Resolve(serviceProvider);
var diagnostics = source.Diagnostics;
```

When using direct instantiation, the return value is already the interface:

```csharp
IOpcUaSubjectClientSource source = subject.CreateOpcUaClientSource(configuration, logger);
var diagnostics = source.Diagnostics;
```

The diagnostics object is a live facade. Resolve it once and poll its properties repeatedly. Categories available:

- **Connection**: whether connected, whether reconnecting, session ID, last connected timestamp
- **Subscriptions**: active subscription count, total monitored items
- **Reconnection history**: total attempts, successes, and failures
- **Polling fallback**: item count, read success/failure counts, value changes detected, slow polls, and circuit breaker state (see [Polling Fallback](#polling-fallback-for-unsupported-nodes))
- **Read-after-write**: scheduled, executed, coalesced, and failed counts (see [Read After Write Fallback](#read-after-write-fallback))

All diagnostics properties are thread-safe for reading.

## Direct Session Access

For scenarios the connector does not cover natively, such as calling OPC UA **Methods** (operator commands, recipe execution, custom server-side procedures) or subscribing to **Alarms & Conditions** events, `IOpcUaSubjectClientSource` exposes the underlying `ISession`:

```csharp
if (source.CurrentSession is ISession session)
{
    var outputs = await session.CallAsync(
        objectId: parentNodeId,
        methodId: methodNodeId,
        inputArguments: new[] { /* ... */ },
        cancellationToken);
}
```

There are two ways to get the source:

**1. Via the registration handle** (recommended for application-level wiring):

```csharp
ISession? session = registration
   .Resolve(serviceProvider)
   .CurrentSession;
```

**2. From any property via `TryGetSource`** (useful when reaching a session from deep inside business code that holds a property reference but not the registration handle):

```csharp
if (property.TryGetSource(out var subjectSource) &&
    subjectSource is IOpcUaSubjectClientSource source)
{
    ISession? session = source.CurrentSession;
    // ... use session
}
```

This works because `OpcUaSubjectClientSource` implements both `ISubjectSource` (returned by `TryGetSource`) and `IOpcUaSubjectClientSource`. The pattern lets any code path that already has a `PropertyReference` reach the OPC UA session without plumbing the registration through.

**Lifecycle contract (read carefully):**

- `CurrentSession` can return `null` at any time during reconnection. Always null-check.
- The underlying `ISession` instance changes after a manual reconnect (Flow C), after a transferred-subscription failure (Flow B), or after stall detection forces a reset. Never cache the reference; never hold long-lived state keyed on a specific session instance.
- Read `CurrentSession` immediately before each use. A reference captured a few seconds ago may already point to a disposed session.
- For long-running operations (e.g. an A&C subscription you create on the session), be prepared to re-create your subscription after a session swap. Either subscribe to `CurrentSessionChanged` (see below) or recreate on demand when calls fail with `BadSessionIdInvalid` / `BadSessionNotActivated`.

### Reacting to session swaps with `CurrentSessionChanged`

For consumers that hold session-bound state (typically Alarms & Conditions subscriptions), the connector raises `CurrentSessionChanged` on every transition of the underlying session, including transitions to and from `null` during reconnection. The event arguments carry both the previous session and the current one, so handlers can tear down state bound to the old session before recreating it on the new one.

```csharp
opcUaSource.CurrentSessionChanged += (_, args) =>
{
    if (args.PreviousSession is not null)
    {
        // Synchronous local cleanup on the previous session (drop refs, unsubscribe handlers).
        // Transport is still open here, but it closes immediately after this handler returns.
        DisposeMyAlarmsSubscription(args.PreviousSession);
    }

    if (args.CurrentSession is not null)
    {
        // Recreating an A&C subscription is async — fire-and-forget so we do not stall reconnection.
        var newSession = args.CurrentSession;
        _ = Task.Run(async () =>
        {
            try { await RecreateMyAlarmsSubscriptionAsync(newSession); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to recreate A&C subscription"); }
        });
    }
};
```

Method-call consumers (e.g. invoking an OPC UA Method on demand) typically do not need this event: they read `CurrentSession` immediately before each call and tolerate transient nulls. The event is intended for consumers that have no inbound traffic that would otherwise surface a stale session as a failure.

Handler guidelines:

- Handlers run synchronously on the connector's own thread, often from inside the reconnection lock. Keep them fast and non-blocking; a slow handler stalls reconnection.
- `PreviousSession`'s transport is still open during the handler and closes after the handler returns. Use it only for synchronous local cleanup; do not start new network operations on it.
- For async work on `CurrentSession` (e.g. recreating a subscription) use fire-and-forget (`_ = Task.Run(...)`) and tolerate the session being swapped again before the task completes — the next `CurrentSessionChanged` event will surface the new state.
- Do not call back into blocking connector methods from a handler.
- The connector wraps the invocation in a try/catch so a throwing handler cannot break its own state, but per standard .NET event semantics a throwing subscriber will skip later subscribers in the invocation list. If you have multiple subscribers that must all run, isolate exceptions in your own handler.

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
