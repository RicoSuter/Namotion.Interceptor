# WebSocket

The `Namotion.Interceptor.WebSocket` package provides bidirectional WebSocket communication for synchronizing subject graphs between .NET servers and clients. It's optimized for industrial, digital twin, and IoT scenarios.

## Key Features

- Bidirectional synchronization between server and clients
- JSON serialization (extensible for MessagePack in future)
- Hello/Welcome handshake with initial state delivery
- Sequence numbers on server-to-client messages for gap detection
- Periodic heartbeat messages for liveness checking
- Automatic reconnection with exponential backoff
- Write retry queue for resilience during disconnection
- Multiple client support with broadcast updates

## Choosing a Server Mode

The package offers two server modes with identical performance (both use Kestrel):

| Mode | Method | Best For |
|------|--------|----------|
| **Standalone** | `AddWebSocketSubjectServer` | Dedicated sync servers, edge nodes, SCADA systems, console apps |
| **Embedded** | `AddWebSocketSubjectHandler` + `MapWebSocketSubjectHandler` | Adding sync to existing ASP.NET apps (API + WebSocket on same port) |

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

> See [Hosting](../hosting.md) for details on `WithHostedServices()`.

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
builder.Services.AddWebSocketSubjectHandler<Device>("/ws");

var app = builder.Build();

// Your existing middleware
app.MapControllers();
app.MapBlazorHub();

// Add WebSocket sync endpoint
app.UseWebSockets();
app.MapWebSocketSubjectHandler("/ws");

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

    // Connection limits
    configuration.MaxConnections = 1000;    // Default: 1000
    configuration.MaxMessageSize = 10 * 1024 * 1024;  // Default: 10 MB
    configuration.HelloTimeout = TimeSpan.FromSeconds(10);  // Default: 10s

    // Heartbeat / sequence numbers
    configuration.HeartbeatInterval = TimeSpan.FromSeconds(30);  // Default: 30s (0 to disable)

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
    configuration.ReceiveTimeout = TimeSpan.FromSeconds(60);      // Default: 60s
    configuration.MaxMessageSize = 10 * 1024 * 1024;             // Default: 10 MB

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
[MessageType, Payload]
```

- `MessageType`: Integer discriminator (0-4)
- `Payload`: Type-specific JSON payload

### Message Types

| Type | Value | Direction | Description |
|------|-------|-----------|-------------|
| Hello | 0 | Client -> Server | Client initiates connection |
| Welcome | 1 | Server -> Client | Server responds with initial state |
| Update | 2 | Bidirectional | Property changes |
| Error | 3 | Bidirectional | Error notification |
| Heartbeat | 4 | Server -> Client | Periodic liveness check with current sequence |

### Connection Sequence

```
Client                                 Server
   |                                      |
   |-------- WebSocket Connect ---------->|
   |                                      |
   |-------- Hello ---------------------->|
   |  [0, {version:1}]                    |
   |                                      |
   |              (server registers connection for broadcasts)
   |              (server reads current sequence under update lock)
   |              (server builds snapshot under update lock)
   |                                      |
   |<------- Welcome ---------------------|
   |  [1, {version:1, format:"json", state: SubjectUpdate, sequence: 5}]
   |                                      |
   |  (client sets expectedNext = 6)      |
   |                                      |
   |<------- Update ----------------------|  (queued broadcasts flushed after Welcome)
   |  [2, {sequence:6, root:..., subjects:...}]
   |                                      |
   |  (client verifies sequence==6, sets expectedNext=7)
   |  (client applies snapshot,           |
   |   then replays buffered updates)     |
   |                                      |
   |<------- Update ----------------------|  (server pushes changes)
   |  [2, {sequence:7, root:..., subjects:...}]
   |                                      |
   |-------- Update --------------------->|  (client writes changes, no sequence)
   |  [2, {root:..., subjects:...}]       |
   |                                      |
   |<------- Heartbeat -------------------|  (periodic, every 30s by default)
   |  [4, {sequence: 7}]                  |
   |                                      |
   |  (client checks: 7 < 8 → in sync)   |
```

#### Register-Before-Welcome Design

The server registers the connection for broadcasts **before** building and sending the Welcome snapshot. This follows the buffer-flush-load-replay pattern (see [Connectors](../connectors.md)) and ensures eventual consistency:

1. **Register**: Connection is added to the broadcast list. Any concurrent property changes are **queued per-connection** (not sent yet). The client does not receive any messages until the Welcome is sent.
2. **Snapshot**: The server builds the complete state snapshot under `_applyUpdateLock`, the same lock used when applying client updates. This ensures the snapshot is a consistent cut: every update applied before the lock is included, every update applied after will be sent as a separate Update message.
3. **Welcome**: The snapshot is sent to the client. Immediately after (under the same send lock), any queued updates are flushed. The client always sees Welcome as the first message, followed by any updates that occurred during snapshot build.
4. **Buffer replay**: The client applies the snapshot as a baseline, then replays all buffered updates (received between connection and snapshot application) to catch up to current state.

The snapshot does not need to be fully up-to-date — it is just a baseline. The buffered updates are what guarantee correctness. After replay, the client is fully caught up and subsequent updates flow directly.

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
  "state": { /* Complete SubjectUpdate */ },
  "sequence": 5
}
```

