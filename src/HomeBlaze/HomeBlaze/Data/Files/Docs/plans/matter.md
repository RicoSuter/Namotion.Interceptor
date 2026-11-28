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

The controller ships in four pull requests:

| Pull request | Content |
|---|---|
| 1. Protocol library | `Namotion.Matter` with the WebSocket backend, `Namotion.Matter.Tests`, `docs/matter.md` |
| 2. Code generator | `Namotion.Matter.Generator`, the vendored data model and the generated protocol types |
| 3. Device library | `Namotion.Devices.Matter` with its subject generator, `Namotion.Devices.Matter.HomeBlaze`, tests, the `IsDiagnostic` state option, the `matter` compose profile and `Docs/devices/Matter.md` |
| 4. Thread | The `thread` compose profile, `ThreadBorderRouterUrl`, Thread network creation and dataset sync, and the Thread part of the documentation |

## Decisions

- **No native C# Matter stack for now.** No usable .NET implementation exists (MatterDotNet is AGPL-3.0 and inactive since February 2025, MatterNet is stale, the Apple bindings run on Apple platforms only). Writing one means TLV, the reliable messaging protocol over UDP, SPAKE2+ (P-256 point arithmetic, which .NET does not expose), Sigma sessions, Matter certificates, device attestation, the interaction model and BLE commissioning: months of security-sensitive work. Every open-source hub (Home Assistant, openHAB, ioBroker) runs matter.js out of process instead.
- **matterjs-server as a sidecar.** The Open Home Foundation's matterjs-server (Apache-2.0, based on matter.js) is the Matter controller of Home Assistant. It owns the fabric, commissioning (also over BLE), sessions, subscriptions, OTA and the Thread dataset, and offers a JSON WebSocket API.
- **No interceptor connector.** A connector with attribute mapping, like Modbus, pays off when users model their own subjects. Matter's data model is standardized, so generated models cover it completely and the connector's only user would be the generated code. A generic "expose any subject as a Matter device" connector may make sense in the bridge sub-project and is decided there.
- **A public, backend-neutral protocol library.** `Namotion.Matter` has an API in Matter terms (`IMatterController`, `MatterValue`, `IMatterValueWriter`), so a native backend can replace the WebSocket backend later without breaking consumers, and the bridge sub-project can build on it. This is a deliberate choice of the maintainer.
- **Device classes own the mapped values, raw clusters stay visible as diagnostics.** Consumers get a clean device model in HomeBlaze units, and every Matter attribute stays reachable by path. This is a documented exception to "one live value, one path" (building-subjects.md, Modeling Subjects); pull request 3 adds a sentence about diagnostic duplicates there.

## Projects

| Project | Location | Content | Depends on |
|---|---|---|---|
| `Namotion.Matter` | `src/` | Public protocol API, generated protocol types, matterjs-server backend | nothing of this repository |
| `Namotion.Matter.Generator` | `src/` | Console tool and data model parser generating the protocol types from the vendored data model XML; output is checked in | |
| `Namotion.Matter.Tests` | `src/` | Protocol, value decoding and generator tests | |
| `Namotion.Devices.Matter` | `src/HomeBlaze/` | Controller, node and device subjects, generated cluster subjects, HomeBlaze interface mapping | `Namotion.Matter`, `HomeBlaze.Abstractions` |
| `Namotion.Devices.Matter.Generator` | `src/HomeBlaze/` | Console tool generating the cluster subjects with the parser of `Namotion.Matter.Generator`; output is checked in | `Namotion.Matter.Generator` |
| `Namotion.Devices.Matter.HomeBlaze` | `src/HomeBlaze/` | Widget, Edit and Page components | `Namotion.Devices.Matter`, `HomeBlaze.Components.Abstractions` |
| `Namotion.Devices.Matter.Tests` | `src/HomeBlaze/` | Reconciliation, mapping, command, event and subject generator tests | |

The cluster subjects carry HomeBlaze attributes (`[State]`, `[Operation]`), so their generator lives in `src/HomeBlaze`: code outside `src/HomeBlaze` never references HomeBlaze.

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

`IMatterController` is the public API. Node IDs are `ulong`, endpoint numbers `ushort`, cluster, attribute, command and event IDs `uint`, and `AttributePath`, `CommandPath` and `EventPath` combine them:

