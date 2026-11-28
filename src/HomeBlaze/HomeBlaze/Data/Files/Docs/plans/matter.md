---
title: Matter Controller
navTitle: Matter
status: Planned
---

# Matter Controller

## Goal

HomeBlaze controls Matter devices (Wi-Fi, Ethernet and Thread) and, in a later sub-project, exposes its own subjects as Matter devices to Apple Home, Google Home and Alexa. This plan covers the first sub-project, the controller.

| Sub-project | Content | Status |
|---|---|---|
| 1. Controller | Commission and control Matter devices from HomeBlaze | This plan |
| 2. Bridge | Expose HomeBlaze subjects as Matter bridged devices | Later, own plan |
| Native backend | A C# Matter stack behind the same protocol API | Possible later spike |

## Decisions

- **No native C# Matter stack for now.** No usable .NET implementation exists (MatterDotNet is AGPL-3.0 and inactive since February 2025, MatterNet is stale, the Apple bindings run on Apple platforms only). Writing one means TLV, the reliable messaging protocol over UDP, SPAKE2+ (P-256 point arithmetic, which .NET does not expose), Sigma sessions, Matter certificates, device attestation, the interaction model and BLE commissioning: months of security-sensitive work. Every open-source hub (Home Assistant, openHAB, ioBroker) runs matter.js out of process instead.
- **matterjs-server as a sidecar.** The Open Home Foundation's matterjs-server (Apache-2.0, based on matter.js) is the Matter controller of Home Assistant. It owns the fabric, commissioning (also over BLE), sessions, subscriptions, OTA and the Thread dataset, and offers a JSON WebSocket API.
- **No interceptor connector.** A connector with attribute mapping, like Modbus, pays off when users model their own subjects. Matter's data model is standardized, so generated models cover it completely and the connector's only user would be the generated code. A generic "expose any subject as a Matter device" connector may make sense in the bridge sub-project and is decided there.
- **A public protocol library.** `Namotion.Matter` has an API in Matter terms, so a native backend can replace the WebSocket backend later without breaking consumers, and the bridge sub-project can build on it.

## Projects

| Project | Content | Depends on |
|---|---|---|
| `Namotion.Matter` | Public protocol API, generated protocol types, matterjs-server backend | nothing of this repository |
| `Namotion.Matter.Generator` | Console tool generating code from the vendored data model XML; output is checked in | |
| `Namotion.Matter.Tests` | Protocol, value decoding and generator tests | |
| `Namotion.Devices.Matter` | Controller, node and device subjects, generated cluster subjects, HomeBlaze interface mapping | `Namotion.Matter`, `HomeBlaze.Abstractions` |
| `Namotion.Devices.Matter.HomeBlaze` | Widget, Edit and Page components | `Namotion.Devices.Matter`, `HomeBlaze.Components.Abstractions` |
| `Namotion.Devices.Matter.Tests` | Reconciliation, mapping, command and event tests | |

All projects reference `SonarAnalyzer.CSharp`, are added to `src/Namotion.Interceptor.slnx`, and the subject and UI assemblies are referenced in `HomeBlaze.csproj` and registered in `Program.cs`.

```
HomeBlaze process                                       matterjs-server container (host network)
┌────────────────────────────────────────────┐          ┌────────────────────────────┐
│ Namotion.Devices.Matter(.HomeBlaze)        │          │ fabric, commissioning,     │   IPv6 / BLE
│   MatterController → Nodes → Devices       │          │ sessions, subscriptions,   │──► Wi-Fi / Ethernet devices
│             │ uses                         │  ws/JSON │ OTA, Thread dataset        │──► Thread border router ──► Thread devices
│ Namotion.Matter: IMatterController ────────┼─────────►│ (matter.js)                │
└────────────────────────────────────────────┘          └────────────────────────────┘
```

## Protocol Library (`Namotion.Matter`)

`IMatterController` is the public API, with types `NodeId` (`ulong`), `EndpointNumber`, `ClusterId`, `AttributePath` and `EventPath`:

- Connection and a snapshot of all nodes with their attributes and endpoint structure.
- Events: node added, updated and removed, attribute changed, Matter event received (node, endpoint, cluster, event ID, event number, priority, timestamp, payload).
- `ReadAttributeAsync`, `WriteAttributeAsync`, `InvokeAsync`.
- `CommissionAsync(code, networkOnly)`, `ParseOnboardingPayloadAsync(code)`, `SetWifiCredentialsAsync`, `SetThreadDatasetAsync`, `RemoveNodeAsync`, `OpenCommissioningWindowAsync`.

Values cross the boundary as an opaque `MatterValue`, which generated code decodes into the C# type of the attribute, field or event. The WebSocket backend fills it from JSON; a native backend would fill it from TLV. JSON shapes do not appear in the public API.

`MatterServerController` implements the API over the matterjs-server WebSocket:

- `ServerUrl` is either `unix:///path/to/socket` (through a connect callback on `ClientWebSocket` that opens a `UnixDomainSocketEndPoint`) or `ws://host:5580/ws`.
- Requests carry a `message_id` and are matched to responses through pending `TaskCompletionSource`s, so callers can run requests concurrently. Requests time out after 30 seconds, except commissioning, which runs until canceled.
- On connect the server's `schema_version` is checked. The minimum is 14 (`endpoint_parents`, commissioning `reason`); an older server fails the connect with a message naming both versions.
- Error responses throw `MatterException` with the error code, the Matter status and cluster status when present, and the commissioning `reason`.
- Frames are reassembled into pooled buffers. Node IDs are read as `ulong`, which keeps 64-bit IDs exact.
- DTOs use System.Text.Json source generation, for Native AOT.

Wi-Fi passwords and Thread datasets are passed through to matterjs-server, which stores them. HomeBlaze never persists them.

The public API is snapshot-tested with `VerifyChecksTests.PublicApi`.

## Code Generation

**Source.** connectedhomeip's `data_model/1.6/` XML (128 clusters, 93 device types, extracted from the specification), vendored with its Apache-2.0 LICENSE and a NOTICE. A new specification version means replacing the folder and regenerating.

**Tool.** `Namotion.Matter.Generator` is run manually and its output is checked in, following `Namotion.Devices.SunSpec.Generator`. An `overrides.json` holds naming fixes and exclusions (camera and WebRTC clusters at first).

**Output into `Namotion.Matter`:**

- One static class per cluster (`OnOff`, `LevelControl`, ...) with `Id`, attribute, command and event IDs, a `[Flags] Feature` enum, enums, bitmaps, structs, command payload records and event payload records.
- `DeviceTypes` with the device type IDs and names.
- Wire names: command, field and event names are converted to the matter.js names (`MoveToLevelWithOnOff` becomes `moveToLevelWithOnOff`). This rule lives only in the generator, and an integration test checks it against the server.

**Output into `Namotion.Devices.Matter`:**

```csharp
[InterceptorSubject]
public partial class OnOffCluster : MatterCluster      // base: FeatureMap, ClusterRevision, AttributeList
{
    [State] public partial bool? OnOff { get; internal set; }
    [State] public partial StartUpOnOffEnum? StartUpOnOff { get; internal set; }

    internal override void Apply(uint attributeId, MatterValue value) { switch (attributeId) { ... } }

    [Operation] public Task OnAsync(CancellationToken cancellationToken = default) => ...;
    public Task SetStartUpOnOffAsync(StartUpOnOffEnum? value, CancellationToken cancellationToken = default) => ...;
}
```

- Properties are nullable, in raw specification units, with `internal set`. `null` means not supported or not reported.
- Commands are methods, writable attributes get `Set...Async` methods. Setters are not intercepted, because a Matter write is a network round trip that can fail.
- `[Operation]` is generated on commands whose parameters are all primitives or enums, so the generic UI can control even devices without a HomeBlaze mapping.
- Clusters with events get one record per event deriving from `MatterEvent : DeviceEvent`, and the cluster subject implements `IObservable<T>` for each, so automation can trigger on any Matter event with typed filter fields. The `Subject<T>` of an event type is created on its first subscription.
- All clusters are generated; the selection happens at the device class level.

## Subject Model