- `sequence`: Server's current sequence number at snapshot time. Clients initialize their expected next sequence to `sequence + 1`.

**HeartbeatPayload**
```json
{
  "sequence": 42
}
```

- `sequence`: Server's current sequence number (last broadcast batch). Does **not** increment the counter — it reflects the current value.

Example wire format: `[4, {"sequence": 42}]`

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

The client uses exponential backoff with jitter for reconnection:

```csharp
configuration.ReconnectDelay = TimeSpan.FromSeconds(5);      // Initial delay
configuration.MaxReconnectDelay = TimeSpan.FromSeconds(60);  // Maximum delay
```

On reconnection, the client follows the buffer-flush-load-replay pattern:

1. **Buffer**: Start buffering inbound updates (via `SubjectPropertyWriter`)
2. **Reconnect**: Connect and perform Hello/Welcome handshake (server registers connection, builds snapshot, sends Welcome)
3. **Flush**: Flush pending writes from the retry queue to the server
4. **Load**: Apply the Welcome snapshot as a baseline
5. **Replay**: Replay all buffered updates received since reconnection (catches up to current state)
6. **Resume**: Switch to direct update application (buffer mode off)

This ensures:
- No updates are lost during the reconnection window
- The snapshot provides a consistent baseline
- Buffered updates (including echoed retry queue changes) bring the state to current
- Client and server converge to the same state (eventual consistency)

