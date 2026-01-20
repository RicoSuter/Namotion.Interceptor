# OPC UA

The `Namotion.Interceptor.OpcUa` package provides integration between Namotion.Interceptor and OPC UA (Open Platform Communications Unified Architecture), enabling bidirectional synchronization between C# objects and industrial automation systems. It supports both client and server modes.

## Key Features

- Bidirectional synchronization between C# objects and OPC UA nodes
- Dynamic property discovery from OPC UA servers
- Attribute-based mapping for precise control
- Subscription-based real-time updates
- Arrays, nested objects, and collections support

## Client Setup

Connect to an OPC UA server by configuring a client with `AddOpcUaSubjectClient`. The client automatically establishes connections, subscribes to node changes, and synchronizes values with your C# properties.

```csharp
[InterceptorSubject]
public partial class Machine
{
    [Path("opc", "Temperature")]
    public partial decimal Temperature { get; set; }

    [Path("opc", "Speed")]
    public partial decimal Speed { get; set; }
}

builder.Services.AddOpcUaSubjectClient<Machine>(
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
builder.Services.AddOpcUaSubjectClient(
    subjectSelector: sp => sp.GetRequiredService<MyRoot>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://localhost:4840",
        PathProvider = new AttributeBasedPathProvider("opc", ".", null),
        TypeResolver = new OpcUaTypeResolver(logger),
        ValueConverter = new OpcUaValueConverter(),
        SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),

        // Optional
        RootName = "Machines",
        DefaultNamespaceUri = "http://factory.com/machines",
        ApplicationName = "MyOpcUaClient",
        ReconnectInterval = TimeSpan.FromSeconds(5),

        // Subscription settings
        DefaultSamplingInterval = 100,
        DefaultPublishingInterval = 100,
        DefaultQueueSize = 10,
        MaximumItemsPerSubscription = 1000,

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

### Server Configuration

The server configuration requires minimal setup with source path provider and value converter. Additional options control the application name, namespace URI, certificate management, and performance tuning.

```csharp
builder.Services.AddOpcUaSubjectServer(
    subjectSelector: sp => sp.GetRequiredService<MyRoot>(),
    configurationProvider: sp => new OpcUaServerConfiguration
    {
        PathProvider = new AttributeBasedPathProvider("opc", ".", null),
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

## Attributes

### OpcUaNodeAttribute

Use `[OpcUaNode]` to map properties to specific OPC UA nodes with precise control over browse names, node identifiers, and subscription behavior. This attribute allows you to specify exact node IDs, custom sampling intervals, and queue settings per property.

```csharp
[InterceptorSubject]
public partial class Device
{
    [OpcUaNode("Temperature")]
    public partial double Temperature { get; set; }

    [OpcUaNode("Pressure", "http://factory.com",
        NodeIdentifier = "ns=2;s=Sensor.Pressure",
        NodeNamespaceUri = "http://factory.com",
        SamplingInterval = 50,
        QueueSize = 5)]
    public partial double Pressure { get; set; }
}
```

### OpcUaTypeDefinitionAttribute

Apply `[OpcUaTypeDefinition]` to classes to use standard OPC UA companion specification types like Device Integration (DI) or Machinery types. This ensures your objects conform to industry-standard OPC UA information models.

```csharp
[InterceptorSubject]
[OpcUaTypeDefinition("FunctionalGroupType", "http://opcfoundation.org/UA/DI/")]
public partial class DeviceGroup
{
    public partial string Name { get; set; }
}
```

### OpcUaNodeReferenceTypeAttribute

Control how properties are linked in the OPC UA address space by specifying the reference type. This determines the semantic relationship between parent and child nodes.

```csharp
[OpcUaNodeReferenceType("Organizes")]
[Path("opc", "Machines")]
public partial Machine[] Machines { get; set; }
```

Common types: `HasComponent`, `Organizes`, `HasProperty`

### OpcUaNodeItemReferenceTypeAttribute

For collection properties, specify how individual items are referenced in the OPC UA address space. This controls the reference type used for each element in arrays or collections.

```csharp
[OpcUaNodeItemReferenceType("HasComponent")]
[Path("opc", "Parameters")]
public partial Parameter[] Parameters { get; set; }
```

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

## Performance

The library includes optimizations:

- Batched read/write operations respecting server limits
- Object pooling for change buffers
- Fast data change callbacks bypassing caches
- Buffered updates batching rapid changes

## Companion Specifications

The server automatically loads embedded NodeSets:

- Opc.Ua.Di.NodeSet2.xml (Device Integration)
- Opc.Ua.PADIM.NodeSet2.xml (Process Automation Devices)
- Opc.Ua.Machinery.NodeSet2.xml (Machinery)
- Opc.Ua.Machinery.ProcessValues.NodeSet2.xml (Process values)

Reference these types with `[OpcUaTypeDefinition]`.

## Resilience Features

### Write Retry Queue During Disconnection

The library automatically queues write operations when the connection is lost, preventing data loss during brief network interruptions. Queued writes are flushed in FIFO order when the connection is restored. This feature is provided by the `SubjectSourceBackgroundService`.

```csharp
builder.Services.AddOpcUaSubjectClient(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://plc.factory.com:4840",
        PathProvider = new AttributeBasedPathProvider("opc", ".", null),
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
builder.Services.AddOpcUaSubjectClient(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://plc.factory.com:4840",
        PathProvider = new AttributeBasedPathProvider("opc", ".", null),
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
builder.Services.AddOpcUaSubjectClient(
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    configurationProvider: sp => new OpcUaClientConfiguration
    {
        ServerUrl = "opc.tcp://plc.factory.com:4840",
        PathProvider = new AttributeBasedPathProvider("opc", ".", null),
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

When the SDK's reconnection handler gets stuck (e.g., server never responds), the client automatically detects the stall and forces a reconnection reset. Configure via `StallDetectionIterations` (default: 10) and `SubscriptionHealthCheckInterval` (default: 5s). Total stall timeout = iterations × interval (default: 50 seconds).

## Diagnostics

Monitor client and server health in production via the `Diagnostics` property on `OpcUaSubjectClientSource` and `OpcUaSubjectServerBackgroundService`.

**Client diagnostics** (`OpcUaClientDiagnostics`): Connection state, session ID, subscription/monitored item counts, reconnection metrics, polling statistics.

**Server diagnostics** (`OpcUaServerDiagnostics`): Running state, active session count, start time, consecutive failures, last error.

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

## Security Considerations

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

## Resilience Configuration

For 24/7 production use, the default configuration provides robust resilience:

| Setting | Default | Description |
|---------|---------|-------------|
| `KeepAliveInterval` | 5s | How quickly disconnections are detected |
| `ReconnectInterval` | 5s | Time between reconnection attempts |
| `StallDetectionIterations` | 10 | Health checks before forcing stall reset |
| `WriteRetryQueueSize` | 1000 | Updates buffered during disconnection |
| `SessionDisposalTimeout` | 5s | Max wait for graceful session close |
| `SubscriptionSequentialPublishing` | false | Process subscription messages in order (see Thread Safety) |

Total stall recovery time = `StallDetectionIterations` × `SubscriptionHealthCheckInterval` (default: 50s).

## Lifecycle Management

### Automatic Cleanup on Subject Detach

When subjects are detached from the object graph (removed from collections, set to null, etc.), the OPC UA client and server automatically clean up their internal tracking structures to prevent memory leaks.

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

**Thread safety:**
- Cleanup handlers are invoked inside the lifecycle interceptor's lock
- Handlers use `ConcurrentDictionary.TryRemove` for safe concurrent modification
- Event handlers are designed to be fast and exception-free

**What this does NOT do:**
- Does NOT dynamically add new subjects to OPC UA after initialization
- Does NOT update the OPC UA address space when subjects are attached
- New subjects added after startup require a restart to appear in OPC UA

This minimal lifecycle integration prevents memory leaks in long-running services with dynamic object graphs.

## Benchmark Results

```
Server Benchmark - 1 minute - [2026-01-20 22:29:45.260]

Total received changes:          1189867
Total published changes:         1196800
Process memory:                  628.24 MB (419.63 MB in .NET heap)
Avg allocations over last 60s:   502.47 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19817.96   19967.97   20763.99   21555.02   22729.66   22729.66   22729.66   14266.49    1099.08          -
Processing latency (ms)             0.06       0.00       0.02       0.09       0.65      10.08      19.22       0.00       0.61    1189867
End-to-end latency (ms)            57.24      28.27     172.11     230.22     322.76     378.85     392.92       0.46      70.20    1189867

```

```
Client Benchmark - 1 minute - [2026-01-20 22:29:52.755]

Total received changes:          1195948
Total published changes:         1189174
Process memory:                  506.01 MB (255.02 MB in .NET heap)
Avg allocations over last 60s:   27.98 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19938.96   20008.40   20726.40   21121.12   23081.15   23081.15   23081.15   14879.78    1027.90          -
Processing latency (ms)             1.89       1.00       2.21       2.80      27.21      43.55     136.39       0.00       5.15    1195948
End-to-end latency (ms)            68.39      56.34     114.76     163.69     255.27     423.46     449.37       2.08      47.60    1195948
```
