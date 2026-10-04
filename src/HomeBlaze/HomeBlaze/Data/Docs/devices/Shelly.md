---
title: Shelly Gen2 Device
icon: Hub
---

# Shelly Gen2 Device

Integration for Shelly Gen2+ smart home devices using the **Gen2 RPC API** over HTTP and WebSocket. Switches, covers, energy meters, inputs and temperature sensors are discovered automatically from the device status. Gen1 devices are **not supported**, because they use a different REST API; they are detected and reported with an error message.

## Supported Devices

Any Shelly Gen2+ device is supported. Components are discovered dynamically from the `/rpc/Shelly.GetStatus` response:

| Device | Components |
|--------|------------|
| Plus 1/1PM | switch:0, input:0 |
| Plus 2PM (switch mode) | switch:0,1, input:0,1 |
| Plus 2PM (cover mode) | cover:0, input:0,1 |
| Pro 3EM (triphase profile) | em:0, emdata:0, temperature:0 |
| Plus Uni | switch:0,1, input:0,1,2 (counter) |
| Plus Plug S | switch:0 (with power metering) |

Unknown component types (for example `light:0`, `dimmer:0`, and the `em1:N` / `pm1:N` meters of the Pro 3EM monophase profile, Pro EM and Plus PM Mini) are ignored. Such devices still work, only the unsupported components are skipped.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name (falls back to the device name from the API) |
| `HostAddress` | string | null | Device IP address or hostname |
| `Password` | string | null | Reserved, device authentication is not supported yet |
| `PollingInterval` | TimeSpan | 15 seconds | Normal polling rate |
| `RetryInterval` | TimeSpan | 30 seconds | Delay before reconnecting after a connection or poll error |

## Device State

| Property | Description |
|----------|-------------|
| `IsConnected` | Connection status |
| `Status` / `StatusMessage` | Service status and the last error message |
| `LastUpdated` | Time of the last successful poll |
| `DeviceName` | Device name configured on the device |
| `Manufacturer` / `Model` / `ProductCode` | "Shelly", model identifier (for example `SPEM-003CEBEU`) and application (for example `Pro3EM`) |
| `SerialNumber` / `MacAddress` | Device MAC address |
| `Generation` | API generation (2 or higher) |
| `SoftwareVersion` | Current firmware version |
| `AvailableSoftwareUpdate` | Available stable firmware version (beta versions are not reported) |
| `IpAddress` | Ethernet IP when connected by cable (Pro devices), otherwise the WiFi station IP |
| `IsWireless` | `false` on Ethernet, `true` on WiFi, `null` when neither has an IP |
| `SignalStrength` | WiFi RSSI in dBm (`null` on Ethernet) |
| `Uptime` | Device uptime |

## Component Types

### Switch (`switch:N`)
| Property | Unit | Description |
|----------|------|-------------|
| `IsOn` | - | Relay state |
| `Source` | - | Source of the last state change, as reported by the device |
| `MeasuredPower` | Watt | Active power (PM models only, null otherwise) |
| `TotalImportedEnergy` | WattHour | Total energy (PM models only) |
| `TotalExportedEnergy` | WattHour | Total returned energy (if reported by the model) |
| `ElectricalVoltage` | Volt | Voltage (if reported by the model) |
| `ElectricalCurrent` | Ampere | Current (if reported by the model) |
| `Temperature` | °C | Internal chip temperature (if reported) |

**Operations:** TurnOn, TurnOff

### Cover (`cover:N`)
| Property | Unit | Description |
|----------|------|-------------|
| `Position` | 0..1 | 0 = fully open, 1 = fully closed |
| `ShutterState` | - | Unknown, Open, Opening, PartiallyOpen, Closing, Closed, Calibrating |
| `IsMoving` | - | Derived from power consumption (> 1 W) |
| `LastDirection` | - | Direction of the last movement |
| `IsCalibrating` | - | Whether a calibration runs |
| `Source` | - | Source of the last command |
| `MeasuredPower` | Watt | Active power |
| `TotalImportedEnergy` | WattHour | Total energy |
| `TotalExportedEnergy` | WattHour | Always null (not measured) |
| `ElectricalVoltage` | Volt | Voltage |
| `ElectricalCurrent` | Ampere | Current |
| `ElectricalFrequency` | Hertz | Frequency |
| `PowerFactor` | - | Power factor |
| `Temperature` | °C | Internal chip temperature |

