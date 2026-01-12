# WebSocket Subject Protocol Design

## Overview

A bidirectional WebSocket protocol for synchronizing `SubjectUpdate` data between .NET servers and clients (including TypeScript). Optimized for industrial, digital twin, and IoT scenarios.

## Key Design Decisions

| Decision | Choice |
|----------|--------|
| Transport | WebSocket only (WSS for TLS) |
| Serialization | JSON for v1 (extensible for MessagePack in future) |
| Message envelope | `[MessageType, CorrelationId, Payload]` |
| SubjectUpdate format | Uses existing SubjectUpdate JSON serialization |
| Handshake | Hello → Welcome (includes initial state) |
| Reconnection | Stateless (fresh InitialState on reconnect) |
| Error handling | Tiered (recoverable → error message, protocol error → disconnect) |
| Pattern | Follows existing ISubjectSource/Server patterns (like MQTT) |

## Target Use Cases

- Industrial / SCADA - High reliability, structured data, audit trails
- Distributed simulation / digital twin - Multiple nodes co-authoring state, high update frequency
- Home automation / IoT - Many small devices, frequent small updates, occasional disconnections

## Topology Support

- Single server, many clients (primary)
- Hierarchical - Edge servers aggregate local devices, sync upstream to central server
- Peer-to-peer mesh (future nice-to-have)

## Protocol Specification

### Message Envelope

All messages use the same envelope structure:

```
[MessageType, CorrelationId, Payload]
```

- `MessageType`: Integer discriminator
- `CorrelationId`: Integer or null (for future request/response pairing)
- `Payload`: Type-specific payload

### Message Types (v1)

| Type | Value | Direction | Payload |
|------|-------|-----------|---------|
| Hello | 0 | Client → Server | `HelloPayload` |
| Welcome | 1 | Server → Client | `WelcomePayload` |
| Update | 2 | Bidirectional | `SubjectUpdate` |
| Error | 3 | Bidirectional | `ErrorPayload` |

### Future Message Types (reserved)

| Type | Value | Direction | Description |
|------|-------|-----------|-------------|
| Command | 4 | Client → Server | RPC call |
| CommandResult | 5 | Server → Client | RPC response |
| Subscribe | 6 | Client → Server | Subscribe to subset |
| Unsubscribe | 7 | Client → Server | Unsubscribe |

### Payload Structures

#### HelloPayload

```typescript
{
  version: number,        // Protocol version (1)
  format?: "json" | "msgpack"  // Optional, defaults to "json". Reserved for future format negotiation.
}
```

#### WelcomePayload

```typescript
{
  version: number,        // Protocol version (1)
  format: "json",         // Confirmed format (always "json" in v1)
  state: SubjectUpdate    // Complete initial state
}
```

#### ErrorPayload

```typescript
{
  code: number,           // Primary error code
  message: string,        // Human-readable summary
  failures?: PropertyFailure[]  // Individual property failures
}

PropertyFailure = {
  path: string,           // Property path (e.g., "Motor/Speed")
  code: number,           // Specific error code
  message: string         // Specific error message
}
```

#### Error Codes

| Code | Name | Description |
|------|------|-------------|
| 100 | UnknownProperty | Property not found |
| 101 | ReadOnlyProperty | Cannot write to read-only property |
| 102 | ValidationFailed | Validation error |
| 200 | InvalidFormat | Malformed message |
| 201 | VersionMismatch | Protocol version not supported |
| 500 | InternalError | Server error |

### SubjectUpdate Wire Format

Uses existing `SubjectUpdate` JSON serialization with camelCase property names:

```json
{
  "id": "optional-id",
  "reference": "optional-reference",
  "properties": {
    "Temperature": {
      "kind": "Value",
      "value": 23.5,
      "timestamp": "2024-01-08T10:30:00Z"
    },
    "Motor": {
      "kind": "Item",
      "item": {
        "properties": {
          "Speed": {
            "kind": "Value",
            "value": 1500
          }
        }
      }
    }
  }
}
```

### Serialization

**v1: JSON only**
- Native browser support via `JSON.parse()`
- Human-readable for debugging
- Uses existing `SubjectUpdate` serialization
- No additional dependencies

**Future: MessagePack support**
- Protocol includes `format` field in Hello/Welcome for negotiation
- Can add MessagePack serializer without breaking existing clients
- Would provide 3-4x smaller payloads for high-frequency scenarios

## Connection Lifecycle

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
   |                                      |
