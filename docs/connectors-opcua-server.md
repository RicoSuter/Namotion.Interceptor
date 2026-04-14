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

builder.Services.AddOpcUaSubjectServer<Sensor>(
    sourceName: "opc",
    pathPrefix: null,
    rootName: "MySensor");

var sensor = serviceProvider.GetRequiredService<Sensor>();
sensor.Value = 42.5m;
await host.StartAsync();
```

Three DI overloads are available with increasing control:

**Simple generic** - resolves the subject from DI automatically:

```csharp
builder.Services.AddOpcUaSubjectServer<Machine>(
    sourceName: "opc",
    pathPrefix: null,
    rootName: "MyMachine");
```

**With subject selector** - custom subject resolution:

```csharp
builder.Services.AddOpcUaSubjectServer(
    sourceName: "opc",
    subjectSelector: sp => sp.GetRequiredService<Machine>(),
    pathPrefix: null,
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
- `sourceName` - The connector name used to match `[Path]` attributes (e.g., `"opc"` matches `[Path("opc", "Temperature")]`)
- `pathPrefix` - Optional prefix prepended to property paths when mapping to OPC UA nodes. Use `null` for no prefix.
- `rootName` - Optional root folder name under the OPC UA ObjectsFolder

Multiple servers can be registered in the same DI container. Each registration uses keyed singletons internally, so they operate independently.

### Direct Instantiation

For scenarios without DI, create a server directly from a subject:

```csharp
var server = subject.CreateOpcUaServer(configuration, logger);
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
| `NodeMapper` | `IOpcUaNodeMapper` | CompositeNodeMapper | Maps C# properties to OPC UA nodes (see [Mapping Guide](connectors-opcua-mapping.md)) |
| `BufferTime` | `TimeSpan?` | 8ms | Time window to buffer incoming property changes before publishing to clients |
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

The following limits are configured by default. Override `CreateApplicationInstanceAsync()` to customize:

| Setting | Default |
|---------|---------|
| Max sessions | 100 |
| Session timeout | 10s - 3600s |
| Max browse continuation points | 100 |
| Min publishing interval | 50ms |
| Max publishing interval | 3600s |
| Max message queue size | 10,000 |
| Max notification queue size | 10,000 |
| Max notifications per publish | 10,000 |
| MaxNodesPerRead | 4,000 |
| MaxNodesPerWrite | 4,000 |
| MaxNodesPerBrowse | 4,000 |
| MaxMonitoredItemsPerCall | 4,000 |

## Companion Specifications

The server automatically loads embedded NodeSets for common industrial standards:

- Opc.Ua.Di.NodeSet2.xml (Device Integration)
- Opc.Ua.PADIM.NodeSet2.xml (Process Automation Devices)
- Opc.Ua.Machinery.NodeSet2.xml (Machinery)
- Opc.Ua.Machinery.ProcessValues.NodeSet2.xml (Process values)

Reference these types with `[OpcUaNode(TypeDefinition = "...", TypeDefinitionNamespace = "...")]`.

For mapping patterns with companion specs, see [OPC UA Mapping Guide -- Companion Spec Support](connectors-opcua-mapping.md#opc-ua-companion-spec-support).

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

The server runs as a [hosted service](hosting.md). Monitor its health in production via the `Diagnostics` property on `OpcUaSubjectServerBackgroundService`.

| Property | Type | Description |
|----------|------|-------------|
| `IsRunning` | `bool` | Whether the server is accepting connections |
| `ActiveSessionCount` | `int` | Number of currently connected clients |
| `StartTime` | `DateTimeOffset?` | When the server started (null if not running) |
| `Uptime` | `TimeSpan?` | Time since server started (null if not running) |
| `LastError` | `Exception?` | Most recent error (null if no errors) |
| `ConsecutiveFailures` | `int` | Number of consecutive startup failures (resets on success) |

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

**Limitations:**
- Does NOT dynamically add new subjects to OPC UA after initialization
- Does NOT update the OPC UA address space when subjects are attached
- New subjects added after startup require a restart to appear in OPC UA
