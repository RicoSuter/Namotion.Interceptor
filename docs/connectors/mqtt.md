# MQTT

The `Namotion.Interceptor.Mqtt` package provides integration between Namotion.Interceptor and MQTT (Message Queuing Telemetry Transport), enabling bidirectional synchronization between C# objects and MQTT brokers. It supports both client and server modes.

## Key Features

- Bidirectional synchronization between C# objects and MQTT topics
- Attribute-based mapping for property-to-topic relationships
- JSON serialization by default (customizable)
- Retained messages for initial state delivery
- QoS support for reliable message delivery
- Automatic reconnection with circuit breaker pattern

## Client Setup

Connect to an MQTT broker as a subscriber with `AddMqttSubjectClient`. The client automatically establishes connections, subscribes to topics, and synchronizes values with your C# properties.

```csharp
[InterceptorSubject]
public partial class Sensor
{
    [Path("mqtt", "Temperature")]
    public partial decimal Temperature { get; set; }

    [Path("mqtt", "Humidity")]
    public partial decimal Humidity { get; set; }
}

builder.Services.AddMqttSubjectClient<Sensor>(
    brokerHost: "mqtt.example.com",
    sourceName: "mqtt",
    brokerPort: 1883,
    topicPrefix: "sensors/room1");

// Use in application
var sensor = serviceProvider.GetRequiredService<Sensor>();
await host.StartAsync();
Console.WriteLine(sensor.Temperature); // Read property synchronized with MQTT broker
sensor.Temperature = 25.5m; // Publishes to MQTT broker
```

## Server Setup

Host an embedded MQTT broker that publishes property changes with `AddMqttSubjectServer`. External MQTT clients can subscribe to receive updates when properties change.

```csharp
[InterceptorSubject]
public partial class Device
{
    [Path("mqtt", "Status")]
    public partial string Status { get; set; }
}

builder.Services.AddMqttSubjectServer<Device>(
    brokerHost: "localhost",
    sourceName: "mqtt",
    brokerPort: 1883,
    topicPrefix: "devices/mydevice");

var device = serviceProvider.GetRequiredService<Device>();
device.Status = "Online"; // Published to all connected MQTT clients
await host.StartAsync();
```

## Configuration

### Client Configuration

For advanced scenarios, use the full configuration API to customize connection behavior and MQTT settings.

```csharp
builder.Services.AddMqttSubjectClient(
    subjectSelector: sp => sp.GetRequiredService<Sensor>(),
    configurationProvider: sp => new MqttClientConfiguration
    {
        BrokerHost = "mqtt.example.com",
        BrokerPort = 1883,
        PathProvider = new AttributeBasedPathProvider("mqtt", "/"),

        // Authentication
        Username = "user",
        Password = "password",
        UseTls = true,

        // Connection settings
        ClientId = "my-client-id",
        CleanSession = true,
        TopicPrefix = "sensors",
        ConnectTimeout = TimeSpan.FromSeconds(10),
        KeepAliveInterval = TimeSpan.FromSeconds(15),

        // QoS settings
        DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
        UseRetainedMessages = true,

        // Reconnection settings
        ReconnectDelay = TimeSpan.FromSeconds(2),
        MaximumReconnectDelay = TimeSpan.FromSeconds(60),
        HealthCheckInterval = TimeSpan.FromSeconds(30),
        ReconnectStallThreshold = 10,

        // Circuit breaker
        CircuitBreakerFailureThreshold = 5,
        CircuitBreakerCooldown = TimeSpan.FromSeconds(60),

        // Performance tuning
        BufferTime = TimeSpan.FromMilliseconds(8),
        RetryTime = TimeSpan.FromSeconds(10),
        WriteRetryQueueSize = 1000,

        // Serialization
        ValueConverter = new JsonMqttValueConverter(),
        SourceTimestampPropertyName = "ts"
    });
```

### Server Configuration

The server configuration controls the embedded MQTT broker behavior.

```csharp
builder.Services.AddMqttSubjectServer(
    subjectSelector: sp => sp.GetRequiredService<Device>(),
    configurationProvider: sp => new MqttServerConfiguration
    {
        BrokerHost = "127.0.0.1", // Optional: bind to specific interface (default: all interfaces)
        BrokerPort = 1883,
        PathProvider = new AttributeBasedPathProvider("mqtt", "/"),

        // Connection settings
        ClientId = "my-server-id",
        TopicPrefix = "devices",

        // QoS settings
        DefaultQualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
        UseRetainedMessages = true,
        MaxPendingMessagesPerClient = 25000,

        // Initial state
        InitialStateDelay = TimeSpan.FromMilliseconds(500),

        // Performance tuning
        BufferTime = TimeSpan.FromMilliseconds(8),

        // Serialization
        ValueConverter = new JsonMqttValueConverter(),
        SourceTimestampPropertyName = "ts"
    });
```

## Topic Mapping

