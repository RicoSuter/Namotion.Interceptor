# OPC UA

The `Namotion.Interceptor.OpcUa` package provides integration between Namotion.Interceptor and OPC UA (Open Platform Communications Unified Architecture), enabling bidirectional synchronization between C# objects and industrial automation systems. It supports both client and server modes.

- [OPC UA Client](connectors-opcua-client.md) - Configuration, authentication, monitoring, resilience, extensibility
- [OPC UA Server](connectors-opcua-server.md) - Configuration, security, companion specs, diagnostics
- [OPC UA Mapping](connectors-opcua-mapping.md) - Attributes, fluent configuration, companion spec patterns

## Key Features

- Bidirectional synchronization between C# objects and OPC UA nodes
- Dynamic property discovery from OPC UA servers
- Attribute-based mapping for precise control
- Subscription-based real-time updates
- Arrays, nested objects, and collections support

## Quick Start

### Client

Connect to an OPC UA server by configuring a client with `AddOpcUaSubjectClientSource`:

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

var machine = serviceProvider.GetRequiredService<Machine>();
await host.StartAsync();
Console.WriteLine(machine.Temperature); // Synchronized with OPC UA server
machine.Speed = 100; // Writes to OPC UA server
```

See [OPC UA Client](connectors-opcua-client.md) for configuration, authentication, monitoring, resilience, and extensibility.

### Server

Expose your C# objects as an OPC UA server with `AddOpcUaSubjectServer`:

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

See [OPC UA Server](connectors-opcua-server.md) for configuration, security, companion specifications, and diagnostics.

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

For comprehensive mapping documentation including companion spec support, VariableTypes, and fluent configuration, see [OPC UA Mapping Guide](connectors-opcua-mapping.md).

### Node Mapper

Both client and server configurations include a `NodeMapper` property (`IOpcUaNodeMapper`) that controls how C# properties map to OPC UA nodes. The default is a `CompositeNodeMapper` combining `PathProviderOpcUaNodeMapper` (maps `[Path]` attributes) and `AttributeOpcUaNodeMapper` (maps `[OpcUaNode]` attributes). For custom mapping strategies including fluent configuration and composite mappers, see [OPC UA Mapping Guide](connectors-opcua-mapping.md).

## Type Conversions

OPC UA doesn't natively support C# `decimal`, so automatic conversion is applied:

- `decimal` <-> `double`
- `decimal[]` <-> `double[]`

All other C# primitive types map directly to OPC UA built-in types. For custom conversions, extend `OpcUaValueConverter` (see [Client Extensibility](connectors-opcua-client.md#custom-value-converter)).

## Performance

The library includes optimizations:

- Batched read/write operations respecting server limits
- Object pooling for change buffers
- Fast data change callbacks bypassing caches
- Buffered updates batching rapid changes

## Benchmark Results

Intel(R) Core(TM) Ultra 7 258V

```
Server Benchmark - 1 minute - [2026-02-18 22:08:35.641]

Total received changes:          1182669
Total published changes:         1196000
Process memory:                  645.51 MB (434.04 MB in .NET heap)
Avg allocations over last 60s:   525.09 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19707.66   19691.68   20604.91   20891.21   25145.73   25145.73   25145.73   14275.83    1155.00          -
Processing latency (ms)             0.05       0.00       0.02       0.07       0.46       8.97     174.59       0.00       0.51    1182669
End-to-end latency (ms)            36.62      28.49      53.71      74.94     239.75     308.09     328.73       0.37      37.57    1182669
```

```
Client Benchmark - 1 minute - [2026-02-18 22:08:38.466]

Total received changes:          1195101
Total published changes:         1181200
Process memory:                  475.24 MB (247.02 MB in .NET heap)
Avg allocations over last 60s:   30.13 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19930.38   19932.58   20715.65   20858.03   21507.32   21507.32   21507.32   16980.51     669.75          -
Processing latency (ms)             1.96       0.86       1.80       2.22      36.11      40.20      41.56       0.00       5.54    1195101
End-to-end latency (ms)            57.50      52.40      87.15     100.72     154.92     238.92     288.00       1.70      27.17    1195101
```
