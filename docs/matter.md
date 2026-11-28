# Matter

The `Namotion.Matter` package controls Matter devices from .NET. It talks to the [Open Home Foundation Matter Server](https://github.com/matter-js/matterjs-server) (matterjs-server, based on matter.js), which owns the Matter fabric, commissioning (also over Bluetooth), secure sessions, subscriptions, OTA updates and the Thread dataset. The package exposes the server through `IMatterController`, an API in Matter terms that does not depend on the server's JSON shapes.

Dependencies: [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions) (MIT). matterjs-server is a separate process (Apache-2.0).

## Running the Server

matterjs-server needs host networking for IPv6 link-local addresses and mDNS. Its WebSocket API has no authentication, so it should listen on a Unix socket or on a trusted network only:

```bash
docker run -d --name matterjs-server --restart=unless-stopped --network=host \
  -v ./matter:/data ghcr.io/matter-js/matterjs-server:stable \
  --listen-address /data/matter.sock
```

The server must speak API schema version 14 or newer, which is matterjs-server 1.6 or newer.

## Connecting

```csharp
await using var controller = await MatterServerController.ConnectAsync(new Uri("unix:///srv/matter/matter.sock"));

var nodes = await controller.StartListeningAsync();
await foreach (var controllerEvent in controller.Events.ReadAllAsync(cancellationToken))
{
    if (controllerEvent is MatterAttributeUpdatedEvent { Path: { Cluster: 6, Attribute: 0 } } updated)
    {
        Console.WriteLine($"Node {updated.NodeId} endpoint {updated.Path.Endpoint} is {(updated.Value.GetBoolean() ? "on" : "off")}");
    }
}
```

`MatterServerController` lives in `Namotion.Matter.Server`, everything else in `Namotion.Matter`. `ConnectAsync` takes `ws://host:5580/ws`, `wss://...` or `unix:///path/to/socket`, optional `MatterServerOptions` and an optional logger. It fails with:

- `ArgumentException` when the URL scheme is not supported.
- `ArgumentOutOfRangeException` when an option is not positive, or a timeout exceeds about 49 days.
- `WebSocketException` when the server refuses the connection or closes it before sending its information.
- `MatterException` when the server's information cannot be read.
- `NotSupportedException` when the server's schema version is not supported.
- `TimeoutException` when the server does not accept the connection and send its information within `ConnectTimeout`.

| Option | Default | Meaning |
|---|---|---|
| `ConnectTimeout` | 30 seconds | How long connecting may take, from opening the WebSocket until the server has sent its information |
| `RequestTimeout` | 60 seconds | How long a request other than commissioning waits for its answer, and how long sending any request may take. It is above the server's own response timeout for a device |
| `MaxMessageSize` | 64 MB | The largest message accepted from the server, in bytes |
| `KeepAliveInterval` | 30 seconds | How often the connection sends a WebSocket ping |
| `KeepAliveTimeout` | 30 seconds | How long the connection waits for the answer to a ping before it treats the connection as lost |

A connection is not reopened. When it is lost, also when the server announces its shutdown, `Events` completes with the error and every pending and later request fails with a `WebSocketException` whose `InnerException` is that error. Connect again and call `StartListeningAsync` for the current state. After `DisposeAsync`, `Events` completes without an error and requests throw `ObjectDisposedException`.

`StartListeningAsync` returns every commissioned node with all attribute values, keyed by `AttributePath` (`endpoint/cluster/attribute`), and the parent of each endpoint in `EndpointParents`. The server subscribes to every node, so the snapshot plus the events always hold the current values; there is no explicit read. Events arrive in order on a single-reader channel. The channel is unbounded, so keep reading it; a consumer that stops lets it grow without limit:

| Event | Meaning |
|---|---|
| `MatterNodeAddedEvent` | A node was commissioned or imported |
| `MatterNodeUpdatedEvent` | A node's structure, availability or attribute set changed; it replaces the previous node |
| `MatterNodeRemovedEvent` | A node was removed |
| `MatterEndpointAddedEvent`, `MatterEndpointRemovedEvent` | An endpoint was added to or removed from a node |
| `MatterAttributeUpdatedEvent` | An attribute value changed; a null value also means the attribute was removed |
| `MatterNodeEventReceivedEvent` | A node reported a Matter event, such as a button press; `TimestampType` (`DeviceUptime`, `Epoch` or `ServerTime`) names the clock of `Timestamp` |
| `MatterServerInfoChangedEvent` | Credentials or other server information changed |

Node IDs of groups (0xFFFFFFFFFFFF0000 and above) are not supported: a method taking a node ID throws `ArgumentOutOfRangeException` for one.

## Values

Received values are `MatterValue`: check `Kind` (`Null`, `Boolean`, `Number`, `String`, `List` or `Struct`), then read with `GetBoolean`, `GetInt64`, `GetUInt64`, `GetDouble`, `GetString`, `GetBytes`, `GetListLength`, `EnumerateList` or `TryGetField`. Structs in attribute values are keyed by field tag and structs in event data and command responses by field name, so `TryGetField(tag, name, out value)` takes both. Values compare by content, so `==`, `Equals` and `GetHashCode` can be used to detect a changed value.

Sent values implement `IMatterPayload`, which writes itself to an `IMatterValueWriter`. `MatterPayload.Empty` is the payload of a command without fields and `MatterPayload.Create` wraps a delegate:

```csharp
await controller.InvokeAsync(nodeId, new CommandPath(endpoint, 8, 4), "moveToLevelWithOnOff", MatterPayload.Create(writer =>
{
    writer.WriteStartStruct();
    writer.WriteFieldName(0, "level");
    writer.WriteUInt64(128);
    writer.WriteFieldName(1, "transitionTime");
    writer.WriteUInt64(10);
    writer.WriteEndStruct();
}));
```

Command and field names are lower camel case, as matter.js spells them. `InvokeAsync` returns the command response, or a null `MatterValue` for a command without response. Attributes are written with `WriteAttributeAsync(nodeId, path, payload)`.

## Commissioning

```csharp
var payloads = await controller.ParseOnboardingPayloadAsync(code);
var node = await controller.CommissionAsync(code, networkOnly: true);
```

`code` is the 11-digit manual pairing code or the `MT:` QR payload. A device that is already on the IP network (for example one shared from another ecosystem through its pairing code) commissions with `networkOnly: true`. A new Wi-Fi or Thread device commissions over Bluetooth, which needs a Bluetooth adapter on the server and the network credentials, set beforehand with `SetWifiCredentialsAsync` or `SetThreadDatasetAsync` (the hex operational dataset). Credentials are stored by the server and never returned.

Commissioning has no timeout; canceling stops waiting, and a node that still gets commissioned arrives as `MatterNodeAddedEvent`. A failure throws `MatterException` with `CommissioningFailureReason`, such as `PasscodeMismatch` or `NotDiscovered`.

`OpenCommissioningWindowAsync` opens a window through which another controller can commission a node and returns the codes to do so. The timeout is a whole number of seconds from 180 to 900, as the Matter specification allows; anything else throws `ArgumentOutOfRangeException`. `RemoveNodeAsync` decommissions a node.

## Errors and Timeouts

A failed request throws `MatterException` with `ErrorCode` and, when a device answered, `Status` and `ClusterStatus`. A write the device rejects throws with the status it answered. A server answer the client cannot read also throws `MatterException`, with the read error as `InnerException`. Requests other than commissioning time out after `RequestTimeout` with `TimeoutException`. A timed invoke takes a `timedRequestTimeout` from 0 to 65535 milliseconds, which some commands such as door lock commands require; anything else throws `ArgumentOutOfRangeException`. Canceling a request's token throws `OperationCanceledException`. Requests can run concurrently.
