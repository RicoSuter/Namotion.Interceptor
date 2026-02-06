# OPC UA

The `Namotion.Interceptor.OpcUa` package provides integration between Namotion.Interceptor and OPC UA (Open Platform Communications Unified Architecture), enabling bidirectional synchronization between C# objects and industrial automation systems. It supports both client and server modes.

## Key Features

- Bidirectional synchronization between C# objects and OPC UA nodes
- Dynamic property discovery from OPC UA servers
- Attribute-based mapping for precise control
- Subscription-based real-time updates
- Arrays, nested objects, and collections support

## Client Setup

Connect to an OPC UA server by configuring a client with `AddOpcUaSubjectClientSource`. The client automatically establishes connections, subscribes to node changes, and synchronizes values with your C# properties.

```csharp
[InterceptorSubject]
public partial class Machine
{
    [Path("opc", "Temperature")]
    public partial decimal Temperature { get; set; }

    [Path("opc", "Speed")]
    public partial decimal Speed { get; set; }
}

builder.Services.AddOpcUaSubjectClientSource<Machine>(
    serverUrl: "opc.tcp://plc.factory.com:4840",
    sourceName: "opc",
    pathPrefix: null,
    rootName: "MyMachine");

// Use in application
var machine = serviceProvider.GetRequiredService<Machine>();
await host.StartAsync();
Console.WriteLine(machine.Temperature); // Read property which is synchronized with OPC UA server
machine.Speed = 100; // Writes to OPC UA server
```

## Server Setup

Expose your C# objects as an OPC UA server by configuring with `AddOpcUaSubjectServer`. The server creates OPC UA nodes from your properties and handles client read/write requests automatically.

```csharp
[InterceptorSubject]
public partial class Sensor
{
    [Path("opc", "Value")]
    public partial decimal Value { get; set; }
}

builder.Services.AddOpcUaSubjectServer<Sensor>(
    sourceName: "opc",
    pathPrefix: null,
    rootName: "MySensor");

var sensor = serviceProvider.GetRequiredService<Sensor>();
sensor.Value = 42.5m;
await host.StartAsync();
```

## Configuration

### Client Configuration

For advanced scenarios, use the full configuration API to customize connection behavior, subscription settings, and dynamic property discovery. The required settings include the server URL and infrastructure components, while optional settings allow fine-tuning of reconnection delays, sampling intervals, and performance parameters.

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

### Server Configuration

The server configuration requires minimal setup with value converter. Additional options control the application name, namespace URI, certificate management, and performance tuning.

```csharp
builder.Services.AddOpcUaSubjectServer(
    subjectSelector: sp => sp.GetRequiredService<MyRoot>(),
    configurationProvider: sp => new OpcUaServerConfiguration
    {
        ValueConverter = new OpcUaValueConverter(),

        // Optional
        RootName = "Root",
        ApplicationName = "MyOpcUaServer",
        NamespaceUri = "http://namotion.com/Interceptor/",
        CleanCertificateStore = true,
        BufferTime = TimeSpan.FromMilliseconds(8)
    });
```

The server automatically configures security policies, authentication, operation limits (MaxNodesPerRead/Write=4000), and companion specification namespaces.

## Property Mapping

Map C# properties to OPC UA nodes using attributes. For simple cases, use `[Path]`. For advanced OPC UA-specific configuration, use `[OpcUaNode]` and related attributes.

```csharp
[InterceptorSubject]
[OpcUaNode(TypeDefinition = "MachineType")]  // Class-level type definition
public partial class Machine
{
    // Simple property mapping
    [Path("opc", "Status")]
    public partial int Status { get; set; }

    // OPC UA-specific mapping with monitoring config
    [OpcUaNode("Temperature", SamplingInterval = 100, DeadbandType = DeadbandType.Absolute, DeadbandValue = 0.5)]
    public partial double Temperature { get; set; }

    // Child object with HasComponent reference
    [OpcUaReference("HasComponent")]
    [OpcUaNode(BrowseName = "MainMotor")]
    public partial Motor? Motor { get; set; }
}
```

For comprehensive mapping documentation including companion spec support, VariableTypes, and fluent configuration, see [OPC UA Mapping Guide](opcua-mapping.md).

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

For **discrete variables**, missing a transition can cause system failures. A classic example is the handshake pattern: client writes `1`, PLC acknowledges by writing `0`. If sampling misses the `1→0` transition (because both samples see `0`), the client never knows the PLC acknowledged.

OPC UA's sampling-based monitoring compares each sample against the *previous sample*, not against what the client knows. When a value changes faster than the sampling rate (e.g., `0→1→0` between samples), the server sees `0` at both sample points and reports no change.

```
Sampling-based (SamplingInterval = 100ms):
t=0ms:   Sample value=0, baseline=0 → no change reported
t=50ms:  Client writes 1 → value=1 (no sample happens)
t=60ms:  PLC writes 0 → value=0 (no sample happens)
t=100ms: Sample value=0, baseline=0 → no change reported ❌
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

## Type Conversions

OPC UA doesn't natively support C# `decimal`, so automatic conversion is applied:

- `decimal` ↔ `double`
- `decimal[]` ↔ `double[]`

All other C# primitive types map directly to OPC UA built-in types.

## Extensibility

### Custom Value Converter

Extend `OpcUaValueConverter` to implement custom type conversions between OPC UA and C# types. Override `ConvertToPropertyValue` for OPC UA → C# conversions and `ConvertToNodeValue` for C# → OPC UA conversions.

```csharp
public class CustomValueConverter : OpcUaValueConverter
{
    public override object? ConvertToPropertyValue(
        object? nodeValue, RegisteredSubjectProperty property)
    {
        if (property.Type == typeof(MyCustomType) && nodeValue is string str)
            return MyCustomType.Parse(str);
        return base.ConvertToPropertyValue(nodeValue, property);
    }