- Connection, server information (version, Bluetooth, whether Wi-Fi credentials and a Thread dataset are set) and a snapshot of all nodes with their attributes and endpoint structure.
- Events: server information updated, server shutdown, node added, updated and removed, attribute changed, Matter event received (node, endpoint, cluster, event ID, event number, priority, timestamp, payload).
- `WriteAttributeAsync` and `InvokeAsync`. There is no explicit read: the server subscribes to every node, so the node snapshot and attribute events always hold the current values. `GetNodeAsync` requests a fresh node snapshot.
- `CommissionAsync(code, networkOnly)`, `ParseOnboardingPayloadAsync(code)`, `SetWifiCredentialsAsync`, `SetThreadDatasetAsync`, `GetCredentialSummariesAsync`, `RemoveNodeAsync`, `OpenCommissioningWindowAsync`.

Values cross the boundary as an opaque `MatterValue`, which generated code decodes into the C# type of the attribute, field or event. The WebSocket backend fills it from JSON; a native backend would fill it from TLV. JSON shapes do not appear in the public API. Outgoing values (command payloads, attribute writes) are written through a backend-neutral `IMatterValueWriter`.

`MatterServerController` implements the API over the matterjs-server WebSocket:

- `ServerUrl` is either `unix:///path/to/socket` (through a connect callback on `ClientWebSocket` that opens a `UnixDomainSocketEndPoint`) or `ws://host:5580/ws`.
- **Requests.** Requests carry a `message_id` and are matched to responses through pending `TaskCompletionSource`s created with `RunContinuationsAsynchronously`, so callers run requests concurrently and no continuation runs on the receive loop. Requests time out after 60 seconds, longer than the server's own response timeout of at least 35 seconds over TCP and BLE. Commissioning has no client timeout: the server ends it itself (by default after 3 minutes without finding the device).
- **Ordered delivery.** Events and the results of node snapshot requests (`start_listening`, `get_node`) enter one ordered channel in the order their frames arrive, and the consumer applies them one by one. The server sends the `start_listening` result before any event, so its snapshot comes first and the buffered events follow. This is correct because attribute events carry absolute values: an event sent after the snapshot was taken is either already contained in it, so applying it again changes nothing, or newer. The same holds for a later `get_node` result: responses bypass the server's event queue, but the server builds queued attribute updates and node snapshots only when it sends them, so a frame that arrives after a snapshot never carries an older value.
- **Schema.** The `server_info` message sent on connect is checked. The minimum `schema_version` is 14 (`endpoint_parents`, commissioning `reason`, `parse_onboarding_payload`), and the server's `min_supported_schema_version` must not exceed the schema version the library implements; otherwise the connect fails with a message naming the versions. `server_info_updated` updates the server information.
- **Errors.** Error responses throw `MatterException` with the error code, the Matter status and cluster status when present, and the commissioning `reason`. A write the device rejects comes back as a successful response with a per-endpoint `Status`; `WriteAttributeAsync` throws `MatterException` with that status when any entry is not 0.
- **Buffers.** Frames are reassembled into pooled buffers and parsed with `JsonDocument`, which reads the buffer in place. A `MatterValue` is valid only while the event or snapshot is applied; anything kept longer is decoded or copied out before the buffer returns to the pool. Node IDs, event numbers and timestamps are read as 64-bit integers, which keeps them exact.
- Messages are written with `Utf8JsonWriter`, without reflection-based serialization, for Native AOT.

Wi-Fi passwords and Thread datasets are passed through to matterjs-server, which stores them. HomeBlaze never persists them.

The public API is snapshot-tested with `VerifyChecksTests.PublicApi`.

## Code Generation

**Source.** connectedhomeip's `data_model/1.6/` XML (128 clusters, 93 device types, extracted from the specification), vendored in `Namotion.Matter.Generator` with its Apache-2.0 LICENSE and a NOTICE. A new specification version means replacing the folder and regenerating.

**Tools.** Both generators are run manually and their output is checked in, following `Namotion.Devices.SunSpec.Generator`. An `overrides.json` holds naming fixes, exclusions (camera and WebRTC clusters at first) and the operation lists described under Operations.

**Output of `Namotion.Matter.Generator` into `Namotion.Matter`:**

