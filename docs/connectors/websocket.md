# WebSocket

The `Namotion.Interceptor.WebSocket` package provides bidirectional WebSocket communication for synchronizing `SubjectUpdate` data between .NET servers and clients. It's optimized for industrial, digital twin, and IoT scenarios.

## Key Features

- Bidirectional synchronization between server and clients
- JSON serialization (extensible for MessagePack in future)
- Hello/Welcome handshake with initial state delivery
- Automatic reconnection with exponential backoff
- Write retry queue for resilience during disconnection
- Multiple client support with broadcast updates

## Choosing a Server Mode

The package offers two server modes with identical performance (both use Kestrel):

| Mode | Method | Best For |
|------|--------|----------|
| **Standalone** | `AddWebSocketSubjectServer` | Dedicated sync servers, edge nodes, SCADA systems, console apps |
| **Embedded** | `AddWebSocketSubjectHandler` + `MapWebSocketSubject` | Adding sync to existing ASP.NET apps (API + WebSocket on same port) |

**Use standalone mode** when WebSocket sync is the primary purpose of your application. It creates a dedicated Kestrel server with minimal overhead.

**Use embedded mode** when you already have an ASP.NET application (with controllers, Blazor, etc.) and want to add WebSocket sync without running a second server.

## Server Setup (Standalone)

Creates a dedicated WebSocket server on its own port. Best for edge nodes, industrial gateways, and dedicated sync services.

```csharp
[InterceptorSubject]
public partial class Device
{
    public partial string Status { get; set; }
    public partial decimal Temperature { get; set; }
}

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithLifecycle()
    .WithHostedServices(builder.Services);

var device = new Device(context);
builder.Services.AddSingleton(device);
builder.Services.AddWebSocketSubjectServer<Device>(configuration =>
{
    configuration.Port = 8080;
});

var host = builder.Build();
host.Run();
// Server listens on ws://localhost:8080/ws
```

## Server Setup (Embedded)

Adds WebSocket sync to an existing ASP.NET application. Best when you already have a web app and want to add real-time sync.

```csharp
var builder = WebApplication.CreateBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithLifecycle()
    .WithHostedServices(builder.Services);

var device = new Device(context);
builder.Services.AddSingleton(device);
builder.Services.AddWebSocketSubjectHandler<Device>();

var app = builder.Build();

// Your existing middleware
app.MapControllers();
app.MapBlazorHub();

// Add WebSocket sync endpoint
app.UseWebSockets();
app.MapWebSocketSubject("/ws");

app.Run();
// WebSocket available alongside your existing endpoints
```

## Client Setup

Connect to a WebSocket server as a subscriber with `AddWebSocketSubjectClientSource`. The client automatically connects, performs the handshake, receives initial state, and synchronizes property changes bidirectionally.

```csharp
var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithContextInheritance()
    .WithHostedServices(builder.Services);

var device = new Device(context);
builder.Services.AddSingleton(device);
builder.Services.AddWebSocketSubjectClientSource<Device>(configuration =>
{
    configuration.ServerUri = new Uri("ws://localhost:8080/ws");
});

var host = builder.Build();

// Register IServiceProvider so DefaultSubjectFactory can create subjects with context
context.AddService(host.Services);

host.Run();
// Client receives initial state and ongoing updates from server
// Local property changes are sent to server
```

## Configuration

### Server Configuration

```csharp
builder.Services.AddWebSocketSubjectServer<Device>(configuration =>
{
    // Network settings
    configuration.Port = 8080;              // Default: 8080
    configuration.Path = "/ws";             // Default: "/ws"
    configuration.BindAddress = "127.0.0.1"; // Default: null (all interfaces)

    // Performance tuning
    configuration.BufferTime = TimeSpan.FromMilliseconds(8);  // Batch updates
    configuration.WriteBatchSize = 1000;    // Max properties per message

    // Path mapping
    configuration.PathProvider = new AttributeBasedPathProvider("ws");

    // Subject creation for client updates
    configuration.SubjectFactory = new DefaultSubjectFactory();

    // Update processing
    configuration.Processors = new ISubjectUpdateProcessor[] { /* custom processors */ };
});
```

### Client Configuration

```csharp
builder.Services.AddWebSocketSubjectClientSource<Device>(configuration =>
{
    // Connection
    configuration.ServerUri = new Uri("ws://localhost:8080/ws");  // Required
    configuration.ConnectTimeout = TimeSpan.FromSeconds(30);      // Default: 30s

    // Reconnection settings
    configuration.ReconnectDelay = TimeSpan.FromSeconds(5);       // Initial delay
    configuration.MaxReconnectDelay = TimeSpan.FromSeconds(60);   // Exponential backoff cap

    // Performance tuning
    configuration.BufferTime = TimeSpan.FromMilliseconds(8);      // Batch updates
    configuration.WriteBatchSize = 1000;    // Max properties per message

    // Write retry queue
    configuration.RetryTime = TimeSpan.FromSeconds(10);           // Retry interval
    configuration.WriteRetryQueueSize = 1000;                     // Buffer size (0 to disable)

    // Path mapping
    configuration.PathProvider = new AttributeBasedPathProvider("ws");

    // Subject creation for server updates
    configuration.SubjectFactory = new DefaultSubjectFactory();

    // Update processing
    configuration.Processors = new ISubjectUpdateProcessor[] { /* custom processors */ };
});
```