**Operations:** Open, Close, Stop, SetPosition

### Energy Meter (`em:0`)
| Property | Unit | Description |
|----------|------|-------------|
| `MeasuredPower` | Watt | Active power of all phases, positive import, negative export |
| `TotalImportedEnergy` | WattHour | Imported energy, phase netted like a billing meter while the [script](#phase-netted-energy-counters-optional) runs, otherwise the same value as `TotalImportedPhaseEnergy`. See `IsTotalEnergyPhaseNetted` |
| `TotalExportedEnergy` | WattHour | Exported energy, phase netted like a billing meter while the [script](#phase-netted-energy-counters-optional) runs, otherwise the same value as `TotalExportedPhaseEnergy`. See `IsTotalEnergyPhaseNetted` |
| `ApparentPower` | VoltAmpere | Apparent power of all phases |
| `ElectricalCurrent` | Ampere | Sum of the phase currents (not a circuit current, so the meter does not implement `IElectricalCurrentSensor`) |
| `NeutralCurrent` | Ampere | Neutral current |
| `TotalImportedPhaseEnergy` | WattHour | Sum of the per-phase import counters (device lifetime). Phases are split by direction before summing, so the value is too high whenever phases flow in opposite directions |
| `TotalExportedPhaseEnergy` | WattHour | Sum of the per-phase export counters (device lifetime), summed like `TotalImportedPhaseEnergy` |
| `IsTotalEnergyPhaseNetted` | - | `true`: the counters come from the script, `false`: they are the per-phase sums, `null`: the source is unknown and both counters are null. See [How HomeBlaze reads the counters](#how-homeblaze-reads-the-counters) |
| `Phases[3]` | - | Per-phase voltage, current, frequency, active power, apparent power (VoltAmpere), power factor, `TotalImportedEnergy`, `TotalExportedEnergy` |

The energy meter has no temperature of its own. On the Pro 3EM, the temperature is reported as a separate `temperature:0` component.

### Input (`input:N`)
| Property | Unit | Description |
|----------|------|-------------|
| `IsActive` | - | Digital input on/off |
| `TotalCount` | - | Pulse count in counter mode (null otherwise) |
| `CountFrequency` | Hertz | Pulse frequency in counter mode |

### Temperature Sensor (`temperature:N`)
| Property | Unit | Description |
|----------|------|-------------|
| `Temperature` | °C | Internal device temperature |

## Interfaces

| Subject | Interfaces |
|---------|------------|
| Device | `IConfigurable`, `IMonitoredService`, `IConnectionState`, `INetworkAdapter`, `ISoftwareState`, `IDeviceInfo` |
| Switch | `IPowerRelay`, `IPowerMeter`, `IElectricalVoltageSensor`, `IElectricalCurrentSensor`, `ITemperatureSensor`, `IObservable<SwitchEvent>` |
| Cover | `IRollerShutter`, `IPowerMeter`, `IElectricalVoltageSensor`, `IElectricalCurrentSensor`, `IElectricalFrequencySensor`, `ITemperatureSensor` |
| Energy meter | `IPowerMeter` |
| Energy meter phase | `IElectricalVoltageSensor`, `IElectricalCurrentSensor`, `IElectricalFrequencySensor` |
| Temperature sensor | `ITemperatureSensor` |

## JSON Configuration Example

```json
{
  "$type": "Namotion.Devices.Shelly.ShellyDevice",
  "name": "Living Room Shelly",
  "hostAddress": "192.168.1.x",
  "pollingInterval": "00:00:15",
  "retryInterval": "00:00:30"
}
```

## Phase Netted Energy Counters (Optional)

Only needed on a Pro 3EM whose import and export counters must match the billing meter, for example with a battery or PV system. Without it, the energy meter's `TotalImportedEnergy` and `TotalExportedEnergy` are the device's per-phase sums (`IsTotalEnergyPhaseNetted` is `false`) and everything else works as described above.

### Why

A three-phase billing meter sums the phase powers first and then splits the sum into import and export. The device's own counters do it the other way round: every phase is split by direction and the phases are added afterwards. When phases flow in opposite directions at the same time, for example a single-phase load while a battery feeds all three phases, both device counters grow although the billing meter counts nothing. The device values stay available as `TotalImportedPhaseEnergy` / `TotalExportedPhaseEnergy` and per phase on `Phases[i]`.

### Requirements

A Pro 3EM in the triphase profile, with a firmware that supports scripts and virtual components (verified on 2.0.1). The script stops after about one minute if the device has no `emdata:0` component.

### Installation

The script is [phase-netted-energy.js](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/src/HomeBlaze/Namotion.Devices.Shelly/Scripts/phase-netted-energy.js).

1. Open the device web interface and create a script under Scripts.
2. Paste the script, save it, and enable "Run on startup".
3. Start the script. The console shows `[phase-netted-energy] version ... started, imported ... Wh, exported ... Wh`, the two components appear within seconds, and HomeBlaze picks them up after its next poll. `IsTotalEnergyPhaseNetted` then becomes `true`.

The script finds the components by name and creates missing ones on a free ID. To create them yourself, add two virtual components of type number named exactly `TotalImportedEnergy` and `TotalExportedEnergy` (any free ID), with `min` 0, a large `max` (for example 999999999999), and not persisted, because a persisted component writes to flash on every publish.

### Updating and Removing

To update the script, replace its code and restart it. It continues from the published values.

To remove it, stop and delete the script, delete both components, and delete its stored state (`http://<host>/rpc/KVS.Delete?key="phase_netted_energy"`). Without the state deletion, a later install continues from the old counters and books the whole time in between as one net correction. As long as the components exist, HomeBlaze keeps reading their last values.

### How the Script Counts

- It integrates the summed phase power every second and publishes the counters every 15 seconds to the components (Wh, rounded to 0.001 Wh).
- Once per minute, when the device counters update, it corrects its net (imported minus exported) against the device net. A correction only affects the counter of the direction that dominated that minute: too little is added, too much is subtracted from the next increments of the same counter (a debt). So the counters only grow, a small measurement offset between the power readings and the device counters does not show up as flow in the other direction, and imported minus exported follows the device net (up to corrections still owed from the last minute).
- It saves its state to the device's KVS after the start, after a device counter reset, and otherwise at most once per hour.
- On first install (no stored state) it starts at the device's own lifetime counters, so switching HomeBlaze from the per-phase sums to the script is continuous.
- A script restart while the device keeps running continues from the published values. The energy of the time it was stopped is recovered from the device counters and booked by its sign.
- Resetting the device's energy counters does not reset the script counters, they continue.

### How HomeBlaze Reads the Counters

- `Shelly.GetStatus` does not contain virtual components, so on a device with an energy meter every poll also reads `Shelly.GetComponents` while both components exist. WebSocket pushes are not used for them, so HomeBlaze lags the script by up to about 30 seconds (15 s publish plus 15 s poll).
- Without both components (or on a firmware without `Shelly.GetComponents`), the counters are the per-phase sums, and the read is repeated only when the device configuration changes (`sys.cfg_rev`), which happens when the components are added.
- If the components disappear after their values were used in the current connection, both counters stay null until they return, so the cumulative series does not jump back to the per-phase sums. After a reconnect (HomeBlaze restart, host change, or the device was unreachable), a device without the components uses the per-phase sums again.
- If the components cannot be read (timeouts, server errors), the current values are kept and the read is retried on every poll. Before the first successful read, both counters and `IsTotalEnergyPhaseNetted` stay null.
- Right after a device reboot both components report 0 until the script publishes again. HomeBlaze ignores two zeros while the device counters are not 0 (or not known yet), and a single 0 for a counter that already showed more. An ignored value keeps the value HomeBlaze shows, or stays null if HomeBlaze reconnected in the meantime.
- HomeBlaze cannot tell whether the script runs. While it is stopped, the counters stay at their last values and `IsTotalEnergyPhaseNetted` stays `true`; check the script status on the device.

### Limitations

- On first install, any historic excess of the per-phase counters (phases that already flowed in opposite directions) is carried over once, equally in both counters, so the net stays exact; from then on only phase netted energy is added.
- Within each second the split follows the sampled power, so very short load spikes are not split exactly like the billing meter does.
- After a device reboot (power loss, firmware update), the energy since the last save is recovered from the device counters but booked only by its sign. Both counters then lack the minority-direction energy since the last save (up to about one hour), and both published counters can step back once by that amount. The net stays exact.
- If the script does not run after a device reboot (for example "Run on startup" is off), the counters keep their last values until it runs, or stay null if HomeBlaze reconnected in between.
- Editing the configuration of a component (for example its unit or display settings) resets its value to 0 on the device. The script republishes the correct value within 15 seconds, and HomeBlaze ignores the 0 in between. Do not rename the components: HomeBlaze and the script find them only by their exact names.

## Troubleshooting

- **"Only Gen2+ Shelly devices are supported"**: The device is Gen1, which uses a different REST API and is not compatible with this integration.
- **Connection timeout** (10 seconds): Verify the IP address and that the device is on the same network.
- **Authentication errors**: Device authentication is not supported yet. Disable authentication on the device.
- **`IsTotalEnergyPhaseNetted` stays `false`**: The script does not run, or the components are missing, named differently, or only one exists. Check the script console on the device.
- **`IsTotalEnergyPhaseNetted` is `null`**: The components were removed after their values were used, or the device rebooted and the script did not start.

## Implementation Details

### Gen2 RPC API

This integration only uses the Shelly Gen2 RPC API. A device whose `/shelly` response has no `gen` field, or one below 2, is rejected as Gen1.

| Endpoint | Purpose |
|----------|---------|
| `GET /shelly` | Device info (name, model, MAC, generation, firmware) |
| `GET /rpc/Shelly.GetStatus` | Full status of all built-in components (including the energy meter's lifetime counters). Virtual components are not part of it |
| `GET /rpc/Shelly.GetComponents?dynamic_only=true&include=["config","status"]&offset=N` | Names and values of virtual number components (phase netted energy counters), paged. See [How HomeBlaze reads the counters](#how-homeblaze-reads-the-counters) |
| `GET /rpc/Switch.Set?id={N}&on=true\|false` | Switch control |
| `GET /rpc/Cover.Open\|Close\|Stop?id={N}` | Cover control |
| `GET /rpc/Cover.GoToPosition?id={N}&pos={0-100}` | Cover position control |
| `ws://{host}/rpc` | WebSocket for real-time push notifications |

### Polling and WebSocket

HTTP polling is the **primary update mechanism**. A single `Shelly.GetStatus` call per cycle retrieves all built-in component states; on a device with an energy meter, a `Shelly.GetComponents` call can follow (see [How HomeBlaze reads the counters](#how-homeblaze-reads-the-counters)). A WebSocket connection runs as a **parallel task** for real-time push updates between poll cycles.

On WebSocket connect, a full `Shelly.GetStatus` request is sent. Afterwards the device sends `NotifyStatus` messages containing only the changed properties of the affected components.

If the WebSocket disconnects or fails, polling continues unaffected. The WebSocket reconnects after a 30-second delay. If a poll fails, the device reconnects after `RetryInterval`.

The polling interval decreases to 1 second while any cover is moving (detected via power consumption > 1 W).

### Dynamic Component Discovery

Components are discovered by parsing the JSON keys of the `Shelly.GetStatus` response (for example `"switch:0"`, `"cover:0"`, `"em:0"`). The top level is enumerated for dynamic key discovery, then each component value is deserialized into typed internal DTOs. Unknown component types are ignored, which keeps the integration compatible with new Shelly device types.

### Child Subject Lifecycle

- Child subjects (switches, covers, inputs, etc.) are created on first discovery and updated in place on subsequent polls.
- Arrays are only replaced when the component count changes (for example after a firmware update adds a component).
- Energy meter phases are always 3 (a, b, c), initialized in the constructor and never replaced.

### Partial Updates

WebSocket `NotifyStatus` messages contain only the changed fields of a component. To keep existing values (for example voltage disappearing temporarily during a cover movement), partial updates only overwrite properties that are present in the message. Full poll responses overwrite all properties, except the energy meter's `TotalImportedEnergy` and `TotalExportedEnergy` while they come from the script.

For the energy meter, `em:0` and `emdata:0` pushes are applied as full values, because the device always sends all their fields (verified on a Pro 3EM with FW 2.0.1). The device pushes `emdata:0` on its own once per minute, so the lifetime counters update without waiting for the next poll.

If a `NotifyStatus` only mentions one component type (for example `cover:0`), other component types are left untouched.

### Configuration Changes

`IConfigurable.ApplyConfigurationAsync` signals the polling loop via a `SemaphoreSlim`. The loop exits and reconnects with the new configuration. When the host address changed, all device state is cleared first (child subjects, including the energy meter, are recreated on the next poll); otherwise the children and counters are kept.