Properties are mapped to MQTT topics using the `[Path]` attribute. The topic is constructed by combining the optional `TopicPrefix` with the path specified in the attribute.

```csharp
[InterceptorSubject]
public partial class Sensor
{
    // With TopicPrefix = "home/living-room":
    // Topic: home/living-room/Temperature
    [Path("mqtt", "Temperature")]
    public partial decimal Temperature { get; set; }

    // Nested paths supported:
    // Topic: home/living-room/metrics/Humidity
    [Path("mqtt", "metrics/Humidity")]
    public partial decimal Humidity { get; set; }
}
```

## Serialization

By default, values are serialized as JSON. Custom value converters can be implemented for different serialization formats.

```csharp
public class CustomMqttValueConverter : IMqttValueConverter
{
    public byte[] Serialize(object? value, Type type)
    {
        // Custom serialization logic
        return Encoding.UTF8.GetBytes(value?.ToString() ?? "");
    }

    public object? Deserialize(ReadOnlySequence<byte> payload, Type type)
    {
        // Custom deserialization logic
        var str = Encoding.UTF8.GetString(payload);
        return Convert.ChangeType(str, type);
    }
}
```

## Resilience Features

### Write Retry Queue

The client automatically queues write operations when the connection is lost. Queued writes are flushed in FIFO order when the connection is restored.

```csharp
new MqttClientConfiguration
{
    WriteRetryQueueSize = 1000 // Buffer up to 1000 writes (default)
}
```

- Ring buffer semantics: drops oldest when full
- Automatic flush after reconnection
- Set to 0 to disable

### Circuit Breaker

Prevents resource exhaustion during prolonged outages by pausing reconnection attempts.

```csharp
new MqttClientConfiguration
{
    CircuitBreakerFailureThreshold = 5, // Open after 5 consecutive failures
    CircuitBreakerCooldown = TimeSpan.FromSeconds(60) // Wait before retrying
}
```

### Reconnect Stall Detection

Detects hung reconnection attempts and forces a reset to allow recovery.

```csharp
new MqttClientConfiguration
{
    ReconnectStallThreshold = 10, // 10 health check iterations
    HealthCheckInterval = TimeSpan.FromSeconds(30) // 5 minute timeout
}
```

## Thread Safety

The library ensures thread-safe operations across all MQTT interactions:
- Property operations are thread-safe
- Subscription callbacks use concurrent queues
- Cache operations use `ConcurrentDictionary`

## Lifecycle Management

The MQTT integration hooks into the interceptor lifecycle system (see [Subject Lifecycle Tracking](../tracking.md#subject-lifecycle-tracking)) to clean up resources when subjects are detached.

### Automatic Cleanup on Subject Detach

**Client behavior:**
- Cache entries in `_topicToProperty` and `_propertyToTopic` are removed for detached subjects
- Cleanup happens both proactively (on detach event) and lazily (when stale entries are accessed)

**Server behavior:**
- Cache entries in `_propertyToTopic` and `_pathToProperty` are removed for detached subjects
- Same dual cleanup strategy as client

**Race condition prevention:**
- Cache lookups validate subject attachment AFTER cache access
- Stale entries are detected and removed even if the detach event already fired
- This ensures we never return stale data even with concurrent attach/detach

## Performance

The library includes optimizations:

- Batched message publishing with configurable buffer time
- Object pooling for change buffers and user property lists
- Fast path for retained message handling
- Efficient topic-to-property caching with lazy cleanup

## Benchmark Results

MQTTnet has currently serious performance issues:

Intel(R) Core(TM) Ultra 7 258V

```
Server Benchmark - 1 minute - [2026-02-18 22:15:10.262]

Total received changes:          206800
Total published changes:         1196400
Process memory:                  329.44 MB (161.17 MB in .NET heap)
Avg allocations over last 60s:   86.49 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)             3451.56    3519.77    3768.97    3815.79    3861.35    3861.35    3861.35    2057.52     314.58          -
Processing latency (ms)             0.04       0.01       0.03       0.05       0.47       1.38      24.47       0.00       0.39     206800
End-to-end latency (ms)          3468.57    3510.97    5277.64    5530.69    5888.33    6458.14    6555.45    1058.94    1331.77     206800
```

```
Client Benchmark - 1 minute - [2026-02-18 22:15:13.630]

Total received changes:          1402251
Total published changes:         1183600
Process memory:                  549.1 MB (314.15 MB in .NET heap)
Avg allocations over last 60s:   63.86 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            23375.11   23411.68   24244.79   24403.03   24940.71   24940.71   24940.71   20190.70     817.26          -
Processing latency (ms)             0.06       0.01       0.04       0.26       0.85       2.12      38.53       0.00       0.55    1402251
End-to-end latency (ms)           544.87      42.58    2581.02    4165.70    5476.68    6132.41    6682.53       4.70    1315.95    1402251
```