    public override object? ConvertToNodeValue(
        object? propertyValue, RegisteredSubjectProperty property)
    {
        if (propertyValue is MyCustomType custom)
            return custom.ToString();
        return base.ConvertToNodeValue(propertyValue, property);
    }
}
```

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

When the client discovers OPC UA nodes not defined in your C# model, they can be added as dynamic properties at runtime. Access these properties through the registry after the client completes its initial load.

```csharp
var registered = machine.TryGetRegisteredSubject();
var dynamicProperty = registered.Properties.FirstOrDefault(p => p.Name == "UnknownSensor");
if (dynamicProperty != null)
{
    var value = dynamicProperty.Reference.GetValue();
}
```

## Companion Specifications

The server automatically loads embedded NodeSets:

- Opc.Ua.Di.NodeSet2.xml (Device Integration)
- Opc.Ua.PADIM.NodeSet2.xml (Process Automation Devices)
- Opc.Ua.Machinery.NodeSet2.xml (Machinery)
- Opc.Ua.Machinery.ProcessValues.NodeSet2.xml (Process values)

Reference these types with `[OpcUaNode(TypeDefinition = "...", TypeDefinitionNamespace = "...")]`.

For mapping patterns with companion specs, see [OPC UA Mapping Guide — Companion Spec Support](opcua-mapping.md#opc-ua-companion-spec-support).

## Resilience

### Write Retry Queue During Disconnection

The library automatically queues write operations when the connection is lost, preventing data loss during brief network interruptions. Queued writes are flushed in FIFO order when the connection is restored. This feature is provided by the `SubjectSourceBackgroundService`.

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
- Automatic flush after reconnection

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

### Stall Detection

When the SDK's reconnection handler gets stuck (e.g., server never responds), the client automatically detects the stall and forces a reconnection reset. Configure via `MaxReconnectDuration` (default: 30 seconds). If the SDK reconnection hasn't succeeded within this duration, a full session reset and manual reconnection is triggered.

### Resilience Configuration

For 24/7 production use, the default configuration provides robust resilience:

| Setting | Default | Description |
|---------|---------|-------------|
| `KeepAliveInterval` | 5s | How quickly disconnections are detected |
| `ReconnectInterval` | 5s | Time between reconnection attempts |
| `MaxReconnectDuration` | 30s | Max time to wait for SDK reconnection before forcing reset |
| `WriteRetryQueueSize` | 1000 | Updates buffered during disconnection |
| `SessionDisposalTimeout` | 5s | Max wait for graceful session close |
| `SubscriptionSequentialPublishing` | false | Process subscription messages in order (see Thread Safety) |

Stall recovery is triggered after `MaxReconnectDuration` (default: 30s) if SDK reconnection hasn't succeeded.

## Diagnostics

Monitor client and server health in production via the `Diagnostics` property on `OpcUaSubjectClientSource` and `OpcUaSubjectServerBackgroundService`.

**Client diagnostics** (`OpcUaClientDiagnostics`): Connection state, session ID, subscription/monitored item counts, reconnection metrics, polling statistics.

**Server diagnostics** (`OpcUaServerDiagnostics`): Running state, active session count, start time, consecutive failures, last error.

## Security

**Client security:**
```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://plc.factory.com:4840",
    UseSecurity = true,  // Enable signing and encryption (recommended for production)
    // ... other settings
};
```

When `UseSecurity = true`, the client prefers secure endpoints with message signing and encryption. The default is `false` for development convenience.

**Server security:**
By default, the server accepts anonymous connections without encryption. For production deployments requiring authentication:
- Configure custom `UserTokenPolicies` in a derived `OpcUaServerConfiguration`
- Use certificate-based authentication
- Enable message signing and encryption

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

## Lifecycle Management

The OPC UA integration hooks into the interceptor lifecycle system (see [Subject Lifecycle Tracking](../tracking.md#subject-lifecycle-tracking)) to clean up resources when subjects are detached.

### Automatic Cleanup on Subject Detach

**Server behavior:**
- When a subject is detached, its corresponding entries in `CustomNodeManager._subjects` are removed
- The OPC UA node remains in the address space until server restart (OPC UA SDK limitation)
- Local tracking is cleaned up immediately to prevent memory leaks

**Client behavior:**
- When a subject is detached, monitored items in `SubscriptionManager._monitoredItems` are removed
- Polling items in `PollingManager._pollingItems` are also cleaned up
- Property data (OPC UA node IDs) associated with the subject is cleared
- OPC UA subscription items remain on the server until session ends
- Cleanup is skipped during reconnection to avoid interfering with subscription transfer

**What this does NOT do:**
- Does NOT dynamically add new subjects to OPC UA after initialization
- Does NOT update the OPC UA address space when subjects are attached
- New subjects added after startup require a restart to appear in OPC UA

## Performance

The library includes optimizations:

- Batched read/write operations respecting server limits
- Object pooling for change buffers
- Fast data change callbacks bypassing caches
- Buffered updates batching rapid changes
