# WebSocket

The `Namotion.Interceptor.WebSocket` package provides bidirectional WebSocket communication for synchronizing subject graphs between .NET servers and clients. It's optimized for industrial, digital twin, and IoT scenarios.

## Key Features

- Bidirectional synchronization between server and clients
- JSON serialization (extensible for MessagePack in future)
- Hello/Welcome handshake with initial state delivery
- Sequence numbers on messages in both directions for gap detection and recovery
- State-digest on heartbeats for quiescent-consistency verification
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

> See [Hosting](hosting.md) for details on `WithHostedServices()`.

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

    // Broadcast
    configuration.BroadcastTimeout = TimeSpan.FromSeconds(10);  // Default: 10s

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

    // Circuit breaker
    configuration.CircuitBreakerFailureThreshold = 5;             // Open after 5 consecutive failures
    configuration.CircuitBreakerCooldown = TimeSpan.FromSeconds(60); // Wait before retrying

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

- `MessageType`: Integer discriminator (0-6)
- `Payload`: Type-specific JSON payload

### Message Types

| Type | Value | Direction | Description |
|------|-------|-----------|-------------|
| Hello | 0 | Client -> Server | Client initiates connection |
| Welcome | 1 | Server -> Client | Server responds with initial state |
| Update | 2 | Bidirectional | Property changes |
| Error | 3 | Bidirectional | Error notification |
| Heartbeat | 4 | Server -> Client | Periodic liveness check with current sequence |
| Resync | 5 | Server -> Client | Requests a client to resend its complete owned state after a client-to-server sequence gap |
| ClientHeartbeat | 6 | Client -> Server | Reports the client's last-sent sequence during idle for trailing-gap detection |

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
   |-------- Update --------------------->|  (client writes changes, monotonic per-connection sequence)
   |  [2, {sequence:1, root:..., subjects:...}]  |
   |                                      |
   |<------- Heartbeat -------------------|  (periodic, server-idle-gated)
   |  [4, {sequence: 7, digest: "a3f..."}]|
   |                                      |
   |  (client checks sequence: 7 < 8 → in sync; compares digest)
   |-------- ClientHeartbeat ------------>|  (client echoes its last-sent sequence + digest)
   |  [6, {lastSentSequence: 1, digest: "a3f..."}]|
```

#### Register-Before-Welcome Design

The server registers the connection for broadcasts **before** building and sending the Welcome snapshot. This follows the buffer-flush-load-replay pattern (see [Connectors](connectors.md)) and ensures eventual consistency:

1. **Register**: Connection is added to the broadcast list. Any concurrent property changes are **queued per-connection** along with their sequence numbers (not sent yet). The client does not receive any messages until the Welcome is sent.
2. **Snapshot**: The server builds the complete state snapshot under `_applyUpdateLock`, the same lock used when applying client updates. This ensures the snapshot is a consistent cut: every update applied before the lock is included, every update applied after will be sent as a separate Update message.
3. **Welcome**: The snapshot is sent to the client with the current sequence number. Immediately after (under the same send lock), queued updates are flushed — but only those with a sequence **greater than** the Welcome sequence are sent, since the snapshot already includes all earlier changes. After Welcome, any further broadcasts whose sequence is ≤ the Welcome sequence are also skipped. The client always sees Welcome as the first message, followed by only the updates that were not yet included in the snapshot.
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
  "sequence": 42,
  "digest": "a3f8c2..."
}
```

- `sequence`: Server's current sequence number (last broadcast batch). Does **not** increment the counter — it reflects the current value.
- `digest`: On-demand state digest of the server's live graph. Carried only when heartbeats are enabled. See [State-Digest Recovery Net](#state-digest-recovery-net).

Example wire format: `[4, {"sequence": 42, "digest": "a3f8c2..."}]`

**ClientHeartbeatPayload**
```json
{
  "lastSentSequence": 3,
  "digest": "b1d9e7..."
}
```

- `lastSentSequence`: Client's last-sent sequence number. The server uses this to detect trailing client-to-server gaps.
- `digest`: On-demand state digest of the client's live graph. The server compares it against its own digest to detect residual content-level divergence. See [State-Digest Recovery Net](#state-digest-recovery-net).

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

See [Subject Updates](connectors-subject-updates.md) for details on the update format.

## Resilience

### Write Retry Queue