```
MatterController                      IHubDevice, BackgroundService
 └─ Nodes: Dictionary<string, MatterNode>          key: node ID (decimal)
     MatterNode                       IDeviceInfo (BasicInformation on endpoint 0), IConnectionState
      └─ Devices: Dictionary<string, MatterDevice> key: endpoint number; top-level devices
          MatterDevice                base class and fallback for unmapped device types
           ├─ Clusters: Dictionary<string, MatterCluster>   key: cluster ID; generated cluster subjects
           └─ Parts: Dictionary<string, MatterDevice>       key: endpoint number; recursive
```

Device classes in v1:

| Class | Device types | Interfaces |
|---|---|---|
| `MatterLight` | On/Off Light, Dimmable Light, Color Temperature Light, Extended Color Light | `ILightbulb`, `IBrightnessState`/`Controller`, `IColorState`/`Controller`, `IColorTemperatureState`/`Controller` |
| `MatterPlug` | On/Off Plug-in Unit | `ISwitchDevice` |
| `MatterTemperatureSensor` | Temperature Sensor | `ITemperatureSensor` |
| `MatterHumiditySensor` | Humidity Sensor | `IHumiditySensor` |
| `MatterOccupancySensor` | Occupancy Sensor | `IPresenceSensor` |
| `MatterContactSensor` | Contact Sensor | `IDoorSensor` |
| `MatterLightSensor` | Light Sensor | `ILightSensor` |
| `MatterButton` | Generic Switch (momentary) | `IButtonDevice`, `IObservable<ButtonEvent>` |

**Endpoint tree rules.** The server's `endpoint_parents` gives each endpoint's parent.

- Endpoint 0 (root node) is not a device; its BasicInformation goes onto `MatterNode`.
- Aggregator endpoints are skipped; their children attach to their parent.
- A Bridged Node endpoint becomes a device that takes its name and reachability from BridgedDeviceBasicInformation (cluster 0x39). When the same endpoint also declares a device type, it gets that device class: a Hue light behind the Hue Bridge is one `MatterLight` without parts.
- Child endpoints of a device go into its `Parts`: a bridged multi-sensor is one device with temperature, humidity and pressure parts, a power strip is one device with outlet parts.
- The device class follows the endpoint's Descriptor device type list, with a fixed priority when an endpoint declares several types.

**Mapping.** Device classes are thin: interface properties are derived properties over cluster properties with unit conversion (hundredths of degrees Celsius, the level range 1 to 254, mireds), and interface methods call generated cluster commands. A capability whose cluster or feature is missing reads `null`. `MatterButton` translates Switch cluster events into `ButtonState`. Titles use the node label, then the bridged node label, then the product name.

**Configuration.** Only `MatterController` has configuration: `ServerUrl` (default `unix:///run/matter/matter.sock`) and `RetryInterval`. Nodes and devices are runtime state with `internal set`, rebuilt from the server on every connect; the HomeBlaze JSON holds only the controller.

**Operations.** On the controller: `CommissionDevice(code)`, `SetWifiCredentials`, `SetThreadDataset`. On a node: `Remove`, `Identify`.

**Extending to further device types** (locks, window coverings, electrical energy measurement) is a device class and a priority entry. `IDoorLock`, `IRollerShutter` and `IPowerMeter` exist; thermostats need a new interface in `HomeBlaze.Abstractions`.

## Runtime Behavior

- **Connect loop.** `MatterController` connects, checks the schema version, sends `start_listening`, reconciles the subject tree with the returned nodes and then applies events. Existing subjects are updated in place, removed ones are detached and disposed.
- **Threading.** One receive loop applies all server messages in order, so subject updates never race each other. Requests from the UI and automation run concurrently through the protocol library.
- **Disconnect.** `Status`, `StatusMessage` and `IsConnected` are set, all nodes report disconnected and keep their last values, and the controller retries after `RetryInterval`. A reconnect reconciles the full node list again.
- **Availability.** A node follows the server's `available` flag; a bridged device also follows its reachability attribute.
- **Event deduplication.** Each node keeps the highest event number it has seen and drops events at or below it, so a reconnect never fires an event twice.
- **Errors.** A failed command or write throws `MatterException`, which the operation reports. Unexpected messages are logged through `ILogger<T>` and never stop the loop.
- **Allocations.** A node dump is parsed once and attribute values are decoded directly into the subjects, without an intermediate DTO graph.

## User Interface