- One static class per cluster (`OnOff`, `LevelControl`, ...) with `Id`, attribute, command and event IDs, a `[Flags] Feature` enum, enums, bitmaps, structs, command payload records and event payload records.
- `DeviceTypes` with the device type IDs and names.
- Wire names: command, field and event names are converted to the matter.js names (`MoveToLevelWithOnOff` becomes `moveToLevelWithOnOff`). This rule lives only in the generator, and an integration test checks it against the server.

**Output of `Namotion.Devices.Matter.Generator` into `Namotion.Devices.Matter`:**

```csharp
[InterceptorSubject]
public partial class OnOffCluster : MatterCluster      // base: global attributes, all diagnostic
{
    [State(IsDiagnostic = true)] public partial bool? OnOff { get; internal set; }
    [State(IsDiagnostic = true)] public partial StartUpOnOffEnum? StartUpOnOff { get; internal set; }

    internal override void Apply(uint attributeId, MatterValue value) { switch (attributeId) { ... } }

    [Operation] public Task OnAsync(CancellationToken cancellationToken = default) => ...;
    public Task SetStartUpOnOffAsync(StartUpOnOffEnum? value, CancellationToken cancellationToken = default) => ...;
}
```

- Properties are nullable, in raw specification units, with `internal set`. `null` means not supported, not reported or unavailable.
- Every raw property, including the global attributes (AttributeList, AcceptedCommandList, GeneratedCommandList, EventList, FeatureMap, ClusterRevision) in the `MatterCluster` base, is `[State(IsDiagnostic = true)]`. `IsDiagnostic` is a new `StateAttribute` option added in pull request 3: the property panel shows the property in a collapsed Diagnostics group, history does not record it, MCP and AI leave it out by default, and a path still reads it.
- Commands are methods, writable attributes get `Set...Async` methods. Setters are not intercepted, because a Matter write is a network round trip that can fail.
- `[Operation]` is generated on commands whose parameters are all primitives or enums, so the generic UI can control even devices without a HomeBlaze mapping, with the exceptions listed under Operations.
- Clusters with events get one record per event deriving from `MatterEvent : DeviceEvent`, which wraps the event payload record of `Namotion.Matter`. The cluster subject implements `IObservable<T>` for each, so automation can trigger on any Matter event with typed filter fields. The `Subject<T>` of an event type is created thread-safely on its first subscription.
- All clusters except the excluded ones are generated; the selection happens at the device class level. A cluster without a generated class (a manufacturer-specific cluster) is not exposed in v1.

## Subject Model

```
MatterController                      IHubDevice, IMonitoredService, IConnectionState, BackgroundService
 └─ Nodes: Dictionary<ulong, MatterNode>          key: node ID
     MatterNode                       IDeviceInfo (BasicInformation on endpoint 0), IConnectionState
      ├─ Clusters: Dictionary<int, MatterCluster> key: cluster ID; raw clusters of endpoint 0
      └─ Devices: Dictionary<int, MatterDevice>   key: endpoint number; top-level devices
          MatterDevice                base class and fallback for unmapped device types
           ├─ Clusters: Dictionary<int, MatterCluster>   key: cluster ID; generated raw cluster subjects
           └─ Parts: Dictionary<int, MatterDevice>       key: endpoint number; recursive
```

Endpoint numbers and the generated (standard) cluster IDs fit `int`. Node keys are `ulong`, because operational node IDs use the full 64-bit range (imported test nodes start at 0xFFFFFFFE00000000) and the path resolver supports `ulong` keys.

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
| `MatterButton` | Generic Switch with the MomentarySwitch feature | `IButtonDevice`, `IObservable<ButtonEvent>` |

A bridged device also implements `IDeviceInfo` from BridgedDeviceBasicInformation. A latching Generic Switch uses the `MatterDevice` fallback.

**Endpoint tree rules.** The server's `endpoint_parents` gives each endpoint's parent.