Write retry queue behavior (ring buffer, optimistic re-apply on reconnection, source wins on conflict) is provided by `SubjectSourceBase`. See [Connectors — Write Retry Queue](connectors.md#write-retry-queue). Configure via the client configuration:

```csharp
configuration.WriteRetryQueueSize = 1000;  // Buffer up to 1000 writes (default, 0 to disable)
configuration.RetryTime = TimeSpan.FromSeconds(10);
```

### Reconnection

The outer retry loop (buffer → listen → load initial state → replay → process) is handled by `SubjectSourceBase` (see [Pump Lifecycle](connectors.md#pump-lifecycle)). The WebSocket client adds exponential backoff with jitter within its monitor loop:

```csharp
configuration.ReconnectDelay = TimeSpan.FromSeconds(5);      // Initial delay
configuration.MaxReconnectDelay = TimeSpan.FromSeconds(60);  // Maximum delay
```

On reconnection, the client performs the Hello/Welcome handshake to obtain a state snapshot from the server. The base class then handles loading initial state, replaying buffered updates, and optimistic retry re-apply (see [Connectors — Initialization Sequence](connectors.md#initialization)).

The circuit breaker pauses reconnection attempts after repeated failures:

```csharp
configuration.CircuitBreakerFailureThreshold = 5;               // Open after 5 consecutive failures (default)
configuration.CircuitBreakerCooldown = TimeSpan.FromSeconds(60); // Wait before retrying (default)
```

### Sequence Numbers and Gap Detection

The server maintains a monotonically increasing sequence counter that is incremented atomically (`Interlocked.Increment`) each time an update batch is broadcast. This enables clients to detect lost updates.

**Server behavior:**
- Each `BroadcastUpdateAsync` call increments the counter and sets the sequence in the `UpdatePayload` (e.g., `[2, {sequence:7, root:..., subjects:...}]`). The sequence is carried in `UpdatePayload`, which inherits from `SubjectUpdate`.
- The Welcome payload includes the current sequence at snapshot time. Clients initialize their expected next sequence to `welcome.sequence + 1`.
- Heartbeat messages include the current sequence in their payload but do **not** increment it.

**Client behavior (receiving from server):**
- On receiving an Update: the client reads the sequence from the `UpdatePayload`. If `sequence != expectedNextSequence`, the client logs a warning and exits the receive loop, triggering reconnection via the existing recovery flow.
- On receiving a Heartbeat: if `heartbeat.sequence >= expectedNextSequence`, the server has sent updates the client never received. The client exits the receive loop and reconnects.
- A heartbeat with `sequence < expectedNextSequence` means the client is fully caught up. No action needed.
- On receiving a Resync: the client immediately sends a complete update of all properties it owns (a reverse Welcome), which the server applies authoritatively to recover any lost writes.

**Client behavior (sending to server):**
- The client stamps a monotonic per-connection `Sequence` on every `Update` it sends, starting at 1 on each (re)connect.
- After receiving a server Heartbeat, the client replies with a `ClientHeartbeat` carrying its last-sent sequence. If the server has not received through that sequence, it sends a `Resync`.

**Server behavior (receiving from client):**
- The server tracks the expected-next client sequence per connection (`ConnectionSequenceTracker`). On an in-stream gap (received sequence higher than expected), the server sends `Resync` to that client and realigns its tracker. The received update is still applied.
- On receiving a `ClientHeartbeat`: if the client's last-sent sequence exceeds the server's last-received sequence for that connection, a `Resync` is sent to recover the trailing loss.

**Recovery flow on server-to-client gap:**
Gap detected -> receive loop exits -> `RunMonitorLoopAsync` detects connection lost -> `StartBuffering` -> exponential backoff delay -> `ConnectAsync` -> Welcome with full state + new sequence -> `SubjectPropertyWriter.LoadInitialStateAndResumeAsync` loads initial state under the buffer lock and replays buffered updates. The existing reconnection flow handles everything.

**Recovery flow on client-to-server gap:**
In-stream gap or idle trailing loss detected by server -> server sends `Resync` -> client responds with complete owned-state update -> server applies it authoritatively. No reconnection needed.

**Symmetry:**
A server-to-client gap is recovered by the client pulling the server's complete state (Welcome on reconnect). A client-to-server gap is recovered by the server pulling the client's complete owned state (Resync). The write retry queue complements this for the disconnect/reconnect case.

### State-Digest Recovery Net

Sequence numbers guarantee message delivery: every sent update reaches its destination or a gap is detected. They do not, however, guarantee that the applied content is identical on both sides once heavy concurrent structural churn or repeated reconnects are factored in. The state-digest net is the quiescent-consistency backstop for that residual content-level divergence. Together the two layers implement the library's "state agrees once writes settle" model.

**How it works:**

- Both the server `Heartbeat` and the client `ClientHeartbeat` carry a `Digest` field. The digest is a deterministic, value-aware hash computed on demand from the live graph (no persistent shadow structure). It is timestamp-insensitive so clock skew does not cause false positives.
- Heartbeats are server-idle-gated: the server only sends a Heartbeat when no update was broadcast within the last `HeartbeatInterval` (a time-since-last-broadcast cooldown, see `BroadcastHeartbeatAsync`). This means digest comparisons happen during quiescent periods, when the two sides should agree.
- On receiving a heartbeat, each side compares the peer's digest to its own. A match means the graphs are consistent; no action is taken.
- A mismatch routes to the existing recovery: a server-to-client mismatch triggers client reconnection (Welcome with full state); a client-to-server mismatch causes the server to send `Resync`, which causes the client to re-push its complete owned state. This preserves client writes rather than discarding them.
- A false-positive mismatch is safe: it triggers one extra resync cycle, never incorrectness.
- The net is bounded: O(1) on the wire (a hash), O(N) to compute but only at idle. There is no persistent per-update structure. This is the key contrast with the previously-removed `SentStructuralState` shadow, which was unbounded, leaky, and structural-only. The digest is value-aware and computed on demand.

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

Unlike MQTT and OPC UA connectors which maintain per-property topic/node caches that require cleanup on subject detach (see [Subject Lifecycle Tracking](tracking.md#subject-lifecycle-tracking)), the WebSocket connector synchronizes the entire subject graph as a unit. There are no per-property caches to clean up — the server builds a fresh snapshot for each new client connection, and broadcast updates are derived from the change tracking layer. Connection-level resources (WebSocket, send lock, cancellation tokens) are cleaned up when a client disconnects or the server stops.

## Known Limitations

- **Snapshot lock during client connection**: When a new client connects, the server builds a full state snapshot under the same lock used for applying updates. This blocks incoming updates for the duration of the snapshot, which is proportional to graph size. This is acceptable because new-client connections are infrequent relative to the update rate, but could become a concern with very large subject graphs and frequent client reconnections.

- **Broadcast timeout**: A slow client can delay broadcast completion for other clients. Broadcasts have a 10-second timeout to mitigate this — sends that haven't completed continue in the background, and zombie detection cleans up persistently slow connections. However, very slow clients may still cause temporary backpressure before being removed. This should be revisited if it becomes a bottleneck in high-throughput scenarios.

- **No server acknowledgment for client writes**: When a client sends an update to the server, `WriteChangesAsync` returns success as soon as the WebSocket send completes — it does not wait for the server to acknowledge or apply the change. This means transactions committed over WebSocket confirm only that the message was sent, not that the server accepted it. This differs from OPC UA and MQTT sources, where the external system provides real write confirmation. If the server fails to apply the update (e.g., validation error, concurrent conflict), the client is not notified. See [#231](https://github.com/RicoSuter/Namotion.Interceptor/issues/231) for planned server-ack support.

## Future Extensibility

The protocol is designed for future enhancements:

- **MessagePack support**: `format` field in Hello/Welcome enables negotiation for 3-4x smaller payloads
- **Commands/RPC**: Message types 7-8 reserved for invoking methods on subjects
- **Subscriptions**: Message types 9-10 reserved for subscribing to specific subjects/properties
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

Intel(R) Core(TM) Ultra 7 258V

```
Server Benchmark - 1 minute - [2026-02-18 22:19:57.430]

Total received changes:          1197999
Total published changes:         1196897
Process memory:                  366.27 MB (188.56 MB in .NET heap)
Avg allocations over last 60s:   78.49 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19966.69   19952.27   20330.10   20395.31   20694.54   20694.54   20694.54   19240.79     299.87          -
End-to-end latency (ms)            21.78      18.33      34.56      38.97      48.64     107.65     152.63       0.22       9.26    1197999
```

```
Client Benchmark - 1 minute - [2026-02-18 22:19:59.994]

Total received changes:          1198798
Total published changes:         1198000
Process memory:                  314.71 MB (166.21 MB in .NET heap)
Avg allocations over last 60s:   72.03 MB/s

Metric                               Avg        P50        P90        P95        P99      P99.9        Max        Min     StdDev      Count
-------------------------------------------------------------------------------------------------------------------------------------------
Received (changes/s)            19990.74   19933.24   20515.98   20677.66   21017.99   21017.99   21017.99   18971.91     399.01          -
End-to-end latency (ms)            22.45      19.17      36.27      40.33      51.50      70.56      85.93       0.24       9.65    1198798
```
