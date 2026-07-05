# OPC UA Server

> Part of the [OPC UA integration](connectors-opcua.md). See also: [Client](connectors-opcua-client.md) | [Mapping Guide](connectors-opcua-mapping.md)

Expose your C# objects as an OPC UA server. The server creates OPC UA nodes from your properties and handles client read/write requests automatically.

## Setup

```csharp
[InterceptorSubject]
public partial class Sensor
{
    [Path("opc", "Value")]
    public partial decimal Value { get; set; }
}

builder.Services.AddSingleton(sensor);
builder.Services.AddOpcUaSubjectServer<Sensor>(
    connectorName: "opc",
    rootName: "MySensor");

// ...
sensor.Value = 42.5m;
await host.StartAsync();
```

For multiple servers, use `AddKeyedOpcUaSubjectServer` with a name and resolve via `[FromKeyedServices("name")]` (see [Diagnostics](#diagnostics)).

Two DI overloads are available with increasing control:

## Resolving the Server

After registration, resolve `IOpcUaSubjectServer` to access diagnostics, the underlying server, and variable node lookups.

**DI (unnamed registration):**

```csharp
var server = serviceProvider.GetRequiredService<IOpcUaSubjectServer>();
```

**DI (keyed registration):**

```csharp
var server = serviceProvider.GetRequiredKeyedService<IOpcUaSubjectServer>("server1");
```

For direct instantiation (without DI), `CreateOpcUaServer` returns `IOpcUaSubjectServer` directly.

## DI Overloads

**Simple generic** - resolves the subject from DI automatically (subject must be registered as a singleton):

```csharp
builder.Services.AddSingleton(machine);
builder.Services.AddOpcUaSubjectServer<Machine>(
    connectorName: "opc",
    rootName: "MyMachine");
```

**Full configuration** - complete control over all settings:

```csharp
builder.Services.AddOpcUaSubjectServer(
    subjectSelector: sp => sp.GetRequiredService<MyRoot>(),
    configurationProvider: sp => new OpcUaServerConfiguration
    {
        ValueConverter = new OpcUaValueConverter(),
        RootName = "Root",
        ApplicationName = "MyOpcUaServer",
        BaseAddress = "opc.tcp://0.0.0.0:4840/",
        NamespaceUri = "http://mycompany.com/machines/",
        BufferTime = TimeSpan.FromMilliseconds(8),
        TelemetryContext = telemetryContext
    });
```

**Parameters:**
- `connectorName` - The connector name used to match `[Path]` attributes (e.g., `"opc"` matches `[Path("opc", "Temperature")]`)
- `rootName` - Optional root folder name under the OPC UA ObjectsFolder

Multiple servers can be registered in the same DI container. Each registration uses keyed singletons internally, so they operate independently.

### Direct Instantiation

For scenarios without DI, create a server directly from a subject:

```csharp
IOpcUaSubjectServer server = subject.CreateOpcUaServer(configuration, logger);
await server.StartAsync(cancellationToken);
```

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RootName` | `string?` | null | Optional folder name under ObjectsFolder to organize server nodes |
| `ApplicationName` | `string` | "Namotion.Interceptor.Server" | Application name for identification and certificate generation |
| `BaseAddress` | `string` | "opc.tcp://localhost:4840/" | Server endpoint address |
| `NamespaceUri` | `string` | "http://namotion.com/Interceptor/" | Primary namespace URI for custom nodes |
| `ValueConverter` | `OpcUaValueConverter` | *required* | Converts between C# properties and OPC UA values |
| `Mapper` | `IPropertyMapper<OpcUaPropertyMapping>` | OpcUaCompositeMapper | Maps C# properties to OPC UA nodes (see [Mapping Guide](connectors-opcua-mapping.md)) |
| `BufferTime` | `TimeSpan?` | 8ms | Window that coalesces interceptor property changes before they are written to the OPC UA nodes. This is server-side change batching, distinct from the OPC UA publishing interval that governs when the server delivers node updates to subscribed clients (see [Publishing intervals and client negotiation](#publishing-intervals-and-client-negotiation)) |
| `TelemetryContext` | `ITelemetryContext` | NullTelemetryContext | Telemetry integration for logging and diagnostics |
| `AutoAcceptUntrustedCertificates` | `bool` | false | Accept untrusted client certificates (testing/development only) |
| `CleanCertificateStore` | `bool` | true | Remove old certificates from the application certificate store on startup |
| `CertificateStoreBasePath` | `string` | "pki" | Base directory for certificate stores. Change to isolate stores for parallel test execution |

## Security

### Security Policies

The server supports multiple security policies out of the box:

| Policy | Modes |
|--------|-------|
| Basic256Sha256 | Sign, Sign & Encrypt |
| Aes128_Sha256_RsaOaep | Sign, Sign & Encrypt |
| Aes256_Sha256_RsaPss | Sign, Sign & Encrypt |
| None | No security |

### User Authentication

Three authentication methods are supported by default:

| Method | Security Policy | Description |
|--------|----------------|-------------|
| Anonymous | None | No credentials required (default for development) |
| UserName | Basic256Sha256 | Username and password authentication |
| Certificate | Basic256Sha256 | X.509 certificate-based authentication |

To customize authentication policies or add authorization logic, override `CreateApplicationInstanceAsync()` in a derived configuration class.

### Custom Application Configuration

Override `CreateApplicationInstanceAsync()` for full control over security settings, transport quotas, and server policies:

```csharp
public class MyServerConfiguration : OpcUaServerConfiguration
{
    public override async Task<ApplicationInstance> CreateApplicationInstanceAsync()
    {
        var application = await base.CreateApplicationInstanceAsync();

        var config = application.ApplicationConfiguration;

        // Tighten security
        config.SecurityConfiguration.AutoAcceptUntrustedCertificates = false;
        config.SecurityConfiguration.MinimumCertificateKeySize = 4096;

        // Adjust transport limits
        config.TransportQuotas.MaxMessageSize = 64_000_000;

        // Customize session limits
        config.ServerConfiguration.MaxSessionCount = 50;
        config.ServerConfiguration.MaxSubscriptionCount = 200;

        return application;
    }
}
```

### Default Server Quotas

The following limits are configured by default. They are set on the SDK `ServerConfiguration`, except `MaxNodesPerRead`, `MaxNodesPerWrite`, `MaxNodesPerBrowse`, and `MaxMonitoredItemsPerCall`, which live on `ServerConfiguration.OperationLimits`. Override `CreateApplicationInstanceAsync()` to customize:

| Setting | Default | Description |
|---------|---------|-------------|
| `MaxSessionCount` | 100 | Maximum number of concurrent client sessions |
| `MinSessionTimeout` | 10s | Shortest session timeout the server will negotiate |
| `MaxSessionTimeout` | 3600s | Longest session timeout the server will negotiate; a session is dropped once the client is silent past the negotiated value |
| `MaxBrowseContinuationPoints` | 100 | Concurrent browse continuation points the server retains per session |
| `MinPublishingInterval` | 50ms | Fastest publishing interval the server grants to a subscription |
| `MaxPublishingInterval` | 3600s | Slowest publishing interval the server grants to a subscription |
| `PublishingResolution` | 25ms | Granularity to which a requested publishing interval is rounded |
| `MaxSubscriptionLifetime` | 3600s | How long a subscription survives without client keep-alive or acknowledgement |
| `MinMetadataSamplingInterval` | 1000ms | Floor for sampling of metadata nodes (does not affect regular value monitoring) |
| `MaxMessageQueueSize` | 10,000 | Publish responses retained per subscription for republish requests |
| `MaxNotificationQueueSize` | 10,000 | Notifications buffered per monitored item before the queue overflows |
| `MaxNotificationsPerPublish` | 10,000 | Maximum notifications packed into a single publish response |
| `MaxNodesPerRead` | 4,000 | Maximum nodes a single Read request may target |
| `MaxNodesPerWrite` | 4,000 | Maximum nodes a single Write request may target |
| `MaxNodesPerBrowse` | 4,000 | Maximum nodes a single Browse request may target |
| `MaxMonitoredItemsPerCall` | 4,000 | Maximum monitored items created or modified in a single call |

### Publishing intervals and client negotiation

The publishing settings above are the bounds this server uses to *revise* the publishing interval a client requests when it creates a subscription (see the client-side [subscription pipeline: sampling vs publishing](connectors-opcua-client.md#the-subscription-pipeline-sampling-vs-publishing)). A client asks for a publishing interval; the server clamps it to `[MinPublishingInterval, MaxPublishingInterval]` and rounds it to a multiple of `PublishingResolution` (25ms), then returns the revised value. `MaxSubscriptionLifetime` bounds how long a subscription survives without client acknowledgement (paired with the client's keep-alive and lifetime counts).

Because this server is backed by the interceptor model rather than a physical device, there is no per-item sampling of an external source: value changes originate from interceptor property changes (coalesced by `BufferTime`), and `MinMetadataSamplingInterval` only sets the floor for metadata sampling. Client-requested sampling intervals are still negotiated per monitored item, but the effective change rate is driven by how fast the model changes, not by server-side polling.

## Subject Deduplication

When the same C# subject instance is referenced from multiple properties in the model (for example, the same `Identification` instance reachable from both a machine root and a building-blocks folder), the server publishes it as a single OPC UA node referenced from each parent rather than creating duplicate nodes. The mapping is keyed by the registered subject identity, so reuse applies whether the property is a single reference, a collection element, or a dictionary value. See [connectors-opcua-client.md](connectors-opcua-client.md#subject-deduplication) for the symmetric client-side behavior.

### BrowseName Limitation

In OPC UA the `BrowseName` is an attribute of the target node, not of the reference pointing at it, so a node has exactly one BrowseName regardless of how many parents reference it. When the same C# subject is reused, the first property to publish it wins: the BrowseName of that property is stored on the node, and every other reference (from the same parent or another) carries the same BrowseName when clients browse it.

This is invisible for the common case where every reusing property uses the same browse name (the typical cross-parent DAG, e.g. both `MyMachine.Identification` and `MachineryBuildingBlocks.Identification` resolve to "Identification"). It is lossy when two properties reference the same instance under different browse names. The most common shape of this is two properties on the **same parent** pointing at one instance:

```csharp
[InterceptorSubject]
public partial class Root
{
    public partial SubA Primary { get; set; }
    public partial SubA Backup { get; set; }   // same instance as Primary
}
```

The server publishes a single node (named after whichever property was registered first) plus a second naked `HasComponent` reference from `Root` to that node. A round-trip client browses two references that are indistinguishable (same target NodeId, same BrowseName) and can only bind one of `Primary` / `Backup`; the other stays unset. If you need both names to round-trip, give each property its own subject instance.

The server logs a warning when it detects this case (BrowseName mismatch between the existing node and the new reference). The address space is still constructed, the warning is purely informational.

## Companion Specifications

The server automatically loads embedded NodeSets for common industrial standards:

- Opc.Ua.Di.NodeSet2.xml (Device Integration)
- Opc.Ua.PADIM.NodeSet2.xml (Process Automation Devices)
- Opc.Ua.Machinery.NodeSet2.xml (Machinery)
- Opc.Ua.Machinery.ProcessValues.NodeSet2.xml (Process values)

Reference these types with `[OpcUaNode(TypeDefinition = "...", TypeDefinitionNamespace = "...")]`.

For mapping patterns with companion specs, see [OPC UA Mapping Guide: Companion Spec Support](connectors-opcua-mapping.md#opc-ua-companion-spec-support).

### Custom Namespaces

Override `GetNamespaceUris()` to register additional namespaces:

```csharp
public class MyServerConfiguration : OpcUaServerConfiguration
{
    public override string[] GetNamespaceUris()
    {
        return [
            .. base.GetNamespaceUris(),
            "http://mycompany.com/custom-types/"
        ];
    }
}
```

### Custom NodeSets

Override `LoadPredefinedNodes()` to load additional companion specification NodeSets:

```csharp
public class MyServerConfiguration : OpcUaServerConfiguration
{
    public override void LoadPredefinedNodes(
        NodeStateCollection collection, ISystemContext context)
    {
        base.LoadPredefinedNodes(collection, context);

        // Load custom NodeSet from embedded resource
        LoadNodeSetFromEmbeddedResource<MyServerConfiguration>(
            "NodeSets.MyCompany.NodeSet2.xml", collection, context);
    }
}
```

The `LoadNodeSetFromEmbeddedResource<T>()` helper loads NodeSet XML files embedded in the assembly of type `T`. The resource name follows the pattern `{AssemblyName}.{ResourcePath}`.

## Diagnostics

`IOpcUaSubjectServer.Diagnostics` exposes a live facade. Resolve it once and poll (see [Resolving the Server](#resolving-the-server)).

Properties: `IsRunning`, `ActiveSessionCount`, `StartTime`, `Uptime`, `LastError`, `ConsecutiveFailures` (resets on successful start, see [Resilience](#resilience)), `IncomingChangesPerSecond` (client writes to server, 60-second sliding window), `OutgoingChangesPerSecond` (subject changes pushed to OPC UA nodes, 60-second sliding window).

## Direct Server Access

For scenarios the connector does not cover natively (custom node managers, server events, custom session handlers), `IOpcUaSubjectServer.CurrentServer` exposes the underlying `StandardServer` (see [Resolving the Server](#resolving-the-server) for how to obtain the server):

```csharp
if (server.CurrentServer is { } current)
{
    // ... advanced interactions on `current`
}
```

**Lifecycle contract:** read `CurrentServer` immediately before each use. It is `null` when the server is not running (startup, between restart attempts, or after a force-kill), and the instance is recreated on every restart. Never cache the reference.

## Variable Node Resolution

`IOpcUaSubjectServer.TryGetVariableNode` resolves the OPC UA `BaseDataVariableState` created for a tracked property. This is useful for raising server-side events or performing advanced operations on a specific node:

```csharp
if (server.TryGetVariableNode(sensor.GetPropertyReference(nameof(Sensor.Value)), out var variable))
{
    // Use variable for direct OPC UA node operations
}
```

Returns `false` if the property is not exposed by this server, not yet created, or was removed during a server restart.

## Resilience

The server automatically restarts on failure using exponential backoff with jitter:

| Failure # | Base Delay | Jitter |
|-----------|-----------|--------|
| 1 | 1s | 0-2s |
| 2 | 2s | 0-2s |
| 3 | 4s | 0-2s |
| 4 | 8s | 0-2s |
| 5 | 16s | 0-2s |
| 6+ | 30s (cap) | 0-2s |

The consecutive failure counter resets when the server starts successfully. Track failure count via `Diagnostics.ConsecutiveFailures`.

## Lifecycle

The OPC UA server hooks into the interceptor lifecycle system (see [Subject Lifecycle Tracking](tracking.md#subject-lifecycle-tracking)) to clean up resources when subjects are detached.

- When a subject is detached, its entries in the node manager are removed
- The OPC UA node remains in the address space until server restart (OPC UA SDK limitation)
- Local tracking is cleaned up immediately to prevent memory leaks

See also [Lifecycle Limitations](connectors-opcua.md#lifecycle-limitations) that apply to both client and server.