> **Note**: The retry queue echo (server broadcasting the client's flushed changes back) may arrive during or shortly after the replay phase. If it arrives during replay, it is included in the buffer. If it arrives after, it is applied immediately. Either way, the state converges within one round trip.

### Sequence Numbers and Gap Detection

The server maintains a monotonically increasing sequence counter that is incremented atomically (`Interlocked.Increment`) each time an update batch is broadcast. This enables clients to detect lost updates.

**Server behavior:**
- Each `BroadcastUpdateAsync` call increments the counter and sets the sequence in the `UpdatePayload` (e.g., `[2, {sequence:7, root:..., subjects:...}]`). The sequence is carried in `UpdatePayload`, which inherits from `SubjectUpdate`.
- The Welcome payload includes the current sequence at snapshot time. Clients initialize their expected next sequence to `welcome.sequence + 1`.
- Heartbeat messages include the current sequence in their payload but do **not** increment it.

**Client behavior:**
- On receiving an Update: the client reads the sequence from the `UpdatePayload`. If `sequence != expectedNextSequence`, the client logs a warning and exits the receive loop, triggering reconnection via the existing recovery flow.
- On receiving a Heartbeat: if `heartbeat.sequence >= expectedNextSequence`, the server has sent updates the client never received. The client exits the receive loop and reconnects.
- A heartbeat with `sequence < expectedNextSequence` means the client is fully caught up — no action needed.
- A null or zero sequence is treated as "unassigned" for client-to-server messages which do not carry sequence numbers.

**Recovery flow on gap detection:**
Gap detected -> receive loop exits -> `ExecuteAsync` detects connection lost -> `StartBuffering` -> exponential backoff delay -> `ConnectAsync` -> Welcome with full state + new sequence -> `CompleteInitializationAsync` (buffer-load-replay). No new recovery logic is needed; the existing reconnection flow handles everything.

**Why only server-to-client messages carry sequence numbers:**
Client-to-server writes are covered by the write retry queue (ring buffer, oldest-dropped-when-full) and flush-before-load on reconnection. The server applies updates synchronously under a lock, so silent drops within the server are impossible.

### Heartbeat

The server periodically sends Heartbeat messages to all connected clients. This allows clients to detect lost updates even during quiet periods (no property changes).

```csharp
configuration.HeartbeatInterval = TimeSpan.FromSeconds(30);  // Default
configuration.HeartbeatInterval = TimeSpan.Zero;              // Disable heartbeats
```

- The heartbeat loop runs as a parallel task alongside the change queue processor.
- If a transient send failure occurs during heartbeat broadcast, the error is logged and the loop continues.
- Zombie connections (repeated send failures) are cleaned up during heartbeat broadcast, using the same logic as update broadcasts.

### Echo Behavior

The server broadcasts every update to **all** connected clients, including the client that sent the change. This is intentional:

- **Sequence number consistency**: Every client must see the same monotonic sequence progression. Skipping an update for the originator would create a gap, triggering a false reconnection.
- **Implicit acknowledgment**: The echo acts as a server-side ACK that the client's update was applied.
- **No correctness issue**: The client applies inbound updates with `SubjectChangeContext.WithSource(this)`, so echoed values are deduplicated by the change tracking layer and do not trigger outbound writes or loops.

### Conflict Resolution

The system uses **last-write-wins (LWW)** at the server. If two clients modify the same property simultaneously, the last update to reach the server wins and is broadcast to all clients.

- All clients and the server converge to the same value after updates propagate (eventual consistency).
- No vector clocks, version stamps, or merge logic needed.
- Acceptable for the target use cases (IoT, industrial automation, UI binding) where properties represent current state rather than accumulated operations.

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

**Server side**: Incoming updates from multiple clients are applied in serialized order. Each message is fully applied before the next one starts, ensuring no interleaving of property writes from different clients. Individual property writes use last-write-wins semantics. Multiple clients can connect and receive broadcasts concurrently.

**Client side**: Updates received from the server are not serialized with local property changes. If the application writes to a property while the client is applying an incoming server update, the two may race. In practice this is rarely an issue because property ownership is typically split (the server owns some properties, the client owns others).

## Lifecycle Management

Unlike MQTT and OPC UA connectors which maintain per-property topic/node caches that require cleanup on subject detach (see [Subject Lifecycle Tracking](../tracking.md#subject-lifecycle-tracking)), the WebSocket connector synchronizes the entire subject graph as a unit. There are no per-property caches to clean up — the server builds a fresh snapshot for each new client connection, and broadcast updates are derived from the change tracking layer. Connection-level resources (WebSocket, send lock, cancellation tokens) are cleaned up when a client disconnects or the server stops.

## Target Use Cases

- **Industrial / SCADA**: High reliability, structured data, audit trails
- **Digital Twin**: Multiple nodes co-authoring state, high update frequency
- **IoT / Home Automation**: Many devices, frequent small updates, occasional disconnections

## Topology Support

- **Single server, many clients**: Primary use case
- **Hierarchical**: Edge servers aggregate local devices, sync upstream to central server
- **Peer-to-peer mesh**: Future extensibility

## Known Limitations

- **Snapshot lock during client connection**: When a new client connects, the server builds a full state snapshot under the same lock used for applying updates. This blocks incoming updates for the duration of the snapshot, which is proportional to graph size. This is acceptable because new-client connections are infrequent relative to the update rate, but could become a concern with very large subject graphs and frequent client reconnections.

- **Broadcast timeout**: A slow client can delay broadcast completion for other clients. Broadcasts have a 10-second timeout to mitigate this — sends that haven't completed continue in the background, and zombie detection cleans up persistently slow connections. However, very slow clients may still cause temporary backpressure before being removed. This should be revisited if it becomes a bottleneck in high-throughput scenarios.

## Future Extensibility

The protocol is designed for future enhancements:

- **MessagePack support**: `format` field in Hello/Welcome enables negotiation for 3-4x smaller payloads
- **Commands/RPC**: Message types 5-6 reserved for invoking methods on subjects
- **Subscriptions**: Message types 7-8 reserved for subscribing to specific subjects/properties
- **Message compression**: Per-message or per-frame compression to reduce bandwidth
- **Authentication/authorization hooks**: Token-based auth during handshake or per-message access control
- **Diagnostic counters**: Connection metrics, reconnection attempts, message throughput tracking

## Performance

The library includes optimizations:

- Batched outbound updates with configurable `BufferTime` to reduce per-message overhead
- `RecyclableMemoryStream` and `ArrayPool<byte>` pooling for read/write buffers
- Per-connection queuing during Welcome handshake to avoid blocking broadcasts
- Configurable `WriteBatchSize` to cap message size and control serialization latency

## Benchmark Results

```
Server Benchmark - 1 minute - [2026-02-08 00:43:54.182]

Total received changes:          1199599
Total published changes:         1199600
Process memory:                  339.89 MB (193.69 MB in .NET heap)
Avg allocations over last 60s:   77.16 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19986.61   19977.31   20264.37   20358.98   20395.47   20395.47   20395.47   19284.34     241.02          -
End-to-end latency (ms)            19.84      17.97      27.82      30.58      35.96      55.13      63.49       0.54       5.52    1199599
```

```
Client Benchmark - 1 minute - [2026-02-08 00:43:49.966]

Total received changes:          1199619
Total published changes:         1199600
Process memory:                  324.68 MB (167.66 MB in .NET heap)
Avg allocations over last 60s:   70.98 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19992.14   19962.97   20394.59   20527.46   20592.19   20592.19   20592.19   19413.39     294.82          -
End-to-end latency (ms)            21.05      18.98      29.77      32.76      42.28      54.79      64.70       0.70       6.53    1199619
```