- Endpoint 0 (root node) is not a device; its BasicInformation goes onto `MatterNode`, and its clusters into `MatterNode.Clusters`.
- An Aggregator endpoint that is not also a Bridged Node is skipped; its children attach to its parent.
- A Bridged Node endpoint becomes a device that takes its name and reachability from BridgedDeviceBasicInformation (cluster 0x39). When the same endpoint also declares a device type, it gets that device class: a Hue light behind the Hue Bridge is one `MatterLight` without parts. An endpoint that is both a Bridged Node and an Aggregator (a bridge behind a bridge) is a device, and its children become its parts.
- Child endpoints of a device go into its `Parts`: a bridged multi-sensor is one device with temperature, humidity and pressure parts. Endpoints whose parent is endpoint 0 are top-level devices, so a power strip without a composed parent endpoint shows one device per outlet.
- An endpoint without an `endpoint_parents` entry, and every endpoint of a node without the field (a dump imported without it), gets as parent the endpoint with the smallest Descriptor PartsList that contains it, and is top-level when no PartsList contains it.
- The device class follows the endpoint's Descriptor device type list, with a fixed priority when an endpoint declares several types. A device whose type list changes is replaced.

**Mapping.** Device classes own the mapped values as `[State]` properties in HomeBlaze units. They compute them as `[Derived]` properties over the raw cluster subjects, read through the tracked `Clusters` dictionary: the interceptor recalculates them when a raw value or the set of clusters changes, so applying a raw value is the only write and nulling raw values also nulls the device values. Interface methods call generated cluster commands. A capability whose cluster or feature is missing reads `null`, and its controller method throws `NotSupportedException`. Titles use the bridged node label, then the node label, then the product name.

| Interface property | Matter attribute | Conversion |
|---|---|---|
| `ITemperatureSensor.Temperature` (°C) | TemperatureMeasurement.MeasuredValue (int16, 0.01 °C) | value / 100 |
| `IHumiditySensor.Humidity` (`Percent`, a 0 to 1 fraction) | RelativeHumidityMeasurement.MeasuredValue (uint16, 0.01 %) | value / 10000 |
| `ILightSensor.Illuminance` (lux) | IlluminanceMeasurement.MeasuredValue (uint16, logarithmic) | 10^((value - 1) / 10000); 0 (too low to measure) gives 0 |
| `IPresenceSensor.IsPresent` | OccupancySensing.Occupancy (bitmap) | bit 0 |
| `IDoorSensor.DoorState` | BooleanState.StateValue | `true` is `Closed`, `false` is `Open` |
| `ISwitchState.IsOn` | OnOff.OnOff | as is; `TurnOn`/`TurnOff` send `On`/`Off` |
| `IBrightnessState.Brightness` (0 to 1) | LevelControl.CurrentLevel (1 to 254) | value / 254; `SetBrightness(0)` sends `Off`, otherwise `MoveToLevelWithOnOff` with the level clamped to MinLevel and MaxLevel |
| `IColorTemperatureState.ColorTemperature` (0 warm to 1 cool) | ColorControl.ColorTemperatureMireds | (max - value) / (max - min) over ColorTempPhysicalMinMireds and ColorTempPhysicalMaxMireds, clamped; `null` unless ColorMode is color temperature; the setter sends `MoveToColorTemperature` with max - fraction × (max - min) |
| `IColorState.Color` (`#RRGGBB`) | ColorControl, by ColorMode | Hue/saturation mode: CurrentHue × 360 / 254 (EnhancedCurrentHue × 360 / 65536 in enhanced mode) and CurrentSaturation / 254, as HSV with full value; XY mode: CurrentX and CurrentY / 65536 as CIE xy with Y = 1, through sRGB (D65) scaled to the largest channel; `null` in color temperature mode. The setter converts back and sends `MoveToColor` when the XY feature is present, otherwise `MoveToHueAndSaturation`. Brightness stays with LevelControl. |

`MatterButton` translates Switch cluster events into `ButtonState` and publishes a `ButtonEvent` for each:

| Switch event | `ButtonState` |
|---|---|
| InitialPress | `Down` |
| LongPress | `Repeat` |
| ShortRelease | `Release` |
| LongRelease | `LongRelease` |
| MultiPressComplete | `Release`, only on a switch without the MomentarySwitchRelease feature, which sends no ShortRelease |
| MultiPressOngoing | none |

The press count of a multi-press stays available as the raw `MultiPressComplete` event of the Switch cluster.

**Configuration.** Only `MatterController` has configuration: `ServerUrl` (default `unix:///data/Matter/matter.sock`), `RetryInterval` and the optional `ThreadBorderRouterUrl` (the REST API of an OpenThread Border Router, see Thread). Nodes and devices are runtime state with `internal set`, rebuilt from the server on every connect; the HomeBlaze JSON holds only the controller.