```

### Client Initialization (ISubjectSource pattern)

Following the existing source/connector pattern:

1. **StartListeningAsync**: Connect WebSocket, send Hello, receive Welcome, start receive loop
2. **LoadInitialStateAsync**: Return action that applies initial state from Welcome
3. **WriteChangesAsync**: Send Update messages for local changes

Buffer → Flush → Load → Replay sequence handled by `SubjectSourceBackgroundService`.

### Reconnection

Stateless reconnection:
1. Connection lost
2. Wait reconnectDelay
3. Call StartListeningAsync (reconnect + Hello)
4. Flush pending writes from retry queue
5. Call LoadInitialStateAsync (apply fresh state from Welcome)
6. Resume normal operation

### Error Handling

Tiered approach:

| Error | Response | Connection |
|-------|----------|------------|
| Unknown property in update | Log warning, send Error message, skip property | Stays open |
| Write to read-only property | Send Error message | Stays open |
| Validation failed | Send Error message | Stays open |
| Malformed JSON | - | Disconnect |
| Version mismatch | Send Error in close frame | Disconnect |

## Project Structure

```
src/
├── Namotion.Interceptor.WebSocket/
│   ├── Protocol/
│   │   ├── MessageType.cs
│   │   ├── WsFormat.cs
│   │   ├── HelloPayload.cs
│   │   ├── WelcomePayload.cs
│   │   └── ErrorPayload.cs
│   ├── Serialization/
│   │   ├── IWsSerializer.cs
│   │   └── JsonWsSerializer.cs
│   ├── Client/
│   │   ├── WebSocketSubjectClientSource.cs
│   │   └── WebSocketClientConfiguration.cs
│   ├── Server/
│   │   ├── WebSocketSubjectServer.cs
│   │   ├── WebSocketClientConnection.cs
│   │   └── WebSocketServerConfiguration.cs
│   ├── WebSocketSubjectExtensions.cs
│   └── Namotion.Interceptor.WebSocket.csproj
│
├── Namotion.Interceptor.WebSocket.Tests/
│   ├── Serialization/
│   │   └── JsonWsSerializerTests.cs
│   ├── Protocol/
│   │   └── PayloadTests.cs
│   ├── Integration/
│   │   ├── WebSocketTestServer.cs
│   │   ├── WebSocketTestClient.cs
│   │   ├── TestModel.cs
│   │   └── WebSocketServerClientTests.cs
│   └── Namotion.Interceptor.WebSocket.Tests.csproj
│
├── Namotion.Interceptor.WebSocket.SampleServer/
│   └── Program.cs
│
├── Namotion.Interceptor.WebSocket.SampleClient/
│   └── Program.cs
│
└── Namotion.Interceptor.SamplesModel/          # Shared (existing)
```

## Configuration

### Server Configuration

```csharp
public class WebSocketServerConfiguration
{
    public int Port { get; set; } = 8080;
    public string Path { get; set; } = "/ws";
    public string? BindAddress { get; set; }
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);
    public PathProviderBase? PathProvider { get; set; }
    public ISubjectFactory? SubjectFactory { get; set; }
    public ISubjectUpdateProcessor[]? Processors { get; set; }
}
```

### Client Configuration

```csharp
public class WebSocketClientConfiguration
{
    public Uri ServerUri { get; set; }
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public PathProviderBase? PathProvider { get; set; }
    public ISubjectFactory? SubjectFactory { get; set; }
    public ISubjectUpdateProcessor[]? Processors { get; set; }
}
```

### DI Extension Methods

```csharp
// Server
services.AddWebSocketSubjectServer<TSubject>(config =>
{
    config.Port = 8080;
    config.BufferTime = TimeSpan.FromMilliseconds(16);
});

// Client
services.AddWebSocketSubjectClientSource<TSubject>(config =>
{
    config.ServerUri = new Uri("ws://localhost:8080/ws");
});
```

## Dependencies

- `System.Net.WebSockets` (built-in)
- `Namotion.Interceptor.Connectors` (SubjectUpdate, ISubjectSource, etc.)

## Testing

### Unit Tests

- JSON serialization round-trip
- Protocol message encoding/decoding
- Error payload construction

### Integration Tests

Following OPC UA test patterns:
- `WebSocketTestServer<T>` / `WebSocketTestClient<T>` helpers
- Server → Client synchronization
- Client → Server synchronization
- Multiple clients receive broadcasts

### Sample Apps

- `Namotion.Interceptor.WebSocket.SampleServer` - Hosts subject, exposes via WebSocket
- `Namotion.Interceptor.WebSocket.SampleClient` - Connects to server, syncs subject
- Both use shared `Namotion.Interceptor.SamplesModel`

## TypeScript Client Support

TypeScript clients use native JSON parsing:

```typescript
// Message envelope
type WsMessage = [MessageType, number | null, unknown];

// Parse incoming message
const [type, correlationId, payload] = JSON.parse(message) as WsMessage;

// SubjectUpdate uses same structure as C#
interface SubjectUpdate {
  id?: string;
  reference?: string;
  properties: Record<string, SubjectPropertyUpdate>;
}

interface SubjectPropertyUpdate {
  kind: "Value" | "Item" | "Collection";
  value?: unknown;
  timestamp?: string;
  item?: SubjectUpdate;
  collection?: SubjectPropertyCollectionUpdate[];
  attributes?: Record<string, SubjectPropertyUpdate>;
}
```

## Future Extensibility

- **MessagePack support**: Add `format` negotiation and MessagePack serializer for 3-4x smaller payloads
- **Commands/RPC**: Message types 4-5 reserved for invoking methods on subjects
- **Subscriptions**: Message types 6-7 reserved for subscribing to specific subjects/properties
- **Peer-to-peer**: Protocol designed to support direct node-to-node connections