| Component | Type | Content |
|---|---|---|
| `MatterControllerWidget` | Widget | Connection status, server version, node count, whether Bluetooth, Wi-Fi credentials and a Thread dataset are available, link to the matterjs-server dashboard |
| `MatterControllerEditComponent` | Edit | `ServerUrl`, `RetryInterval`, with local state and dirty tracking initialized in `OnInitialized` |
| `MatterControllerPage` | Page | Node list (name, vendor, product, availability, device count), Add device panel, credentials section |

There is no Setup component and no discovery: the single controller is created with the default URL, and commissionable devices cannot be added without their pairing code anyway. Nodes and devices use the generic interface-driven display, and their operations appear through `[Operation]`.

**Add device.**

1. The user pastes the 11-digit manual code or the `MT:` QR payload.
2. `parse_onboarding_payload` decodes it without contacting the device; the panel shows vendor, product and whether the device commissions over BLE or on the network.
3. A BLE-only device needs Bluetooth on the server and Wi-Fi credentials or a Thread dataset; when one is missing, the panel says so and points to the credentials section.
4. Commissioning runs with progress shown and can be canceled.
5. On success the node arrives through `node_added`. On failure the server's `reason` is shown in plain words, such as "Wrong pairing code" or "Device not found, is it in pairing mode?".

**Credentials.** Wi-Fi SSID and password and the Thread dataset (hex TLV) are write-only: they are sent to the server, and the page only shows whether each is set.

## Deployment

- **Docker Compose.** An opt-in `matter` profile runs `ghcr.io/matter-js/matterjs-server`, pinned by version and digest, with `network_mode: host` (needed for IPv6 link-local addresses and mDNS), `read_only`, and its storage in `./data/Matter`. It listens only on `--listen-address /run/matter/matter.sock` in a volume shared with the `homeblaze` service, so no unauthenticated port is opened on the network. Bluetooth (`--bluetooth-adapter 0` and the D-Bus mount) is commented out, like the GPIO lines, together with the permission note for the unprivileged data folder.
- **Aspire AppHost.** An optional matterjs-server container behind an `AppHost:Matter` flag, with host networking (Linux only).
- **Trust model.** The matterjs-server WebSocket has no authentication. The documentation states that a `ws://` port, when used, must stay on a trusted network.

## Testing

**Unit tests** (default run):

- `Namotion.Matter.Tests`: the WebSocket protocol against an in-memory fake server (request matching, timeouts, error mapping, schema check, node IDs up to `ulong.MaxValue`, the Unix socket connect path), `MatterValue` decoding for every generated type shape (integers, nullables, enums, bitmaps, structs, lists, octet strings), and generator tests from XML snippets to C# including the wire naming rule.
- `Namotion.Devices.Matter.Tests`: replayed server messages through the fake server: reconciliation in place, the endpoint tree rules (Aggregator, bridged multi-sensor with parts, composed power strip, a Hue Bridge fixture from a recorded dump), device class selection, interface mapping and unit conversion, `device_command` payloads, button event translation, and event deduplication after a reconnect.

**Integration tests** (`Category=Integration`, run when the Matter code changes): a fixture starts matterjs-server and a matter.js virtual light through `npx`, commissions the light on the network with its pairing code, reads state, toggles it and observes the update, reads and writes an attribute, checks that the generated wire names are accepted, and removes the node. `import_test_node` loads recorded dumps of real devices to check their structure end to end.

**Manual verification** before the pull request is finalized: commission a Philips Hue Bridge on a real host and check on/off, brightness, color and color temperature of its lights, behavior after a HomeBlaze reconnect and after a matterjs-server restart.

## Documentation

- `docs/matter.md`: the `Namotion.Matter` library (API, backend, code generation), without application references.
- `Docs/devices/Matter.md`: the compose profile, commissioning with a Hue Bridge walkthrough, Wi-Fi and Thread credentials, Bluetooth, the trust model and troubleshooting.
- `Data/Files/Devices/Matter.json`: a sample controller configuration.

## Out of Scope

- The bridge sub-project and a native C# backend.
- Device types beyond the table above (locks, window coverings, energy measurement, thermostats).
- QR code scanning with the camera, an OTA update UI, a Thread topology view, group commands, ICD management, camera clusters and discovery.