**Operations.** On the controller: `CommissionDevice(code)`. On a node: `Remove`, with `Purpose = OperationPurpose.Delete` and `RequiresConfirmation = true`. On a device whose endpoint has the Identify cluster: `Identify`. Wi-Fi credentials and the Thread dataset are set only on the controller page, never through an operation, so they reach neither the generic operation dialog nor MCP.

Generated cluster operations follow two lists in `overrides.json`:

- A deny list gets no `[Operation]`: the commands of clusters that administer the device or the fabric (Operational Credentials, General Commissioning, Administrator Commissioning, Network Commissioning, Time Synchronization, OTA Software Update Requestor, Diagnostic Logs, ICD Management and similar). The C# methods still exist.
- A confirm list gets `RequiresConfirmation = true`: commands that unlock or open access, such as DoorLock `UnlockDoor` and `UnlockWithTimeout`.

**Extending to further device types** (locks, window coverings, electrical energy measurement) is a device class and a priority entry. `IDoorLock`, `IRollerShutter` and `IPowerMeter` exist; thermostats need a new interface in `HomeBlaze.Abstractions`.

## Runtime Behavior

- **Connect loop.** `MatterController` connects, checks the schema version, sends `start_listening`, reconciles the subject tree with the returned nodes and then applies the buffered events in arrival order (see Ordered delivery). Existing subjects are updated in place, attributes missing from a node snapshot become `null`, and removed subjects are detached and disposed.
- **Threading.** One consumer applies the ordered channel, so subject updates never race each other. Requests from the UI and automation run concurrently through the protocol library.
- **Availability.** A node follows the server's `available` flag; a bridged device also follows its Reachable attribute. While a node or a bridged device is unavailable, its raw readings are `null`, and with them the device readings (Readings Are Null Until Observed). A node that becomes available again arrives in a `node_updated` with all attributes. The server does not resend unchanged attributes when a bridged device becomes reachable again, so the controller then requests the node with `get_node` and applies the snapshot at its arrival position.
- **Disconnect.** `Status`, `StatusMessage` and `IsConnected` are set, all nodes report disconnected, all readings become `null`, and the controller retries after `RetryInterval`. A reconnect reconciles the full node list again. `server_shutdown` closes the connection and starts the retry.
- **Event timestamps.** `node_event.timestamp` is used as `Timestamp` only for `timestamp_type` 1 (epoch milliseconds). Type 0 is milliseconds since the device started, and type 2 is the server's time, so for both the receive time is used.
- **Lost events.** Under backpressure the server drops the oldest queued `node_event`s, and it closes the connection when structural events overflow, which triggers a reconnect. The consumer therefore only applies values and never blocks on I/O.
- **Errors.** A failed command or write throws `MatterException`, which the operation reports. Unexpected messages are logged through `ILogger<T>` and never stop the loop.
- **Allocations.** A node dump is parsed once and attribute values are decoded directly into the subjects, without an intermediate DTO graph.

## User Interface

| Component | Type | Content |
|---|---|---|
| `MatterControllerWidget` | Widget | Connection status, server version, node count, whether Bluetooth, Wi-Fi credentials and a Thread dataset are available, and a link to the matterjs-server dashboard only when `ServerUrl` is a `ws://` URL (a Unix socket serves no dashboard to the browser) |
| `MatterControllerEditComponent` | Edit | `ServerUrl`, `RetryInterval`, `ThreadBorderRouterUrl`, with local state and dirty tracking initialized in `OnInitialized` |
| `MatterControllerPage` | Page | Node list (name, vendor, product, availability, device count), Add device panel, credentials section |

There is no Setup component and no discovery: the single controller is created with the default URL, and commissionable devices cannot be added without their pairing code anyway. Nodes and devices use the generic interface-driven display, and their operations appear through `[Operation]`.

**Add device.**

1. The user pastes the `MT:` QR payload or the 11- or 21-digit manual code.
2. `parse_onboarding_payload` decodes it without contacting the device. A QR payload gives the vendor and product IDs and whether the device commissions over BLE, on the network or both. A manual code gives no vendor (the 21-digit form gives IDs) and no capabilities, so the panel says that the method is unknown.
3. The panel decides `networkOnly`: true when the server has no Bluetooth or the QR payload names only on-network commissioning. A QR payload that names only BLE needs Bluetooth on the server and Wi-Fi credentials or a Thread dataset; when one is missing, the panel says so, points to the credentials section and does not start.
4. Commissioning starts with `commission_with_code`. The server reports no progress, so the panel shows an indeterminate indicator until the response. Cancel only stops waiting: the server has no cancel command and keeps commissioning until it succeeds or gives up, and the panel says that the device can still appear.
5. On success the node arrives through `node_added`. On failure the server's `reason` is shown in plain words, such as "Wrong pairing code" (`passcode-mismatch`) or "Device not found, is it in pairing mode?" (`not-discovered`).