## Protocol

The WebSocket protocol uses a simple message envelope for all communication.

### Message Envelope

All messages use the same structure:

```
[MessageType, CorrelationId, Payload]
```

- `MessageType`: Integer discriminator (0-3)
- `CorrelationId`: Integer or null (reserved for future request/response pairing)
- `Payload`: Type-specific JSON payload

### Message Types

| Type | Value | Direction | Description |
|------|-------|-----------|-------------|
| Hello | 0 | Client -> Server | Client initiates connection |
| Welcome | 1 | Server -> Client | Server responds with initial state |
| Update | 2 | Bidirectional | Property changes |
| Error | 3 | Bidirectional | Error notification |

### Connection Sequence

```
Client                                 Server
   |                                      |
   |-------- WebSocket Connect ---------->|
   |                                      |
   |-------- Hello ---------------------->|
   |  [0, null, {version:1}]              |
   |                                      |
   |<------- Welcome ---------------------|
   |  [1, null, {version:1, format:"json", state: SubjectUpdate}]
   |                                      |
   |  (client applies initial state)      |
   |                                      |
   |<------- Update ----------------------|  (server pushes changes)
   |  [2, null, SubjectUpdate]            |
   |                                      |
   |-------- Update --------------------->|  (client writes changes)
   |  [2, null, SubjectUpdate]            |
```

### Payload Structures

**HelloPayload**
```json
{
  "version": 1,
  "format": "json"
}
```

**WelcomePayload**
```json
{
  "version": 1,
  "format": "json",
  "state": { /* Complete SubjectUpdate */ }
}
```

**ErrorPayload**
```json
{
  "code": 100,
  "message": "Property not found",
  "failures": [
    { "path": "Motor/Speed", "code": 101, "message": "Read-only" }
  ]
}
```

### Error Codes

| Code | Name | Description |
|------|------|-------------|
| 100 | UnknownProperty | Property not found |
| 101 | ReadOnlyProperty | Cannot write to read-only property |
| 102 | ValidationFailed | Validation error |
| 200 | InvalidFormat | Malformed message |
| 201 | VersionMismatch | Protocol version not supported |
| 500 | InternalError | Server error |

### SubjectUpdate Wire Format

See [Subject Updates](subject-updates.md) for details on the update format.

## Resilience

### Write Retry Queue

The client automatically queues write operations when disconnected. Queued writes are flushed in FIFO order when the connection is restored.

```csharp
configuration.WriteRetryQueueSize = 1000;  // Buffer up to 1000 writes
configuration.RetryTime = TimeSpan.FromSeconds(10);
```

- Ring buffer semantics: drops oldest when full
- Automatic flush after reconnection
- Set to 0 to disable

### Reconnection

The client uses exponential backoff for reconnection:

```csharp
configuration.ReconnectDelay = TimeSpan.FromSeconds(5);      // Initial delay
configuration.MaxReconnectDelay = TimeSpan.FromSeconds(60);  // Maximum delay
```

On reconnection:
1. Wait for reconnect delay (with exponential backoff)
2. Reconnect and perform Hello/Welcome handshake
3. Flush pending writes from retry queue
4. Apply fresh initial state from Welcome
5. Resume normal operation

### Error Handling

Tiered error handling preserves connections when possible:

| Error | Response | Connection |
|-------|----------|------------|
| Unknown property | Log warning, send Error | Stays open |
| Read-only property | Send Error | Stays open |
| Validation failed | Send Error | Stays open |
| Malformed JSON | - | Disconnect |
| Version mismatch | Send Error in close frame | Disconnect |

## Thread Safety

The library ensures thread-safe operations:
- Property operations are thread-safe
- Multiple clients can connect and receive broadcasts concurrently
- Write operations use atomic updates

## Target Use Cases

- **Industrial / SCADA**: High reliability, structured data, audit trails
- **Digital Twin**: Multiple nodes co-authoring state, high update frequency
- **IoT / Home Automation**: Many devices, frequent small updates, occasional disconnections

## Topology Support

- **Single server, many clients**: Primary use case
- **Hierarchical**: Edge servers aggregate local devices, sync upstream to central server
- **Peer-to-peer mesh**: Future extensibility

## Future Extensibility

The protocol is designed for future enhancements:

- **MessagePack support**: `format` field in Hello/Welcome enables negotiation for 3-4x smaller payloads
- **Commands/RPC**: Message types 4-5 reserved for invoking methods on subjects
- **Subscriptions**: Message types 6-7 reserved for subscribing to specific subjects/properties
