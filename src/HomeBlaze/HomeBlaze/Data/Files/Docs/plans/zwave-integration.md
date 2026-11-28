---
title: Z-Wave Integration
navTitle: Z-Wave
status: Planned
---

# Z-Wave Integration

**Status: Planned**

This plan migrates the Z-Wave support of HomeBlaze v1 (`HomeBlaze.Zwave`) to v2. It does not port v1: it replaces the unmaintained protocol library with a Z-Wave stack of our own, models the devices on shared HomeBlaze contracts, and adds the contracts that Z-Wave, Zigbee and Matter devices have in common.

## Context

v1 consists of a `ZwaveController` (a `PollingThing` on a serial port) and a generic `ZwaveDevice` per node with a list of per-command-class components (`ZwaveSensorComponent`, `ZwaveNotificationComponent`, `ZwaveSwitchComponent` and others) that expose raw values, scales and units. It references [ZWave4Net](https://github.com/roblans/ZWave4Net) 1.3.1, embeds 12 MB of zwave-js device files to name devices and ships `libnserial.so.1`, which .NET 6 and later no longer need.

The available Z-Wave libraries:

| Library | License | State |
|---|---|---|
| ZWave4Net | MIT | Last release and commit July 2023 (1.3.1, which carries a fix that v1 contributed after inlining the library twice). No S0/S2, no interview, no device database |
| ZWaveDotNet | AGPL-3.0 | Single maintainer. Its license rules out referencing it, copying from it and using it as a reference |
| ZWaveJS.NET | MIT | Wrapper of the zwave-js server, needs a Node process or a bundled binary |
| [zwave-js](https://github.com/zwave-js/zwave-js) | MIT | Very active (weekly releases), the stack behind Home Assistant. Node only |

The decision is a **Z-Wave stack of our own** in C#, written with zwave-js, ZWave4Net and the public Z-Wave specifications as references. Code is not copied; data tables are ported where noted, with their license notices. ZWaveDotNet is not used in any way.

The official machine-readable command class definitions are `XmlFiles/zwave.xml` in [Z-Wave-Alliance/z-wave-tools-core](https://github.com/Z-Wave-Alliance/z-wave-tools-core) (BSD-3-Clause, version 2.20.1, commit `f33dc487341d7f8534dc416da442a7b4b0098da1`): 142 command classes in 261 versions with 1677 commands, down to bitfields, enums and variable-size fields.

## Goals and Non-Goals

Goals:

- Every v1 capability is present in v2 (see [v1 Feature Checklist](#v1-feature-checklist)).
- A Z-Wave SDK that is usable in any .NET application without interceptors or HomeBlaze.
- Device subjects that implement shared HomeBlaze contracts, so that automations and dashboards treat a Z-Wave sensor like a Zigbee, Matter or Ecowitt sensor.
- New contracts where devices of several ecosystems share a concept and a consumer needs it now.

Non-goals of the first version: S0 and S2 security, SmartStart, Z-Wave Long Range, OTA firmware updates, NVM backup and restore, Transport Service (segmented frames), association management in the UI, configuration parameter metadata from the device database, and a `Namotion.Interceptor.ZWave` connector with attribute mapping (possible later on the same SDK when a consumer maps Z-Wave values onto its own subject model).

## Decomposition

Three changes, in this order, each with its own pull request:

1. **Abstractions**: new contracts and units in `HomeBlaze.Abstractions`, with Ecowitt, Hue, Nuki and SunSpec adopting them.
2. **Z-Wave SDK** (sub-project 1): `Namotion.ZWave`, `Namotion.ZWave.Generator` and `Namotion.ZWave.Tests`.
3. **Z-Wave device library** (sub-project 2): `Namotion.Devices.ZWave`, `Namotion.Devices.ZWave.HomeBlaze` and `Namotion.Devices.ZWave.Tests`. It gets its own design round before implementation; this plan records its direction only.

## Change 1: Abstractions

### New Contracts

Every contract carries `[SubjectAbstraction]` and a `[Description]`, and readings are nullable and `null` until observed, as [Building Subjects](../development/building-subjects.md) requires.

| Contract | Members | Position | Shared by |
|---|---|---|---|
| `Sensors/ICarbonDioxideSensor` | `decimal? CarbonDioxide` (`StateUnit.PartsPerMillion`) | 280 | Ecowitt WH45, Z-Wave sensor type CO₂, Zigbee and Matter CO₂ measurement |
| `Sensors/IPm25Sensor` | `decimal? Pm25` (`StateUnit.MicrogramPerCubicMeter`) | 285 | Ecowitt WH41/WH45, Zigbee and Matter PM2.5 measurement |
| `Sensors/IWaterLeakSensor` | `bool? IsLeakDetected` (`IsDiscrete`) | 290 | Ecowitt WH55, Z-Wave Notification Water, Zigbee IAS Zone, Matter Water Leak Detector |
| `Sensors/ITamperSensor` | `bool? IsTampered` (`IsDiscrete`) | 295 | Z-Wave Home Security tampering, Zigbee IAS Zone tamper bit |
| `Sensors/ISmokeSensor` | `bool? IsSmokeDetected` (`IsDiscrete`) | 180 | Z-Wave Notification Smoke, Zigbee IAS fire, Matter Smoke CO Alarm |
| `Sensors/ICarbonMonoxideSensor` | `bool? IsCarbonMonoxideDetected` (`IsDiscrete`) | 181 | Z-Wave Notification CO, Zigbee IAS CO, Matter Smoke CO Alarm |
| `Devices/IPairingController` | `bool? IsPairing` (state), `StartPairingAsync(TimeSpan? duration)`, `StopPairingAsync` (operations) | 190 | Z-Wave inclusion, Zigbee permit join, a later Hue light search. A `null` duration uses the default of the hub, a duration above its maximum is clamped to that maximum, and a zero or negative one throws `ArgumentOutOfRangeException`. Starting while a window is open restarts it with the new duration, and stopping while none is open does nothing |
| `Inputs/IMultiPressButtonDevice : IButtonDevice` | `int? PressCount` (`IsDiscrete`): the number of presses of a sequence, set with its final `Release` and `null` in every other state | 151 | Hue `double_short_release`, Z-Wave Central Scene 2x to 5x, Matter Switch MultiPressComplete |

The smoke and carbon monoxide contracts follow the `I{Concept}Sensor` naming of the other sensors. They sit at 180 and 181, in the range of the primary device function, because an alarm is the primary function of a smoke or carbon monoxide detector. Particulate matter has one contract per particle size, as Matter (one cluster per size), Zigbee and Home Assistant (`pm1`, `pm25`, `pm10`) have, so that a device implements only the sizes it measures; `IPm25Sensor` uses the established name of the quantity because no plain English name exists for PM2.5, PM10 or PM1.

The XML documentation of each contract states its semantics, so that every integration maps its protocol the same way:

- `IsSmokeDetected` and `IsCarbonMonoxideDetected` are true for every alarm level (Matter Warning and Critical, Z-Wave detected events), false when the device reports normal, and `null` when unknown. An alarm that the user silenced stays true while the condition persists. Test, fault and end-of-life states are not alarms.
- `IsTampered` is true from the tamper event until the device reports idle. A device that never reports idle resets it after a timeout that its implementation documents.
- `IsLeakDetected` is true while the device reports a leak, also for a leak at an unknown location.
- `PressCount` is set together with the final `ButtonState.Release` of a press sequence and is `null` in every other state. When the protocol signals the end of a sequence (Matter MultiPressComplete, Z-Wave Central Scene, zigbee2mqtt actions), only the final count is reported. Only a device whose protocol reports each press before it knows that the sequence ended, such as Hue, may report the earlier presses as their own `Release` with a lower count, so a consumer that tells a single from a double press reacts to the highest count. `ButtonEvent.PressCount` carries the count on `Release` events and is 1 on every other event.
- `IsPairing` is true while the hub accepts new devices and `null` until the hub reports its state; it turns false when the window closes on its own or when `StopPairingAsync` is called, and depending on the protocol after a device was added.

### Fit with Other Ecosystems

Each new contract was checked against Matter, Zigbee (ZCL clusters and the actions zigbee2mqtt exposes), Home Assistant device classes and Z-Wave:

| Contract | Matter | Zigbee | Home Assistant | Z-Wave |
|---|---|---|---|---|
| `ICarbonDioxideSensor` | CO₂ Concentration Measurement | CO₂ measurement cluster | `carbon_dioxide` | Multilevel Sensor CO₂ |
| `IPm25Sensor` | PM2.5 Concentration Measurement | PM2.5 measurement cluster | `pm25` | (none) |
| `IWaterLeakSensor` | Water Leak Detector | IAS Zone water | `moisture` | Notification Water |
| `ITamperSensor` | (vendor specific) | IAS Zone tamper bit | `tamper` | Notification Home Security |
| `ISmokeSensor` | Smoke CO Alarm `SmokeState` | IAS Zone fire | `smoke` | Notification Smoke |
| `ICarbonMonoxideSensor` | Smoke CO Alarm `COState` | IAS Zone CO | `carbon_monoxide` | Notification CO |
| `IPairingController` | (code based, see the checklist) | Permit join | (none) | Inclusion |
| `IMultiPressButtonDevice` | Switch MultiPressComplete | Single, double and triple actions | Event entity | Central Scene |
| `IBatteryState.IsBatteryLow` | Power Source charge level | Battery alarm | `battery` | Battery 0xFF |

### Changes to Existing Contracts

- `IBatteryState` gets `bool? IsBatteryLow` (position 344, after the charge states of vehicles at 341 to 343) as a `[Derived]` default member that is true when `BatteryLevel` is at most the constant `IBatteryState.LowBatteryLevel` (0.1), false above and `null` when the level is unknown. It signals that the battery that powers the device needs charging or replacing soon. A device that reports a low battery flag, such as Z-Wave Battery 0xFF, implements the property itself and leaves `BatteryLevel` `null` when it has no level. A stationary storage battery that is discharged to a reserve by design reports `null`, so `SunSpecBattery` and `SunSpecStorageCapacity` implement it as `null`; vehicles keep the default. Existing implementations compile unchanged.
- `ButtonEvent` gets `int PressCount { get; init; } = 1`.
- `StateUnit` gets `PartsPerMillion` (ppm) and `MicrogramPerCubicMeter` (µg/m³), appended at the end, with the unit formatting of the UI and the unit list in [Building Subjects](../development/building-subjects.md#state-properties) updated.

### Retrofits

- **Ecowitt**: `EcowittCo2Sensor` implements `ICarbonDioxideSensor` (`Co2` becomes `CarbonDioxide`, `Co2Avg24h` becomes `CarbonDioxideDailyAverage`), `EcowittPm25Sensor` implements `IPm25Sensor` (`Pm25` keeps its name, `Pm25Avg24h` becomes `Pm25DailyAverage`) and `EcowittLeakSensor` implements `IWaterLeakSensor` (`IsLeaking` becomes `IsLeakDetected`, `null` while the gateway reports no status). The daily averages are class-specific properties at position 400. `EcowittSensor` reports `IsBatteryLow` at level 1 of 5 and below, the threshold of Ecowitt, and for a binary battery that reports low.
- **Hue**: `HueButton` implements `IMultiPressButtonDevice` and maps `double_short_release` to `Release` with `PressCount` 2. Single releases report 1. `HueButtonDevice` and `HueMotionDevice` report `IsBatteryLow` from the battery state of the bridge (true for low and critical, false for normal) and fall back to the level when the bridge reports no state.
- **Nuki**: `NukiDevice.IsBatteryCritical` becomes `IsBatteryLow`, so that `NukiSmartLock` reports the bridge's critical battery flag through `IBatteryState`. The property is declared on `NukiDevice`, so `NukiOpener` loses `IsBatteryCritical` as well and reports `IsBatteryLow` without implementing `IBatteryState`.
- **SunSpec**: `SunSpecBattery` and `SunSpecStorageCapacity` report `IsBatteryLow` as `null`, because a storage battery is discharged to its reserve by design.

The Ecowitt DTOs (`EcowittCo2Data`, `EcowittPm25Data`, `EcowittLeakData`) mirror the JSON of the gateway and keep their names.

These renames change the paths of the properties, so history recorded under the old names is not continued and automations or pages that use them must be updated. The pull request lists each rename as a breaking change with its migration step, together with these behavior changes:

- **Hue double press**: a Hue double press now emits a second `Release` event, with `PressCount` 2, after the `Release` of its first press. An automation that reacts to every `Release` runs twice for a double press. Migration: an automation that must run once per press sequence or only for a double press filters on `event/PressCount`, such as `event/ButtonState == Release and event/PressCount == 2`; `event/PressCount == 1` still matches the first press of a Hue double press.
- **New recorded state**: `IsBatteryLow` is a new recorded state on every `IBatteryState` subject, `null` for SunSpec storage. No migration is needed; history of it starts with the update.
- **Ecowitt leak DTO**: `EcowittLeakData.IsLeaking` becomes `bool?`, `null` when the gateway reports no status. Migration: handle `null` where the DTO is read.

### Checklist for Later Integrations

Concepts that Matter, Zigbee or a v1 migration will need, not added until an integration consumes them:

- Thermostat (`IThermostatState`, `IThermostatController`: setpoint, mode, operating state)
- Valve (`IValveState`, `IValveController`), with the migration of the Gardena irrigation valves and Z-Wave or Matter water valves
- Radio signal and link quality of mesh devices (`INetworkAdapter.SignalStrength` assumes an IP adapter)
- `IPm10Sensor` and `IPm1Sensor`, volatile organic compounds and air quality indexes
- Carbon monoxide concentration in ppm (`ICarbonMonoxideConcentrationSensor`), as Matter measures it next to the alarm
- Pairing with a setup code (`ISetupCodePairingController`), for Matter commissioning and Z-Wave S2 or SmartStart

## Change 2: Z-Wave SDK (Sub-Project 1)

### Projects

```
src/HomeBlaze/
├── Namotion.ZWave/                  # SDK, plain .NET
│   ├── Transport/                   # IZWaveTransport, SerialTransport, TcpTransport
│   ├── SerialApi/                   # frame parser, driver, send queue
│   ├── Network/                     # ZWaveController, ZWaveNode, ZWaveEndpoint, interview, node cache
│   ├── CommandClasses/
│   │   ├── Generated/               # one file per command class, generated, checked in
│   │   └── *.cs                     # semantic layer
│   ├── DeviceDatabase/              # lookup over the embedded index
│   └── Definitions/                 # zwave.xml, ported tables, licenses, README with sources and commits
├── Namotion.ZWave.Generator/        # console tool that writes CommandClasses/Generated and the device index
└── Namotion.ZWave.Tests/
```

- `Namotion.ZWave` references only `System.IO.Ports` and `Microsoft.Extensions.Logging.Abstractions`, neither `Namotion.Interceptor` nor `HomeBlaze.*`. JSON goes through a `System.Text.Json` source-generated context, and nothing uses reflection, so the library is Native AOT compatible.
- Like every project under `src/HomeBlaze`, it is not packable for now. The API is still designed as an SDK: public types are deliberate, everything else is internal, `InternalsVisibleTo` only for `Namotion.ZWave.Tests`, and the public API is snapshot-tested with `VerifyChecksTests.PublicApi`.
- SonarAnalyzer is referenced with `PrivateAssets="all"`.
- A regular console generator with checked-in output is used instead of a Roslyn source generator: the definitions change rarely, the generated code stays reviewable in diffs and debuggable, and builds do not parse the 1.3 MB XML.

### Transport

`IZWaveTransport` has one member, `ValueTask<Stream> OpenAsync(CancellationToken cancellationToken)`.

- `SerialTransport(string portName)`: `SerialPort` at 115200 baud, 8N1, returns its `BaseStream`.
- `TcpTransport(string host, int port)`: returns a `NetworkStream`, for a stick behind ser2net or a similar bridge.
- Tests pass a transport that returns an in-memory duplex stream.

### Serial API Driver

- **Frame parser**: a pure incremental state machine over `ReadOnlySpan<byte>` with a pooled buffer, producing `Ack`, `Nak`, `Can` and `DataFrame(type, function, payload)`. It skips bytes up to the next start of frame. The driver acknowledges each valid data frame and answers a bad checksum with a NAK.
- **One transaction at a time**: a read loop and a send loop over a `Channel<T>` queue.
  1. Write the frame and wait for the ACK (1600 ms). On NAK, CAN or timeout, retransmit, with 3 attempts in total and a delay of 100 ms + (attempt - 1) × 1000 ms before each retransmission.
  2. Wait for the response if the function has one (10 s).
  3. Wait for the callback if the function has one, matched by a rolling callback ID from 1 to 255 (SendData: 30 s, with SendDataAbort after 20 s).
- The timeouts and attempt counts are the zwave-js defaults and live in `ZWaveControllerOptions`, the options of `ZWaveController.ConnectAsync`. All waits use the `TimeProvider` of the options. A frame whose bytes stop arriving before it is complete is not timed out yet; the byte timeout of the Host API (150 ms) is added together with the interview stage.
- **Unsolicited frames** (ApplicationCommandHandler, its bridge variant and ApplicationUpdate) are handed to a dispatch loop and never processed on the read loop, so consumer code cannot stall the serial line, and the reports of a node stay in order.
- **Startup**: NAK to resynchronize, then version, home ID and own node ID, Serial API capabilities with the supported function bitmask, init data with the node list, and controller capabilities. The first version targets the 500 series; differences of the 700 and 800 series (Serial API started notification, extended capabilities) are kept out of the way and not implemented. No soft reset: a 500-series stick may re-enumerate on USB after it, and ZWave4Net ran this stick without one.

### Connection Lifetime

One `ZWaveController` instance is one connection:

```csharp
await using var controller = await ZWaveController.ConnectAsync(
    new SerialTransport("/dev/ttyACM0"), options, cancellationToken);
```

When the stream fails, every pending request faults with `ZWaveConnectionException` and `controller.Completion` faults. The SDK does not reconnect by itself; the caller decides, and the device library reconnects with a backoff. The node cache makes a reconnect cheap.

Errors are typed: `ZWaveException` with `ZWaveTimeoutException`, `ZWaveConnectionException`, `ZWaveTransmitException` (carrying the `TransmitStatus`, for example `NoAck` when the node does not answer) and `ZWaveControllerException` (the controller rejected the request). Every request takes a `CancellationToken`; canceling a queued request removes it before it is written.

### Network Model

- `ZWaveController`: `HomeId`, `OwnNodeId`, Serial API and library version, capabilities, `Nodes` (`IReadOnlyDictionary<byte, ZWaveNode>`), events `NodeAdded`, `NodeRemoved`, `NodeStatusChanged`, `InterviewStageChanged` and `ReportReceived`.
- `ZWaveNode`: node ID, protocol info (listening, frequent listening, basic, generic and specific device class, protocol version), manufacturer ID, product type and product ID, `DeviceDescription` from the device database, `InterviewStage`, `Status` (`Alive`, `Dead`, `Asleep`, `Awake`), `Endpoints` (index 0 is the root device, higher indexes come from Multi Channel) and `ReportReceived`.
- `ZWaveEndpoint`: the supported and controlled command classes with the version the node reported, typed command class APIs (see [Command Classes](#command-classes)), `SendAsync(command)` and `GetAsync<TReport>(command)`.

A node object is never replaced while the node stays in the network, so subscriptions on it stay valid. `Nodes` and the command class sets of an endpoint are replaced, never mutated.

### Interview

Each node goes through these stages. The reached stage is stored, so an interrupted interview continues where it stopped:

1. **ProtocolInfo**: from the controller, without radio traffic.
2. **NodeInfo**: RequestNodeInfo, answered by the node information frame through ApplicationUpdate.
3. **CommandClasses**: Manufacturer Specific, then Version (the version of each command class), then Multi Channel endpoints and their capabilities, then the interview of each command class on each endpoint. A command class interview has a static part (supported sensor types and scales, meter types and scales, notification types and events, wake-up capabilities, supported scenes), which is stored, and a dynamic part (current values), which is read again after each start.
4. **DeviceDatabase**: lookup of manufacturer ID, product type, product ID and firmware version.
5. **Complete**.

Up to 5 attempts per node. Nodes are interviewed one at a time, listening nodes first. A sleeping node is interviewed, or refreshed, when its Wake Up Notification arrives; the stack sends Wake Up No More Information once the queue of that node is empty. This replaces the v1 workaround of reading the manufacturer information on every wake-up.

### Node Cache

`IZWaveNodeCache` with `LoadAsync(homeId, cancellationToken)` and `SaveAsync(homeId, snapshot, cancellationToken)`. `JsonFileNodeCache(directory)` writes `<homeId>.json` atomically (temporary file and rename) through the source-generated JSON context.

The snapshot holds static facts only: protocol info, command classes and versions, endpoints, manufacturer IDs, the static interview results and the interview stage. It holds no readings, so a reading stays `null` until the node reports it. Without a cache (the default), every start interviews all nodes. With a cache, a known node, also a sleeping one, is complete at once and only refreshes its dynamic values.

### Network Management

`StartInclusionAsync`, `StopInclusionAsync`, `StartExclusionAsync`, `StopExclusionAsync` (without security), `HealNodeAsync`, `RemoveFailedNodeAsync` and `PingAsync`. They update `Nodes` and raise `NodeAdded` and `NodeRemoved`.

### Command Classes

**Generated layer** (`CommandClasses/Generated/*.g.cs`, from `zwave.xml`):

- One static class per command class, for example `Meter` with `Id = 0x32` and `LatestVersion = 6`, holding one `sealed record` per command (`MeterGet`, `MeterReport`, `MeterSupportedGet`, `MeterSupportedReport`, `MeterReset`) and the enums of the field enums and constant tables (`MeterType`, `RateType`, `SensorType`, `NotificationType`).
- Each command implements `IZWaveCommand<TSelf>` with the static abstract members `CommandClassId`, `CommandId` and `TryDecode(ReadOnlySpan<byte>, out TSelf)`, and the instance member `Encode(Span<byte>, byte version)`.
- One type per command with the layout of the newest version. Versions only add fields at the end, so fields that older versions lack are nullable. Decoding accepts shorter payloads; encoding writes only the fields of the version the node reported. Bitfields become typed properties, variable-size fields `ReadOnlyMemory<byte>` that the semantic layer interprets.
- A generated `ZWaveCommandDecoder` switches on command class and command to the matching `TryDecode`. An unknown or malformed command becomes `UnknownCommand(commandClass, command, payload)`, which is logged and still delivered.
- `overrides.json` fixes quirks of the XML: names, field types and the meaning of variable-size fields.
- Reports are records rather than pooled structs: a Z-Wave network delivers tens of frames per second at most, so the allocation of a report does not matter, and records are simpler to consume.

**Semantic layer** (hand-written partial classes next to the generated ones):

- Values with precision, scale and size as `decimal` for Meter, Multilevel Sensor and Thermostat Setpoint, with the units of each sensor type and scale and of each meter type and scale.
- The events of each notification type, including Water (leak), Access Control (window or door open and closed), Home Security (motion, tampering), Smoke and CO.
- Battery: 0xFF is reported as low battery without a level.
- Central Scene: key pressed 1x to 5x, held down and released, and the supported scenes.
- Typed APIs per endpoint for the command classes v1 uses: Basic, Binary Switch, Multilevel Switch, Binary Sensor, Multilevel Sensor, Meter, Notification (and Alarm v1/v2), Sensor Alarm, Battery, Wake Up, Central Scene, Configuration, Manufacturer Specific and Version, for example `endpoint.SwitchBinary.SetAsync(true, cancellationToken)` or `endpoint.Configuration.SetAsync(parameter, value, cancellationToken)`. Every other command class is reachable through `SendAsync` and `GetAsync<TReport>` with the generated commands.

The unit and notification tables are ported from the registries of zwave-js (`packages/core/src/registries`, MIT, commit `473703662970725e75fcf8d6f2d226c48dc4f3ba`) into JSON in `Definitions/`, checked against the specification, and turned into C# by the generator. The README in `Definitions/` names the sources, commits and licenses, and keeps the license notices of zwave-js (MIT) and of `zwave.xml` (BSD-3-Clause).

**Receiving**: encapsulations are unwrapped recursively. CRC-16 (the checksum is verified), Multi Channel (sets the endpoint), Multi Command (several commands) and Supervision Get (the stack answers with a Supervision Report, Success, or No Support when it cannot decode the inner command, and then delivers the inner command). Frames with S0 or S2 encapsulation are logged as unsupported.

**Sending**: commands to an endpoint above 0 are wrapped in Multi Channel and sent with SendData (ACK, auto route, explore). `GetAsync<TReport>` waits up to 1 s for the matching report: same node, endpoint, command class and command, and an optional predicate such as the sensor type. Requests to a sleeping node wait in the queue of that node until it wakes up, bounded by the caller's `CancellationToken`.

### Device Database

- The generator reads the zwave-js device files (`packages/config/config/devices`, 2388 devices, and `manufacturers.json`, MIT) of a pinned commit. The files are not checked in; the repository holds the generated index and the README with the commit and the license notice.
- The index is one embedded resource, `devices.json.gz`, about 1.1 MB raw and 175 KB compressed, compared to 12 MB in v1. An entry holds the manufacturer name, label, description, the pairs of product type and product ID, the firmware range and the texts for inclusion, exclusion, reset and wake-up and the manual URL. The format is versioned and readers ignore unknown fields, so configuration parameters can be added later.
- `ZWaveDeviceDatabase.TryFind(manufacturerId, productType, productId, firmwareVersion, out ZWaveDeviceDescription description)` loads the resource lazily once into a frozen dictionary and is thread-safe. When several entries match a product with different firmware ranges, the firmware version decides. An unknown product leaves `DeviceDescription` `null`.

### Threading, Events and Logging

- Every public method may be called from any thread. Node and controller state changes on the dispatch loop only, and readers see immutable snapshots.
- Events are `EventHandler<T>` raised on the dispatch loop, one at a time and in order. An exception of a handler is logged and does not stop the loop; a blocking handler delays later events but never the serial line.
- `ILogger<T>`: frames as hex at Trace, interview steps and node status at Information or Debug, retries, timeouts, unsupported encapsulations and unknown commands at Warning, rate-limited per node.
- `DisposeAsync` stops the loops, faults pending requests with `ObjectDisposedException`, closes the stream and saves the node cache. Late callbacks are dropped through a `TaskCompletionSource` gate, never by an exception on a background thread.

### Testing

- **Frame parser**: byte-by-byte input, garbage before a frame, split frames, bad checksums, ACK, NAK and CAN between data frames.
- **Driver** with a fake transport and `FakeTimeProvider`, without real waits: ACK timeouts and retransmission delays, giving up, CAN during a transaction, callback ID wrap-around, SendData abort, canceling a queued request, a failing stream faulting the pending requests and `Completion`.
- **Simulated controller and nodes** in the test project: an in-process Serial API controller with scripted nodes (listening, sleeping with wake-up, multi-channel). It drives the interview tests (continuing after a failure, the queue of a sleeping node and Wake Up No More Information, cached nodes, inclusion and exclusion) and is reused by the tests of the device library.
- **Command classes**: a generated round trip of every command (encode, decode, compare, with sample values from the generator), golden vectors from the specification and from the zwave-js tests for the semantic layer (meter precision and scale, sensor units, Battery 0xFF, notification events, Central Scene), and the encapsulations (CRC-16 vectors, Multi Channel, Multi Command, the Supervision answer).
- **Generator drift test**: the checked-in generated code and index equal the generator output, as for SunSpec.
- **Device database**: lookup with firmware ranges, unknown products, resource size.
- **Public API snapshot** with `VerifyChecksTests.PublicApi`.
- **Hardware** (agreed as part of this plan, not run in CI): a console probe on the maintainer's Aeotec Z-Stick Gen5 connects, prints version, home ID and nodes, interviews with a file cache and logs reports for a while. It is read-only: no inclusion, exclusion or commands that change a device, unless the maintainer approves a separate step such as switching a plug. An xUnit test with `Category=Hardware` runs the same checks when `ZWAVE_PORT` is set and is skipped otherwise.

## Change 3: Z-Wave Device Library (Sub-Project 2, Direction)

The detailed design follows in its own round. The direction:

- **A device library like Shelly**, not a connector: Z-Wave nodes describe themselves, so the library builds subjects from the interview, and commands are operations such as `ISwitchController.TurnOnAsync`.
- **Subjects**, created only for what a node supports and updated in place when reports arrive:

```
ZWaveController           IHubDevice, IPairingController, IConnectionState, IConfigurable, IMonitoredService
└── Nodes: Dictionary<int, ZWaveNode>                      key: node ID
    ZWaveNode             IDeviceInfo, ISoftwareState, IBatteryState, ILastUpdatedProvider, IConnectionState
    ├── Switches            Dictionary<int, ZWaveSwitch>            IPowerRelay, IPowerMeter
    ├── TemperatureSensors  Dictionary<int, ZWaveTemperatureSensor> ITemperatureSensor
    ├── HumiditySensors     Dictionary<int, ZWaveHumiditySensor>    IHumiditySensor
    ├── LightSensors        Dictionary<int, ZWaveLightSensor>       ILightSensor
    ├── RainSensors         Dictionary<int, ZWaveRainSensor>        IRainSensor
    ├── CarbonDioxideSensors Dictionary<int, ZWaveCarbonDioxideSensor> ICarbonDioxideSensor
    ├── DoorSensors         Dictionary<int, ZWaveDoorSensor>        IDoorSensor, ITamperSensor
    ├── PresenceSensors     Dictionary<int, ZWavePresenceSensor>    IPresenceSensor, ITamperSensor
    ├── WaterLeakSensors    Dictionary<int, ZWaveWaterLeakSensor>   IWaterLeakSensor
    ├── SmokeSensors        Dictionary<int, ZWaveSmokeSensor>       ISmokeSensor
    ├── CarbonMonoxideSensors Dictionary<int, ZWaveCarbonMonoxideSensor> ICarbonMonoxideSensor
    ├── Buttons             Dictionary<int, ZWaveButton>            IMultiPressButtonDevice   key: scene number
    └── Sensors             Dictionary<string, ZWaveSensor>         sensor types without a contract: type, value, unit
```

- The keys of the component dictionaries are endpoint indexes, which can have gaps. Contracts sit on the subject that owns the data: an endpoint with Binary Switch and an electric Meter is one `ZWaveSwitch`, like `ShellySwitch`. One class per measurement keeps interface queries clean.
- The UI project provides a controller widget, edit and setup components (serial port or TCP host, inclusion and exclusion) and node widgets.
- Open questions for that round: a root endpoint that mirrors endpoint 1, dimmers through Multilevel Switch, the placement of tamper state on devices with several notification types, and how the node cache is stored in the HomeBlaze data folder.

## v1 Feature Checklist

| v1 feature | v2 |
|---|---|
| Controller on a serial port, with the Windows/Linux port list `COM7;/dev/ttyACM1` | Serial or TCP transport, one configured port per controller |
| `IsConnected`, `Version`, `LastUpdated` of the controller | Controller state in sub-project 2 |
| Start and stop adding and removing nodes | Inclusion and exclusion in the SDK, `IPairingController` and exclusion operations in sub-project 2 |
| Refresh on network changes, polling every 60 s | Event driven, with interview and dynamic refresh; reconnect with backoff |
| Node identity: manufacturer, label, description, device types, listening, version, product IDs | Protocol info, Manufacturer Specific, device database, `IDeviceInfo`, `ISoftwareState` |
| Inclusion, exclusion and reset descriptions, manual URL | Device database |
| `SetConfigurationAsync(parameter, value)` | Configuration command class, node operation |
| `HealNodeNetworkAsync`, `RemoveFailedNode` | `HealNodeAsync`, `RemoveFailedNodeAsync`, node operations |
| Binary switches, also multi-channel, with turn on and off | Binary Switch with Multi Channel, `ZWaveSwitch` (`IPowerRelay`) |
| Electric meter | Meter, `IPowerMeter` on the switch of the endpoint |
| Temperature (with °F conversion), humidity, luminance, rain rate and total rain (POPP Z-RAIN), other sensor types | Multilevel Sensor with units, the sensor components and the generic `Sensors` |
| Notification and alarm with door sensor and flood sensor | Notification events, `IDoorSensor`, `IWaterLeakSensor` |
| Sensor Alarm | Sensor Alarm command class, mapped to the matching components |
| Battery level | Battery, `IBatteryState` with `IsBatteryLow` |
| Central Scene button | Central Scene, `IMultiPressButtonDevice` |
| Wake Up handling | Wake Up queue in the SDK |
| Logging of messages, updates and unknown commands | Logging and `UnknownCommand` |
| Retry helper | Serial API retransmission and interview attempts |
| `libnserial.so.1` | Not needed |
| 12 MB of embedded device files | 175 KB index |

## Verification

- `dotnet build src/Namotion.Interceptor.slnx` with no errors and no warnings.
- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` passes.
- The hardware probe on the Aeotec Z-Stick Gen5 runs before the pull request of the SDK is finalized.
- The v1 feature checklist is reviewed with sub-project 2.