**Credentials.** Wi-Fi SSID and password and the Thread dataset (hex TLV) are write-only: they are sent to the server, and the page only shows whether each is set, from the server information. With `ThreadBorderRouterUrl` configured, the section shows the result of the automatic Thread dataset sync, or the error when the border router is unreachable.

## Thread

Thread devices are reachable over IPv6 through a Thread border router. Commissioning a new Thread device needs the network's active dataset, which commercial border routers (Apple, Google) do not hand out. With those, HomeBlaze can only control Thread devices that were paired there first and shared through a multi-admin pairing code. For commissioning its own Thread devices, HomeBlaze runs an OpenThread Border Router (OTBR) that it sets up itself.

- **Radio.** A USB Thread radio flashed with OpenThread RCP firmware first, such as the Home Assistant Connect ZBT-2 (flashed with the Open Home Foundation web flasher, serial at 460800 baud). The radio runs Thread only, not Zigbee at the same time.
- **Border router.** The production image `openthread/border-router` (`openthread/otbr` is the test image of ot-br-posix) on the same host as matterjs-server: host networking, `NET_ADMIN`, `/dev/net/tun` and the radio's serial device. It is configured through `OT_RCP_DEVICE` (for example `spinel+hdlc+uart:///dev/serial/by-id/...?uart-baudrate=460800`), `OT_INFRA_IF` (the LAN interface) and `OT_REST_LISTEN_ADDR` with `OT_REST_LISTEN_PORT` (default `127.0.0.1` and 8081).
- **Network setup and dataset sync.** With `ThreadBorderRouterUrl` configured, `MatterController` checks the border router on every connect to the Matter server and when the URL changes. HomeBlaze writes to the border router only to create a network that does not exist yet:
  1. `GET {ThreadBorderRouterUrl}/node/dataset/active` with `Accept: text/plain` returns the active dataset as hex TLV.
  2. On 204 (no network yet), `PUT /node/dataset/active` with a JSON dataset of channel 15, a random PAN ID and the network name `HomeBlaze-` with the PAN ID in hex, as Home Assistant's OTBR integration does (python-otbr-api `create_active_dataset`). The border router fills the network key, extended PAN ID, mesh-local prefix and PSKc with random values. Then `PUT /node/state` with `"enable"` starts the network, and step 1 reads the dataset. The JSON keys are camelCase, as in the pinned image's `openapi.yaml` (ot-br-posix changed from PascalCase in September 2025).
  3. When the server's `thread_credentials_set` is false, or the extended PAN ID or network name in the server's `get_all_credentials` summary differ from the dataset's, the controller sends the dataset with `set_thread_dataset`.
- **Host network.** The host accepts the border router's route announcements: kernel support for IPv6 route information, `accept_ra_rt_info_max_plen` of at least 64 and `accept_ra=2` (IPv6 forwarding is on for the border router), set persistently through NetworkManager or systemd-networkd. This is also what Thread devices behind Apple or Google border routers need. Running matterjs-server and OTBR on one host with forwarding enabled is part of the manual verification.

## Deployment

- **Docker Compose.** An opt-in `matter` profile runs `ghcr.io/matter-js/matterjs-server`, pinned by version and digest, with `network_mode: host` (needed for IPv6 link-local addresses and mDNS), `read_only`, and its storage in `./data/Matter` mounted at `/data`. It listens only on a Unix socket, set with the `LISTEN_ADDRESS=/data/matter.sock` environment variable rather than `--listen-address`, because the image's health check reads the variable. The socket lies in the storage folder, which the `homeblaze` service already sees as `/data/Matter/matter.sock` through its `./data` mount, so no unauthenticated port is opened on the network. The server runs as uid 1000, so the documentation and the compose comment tell the user to create the folder first (`mkdir -p data/Matter && sudo chown 1000:1000 data/Matter`); compose would otherwise create the missing bind path owned by root, and the server could neither store nor listen. `DEFAULT_FABRIC_LABEL` is set to `HomeBlaze`, which other ecosystems show as the controller's name. Bluetooth (`BLUETOOTH_ADAPTER=0` and the D-Bus mount) is commented out, like the GPIO lines.
- **Thread profile.** An opt-in `thread` profile runs `openthread/border-router`, pinned by version and digest, with the radio's `/dev/serial/by-id/...` path as a commented placeholder. Its REST API has no authentication, so the compose file pins the default network's subnet through `ipam` (for example `172.30.200.0/24` with gateway `172.30.200.1`) and sets `OT_REST_LISTEN_ADDR` to that gateway address, which the `homeblaze` container reaches and LAN clients have no route to. `ThreadBorderRouterUrl` is `http://172.30.200.1:8081`. Whether a host-network container can bind the gateway address reliably, also right after a host restart, is not confirmed and is part of the manual verification.
- **Aspire AppHost.** An optional matterjs-server container behind an `AppHost:Matter` flag, with host networking (Linux only) and `LISTEN_ADDRESS=127.0.0.1`; the developer sets the controller's `ServerUrl` to `ws://localhost:5580/ws`.
- **Trust model.** The matterjs-server WebSocket and the OTBR REST API have no authentication, and whoever reaches the socket controls the fabric. The documentation states that a `ws://` port, when used, must stay on a trusted network.

## Testing

**Unit tests** (default run):

- `Namotion.Matter.Tests`: the WebSocket protocol against an in-memory fake server (request matching, timeouts, ordered delivery of a snapshot followed by buffered events, error mapping including rejected write statuses, schema check in both directions, node IDs up to `ulong.MaxValue`, the Unix socket connect path), `MatterValue` decoding for every generated type shape (integers, nullables, enums, bitmaps, structs, lists, octet strings), and generator tests from XML snippets to C# including the wire naming rule.
- `Namotion.Devices.Matter.Tests`: replayed server messages through the fake server: reconciliation in place, the endpoint tree rules (Aggregator, bridged multi-sensor with parts, a bridged Aggregator, composed power strip, the matterjs-server bridge fixture, a Hue Bridge fixture from a recorded dump, a dump without `endpoint_parents`), device class selection, every row of the conversion and button tables, readings becoming `null` and coming back on availability changes, event timestamps by type, `device_command` payloads, the operation deny and confirm lists, and the subject generator.
- Unit tests of the Thread setup run against a fake border router (dataset returned, 204 followed by network creation, unreachable) and a fake server (dataset sent when missing or different, not sent when equal).

**Integration tests** (`Category=Integration`, run when the Matter code changes): a fixture starts matterjs-server and a matter.js virtual light through `npx` with pinned versions, commissions the light on the network with its pairing code, reads state, toggles it and observes the update, reads and writes an attribute, checks that the generated wire names are accepted, and removes the node. `import_test_node` loads recorded dumps of real devices to check their structure end to end.

**Manual verification** before the pull request is finalized: commission a Philips Hue Bridge on a real host and check on/off, brightness, color and color temperature of its lights, behavior after a HomeBlaze reconnect and after a matterjs-server restart. With a Thread radio: start the `thread` profile, check that the border router's REST API binds the pinned gateway address (also after a host restart) and that HomeBlaze creates the network and syncs the dataset, commission a Thread contact sensor over Bluetooth and check that open and close reach HomeBlaze, also after the host restarts.

## Documentation

- `docs/matter.md`: the `Namotion.Matter` library (API, backend, code generation), without application references.
- `Docs/devices/Matter.md`: the compose profile and the storage folder owned by uid 1000, commissioning with a Hue Bridge walkthrough, Wi-Fi and Thread credentials, Bluetooth, the Thread border router setup (radio firmware, `thread` profile, host network settings, automatic network creation and dataset sync), the trust model and troubleshooting.
- `building-subjects.md`: a sentence on diagnostic duplicates of device values (pull request 3).
- `Data/Files/Devices/Matter.json`: a sample controller configuration.

## Out of Scope

- The bridge sub-project and a native C# backend.
- Device types beyond the table above (locks, window coverings, energy measurement, thermostats).
- Manufacturer-specific clusters, QR code scanning with the camera, an OTA update UI, a Thread topology view, group commands, ICD management, camera clusters and discovery.
