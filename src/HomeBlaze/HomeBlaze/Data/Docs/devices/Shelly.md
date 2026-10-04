---
title: Shelly Gen2 Device
icon: Hub
---

# Shelly Gen2 Device

Integration for Shelly Gen2+ smart home devices using the **Gen2 RPC API** over HTTP and WebSocket. Gen1 devices are **not supported** — they use a different REST API and are detected with an error message.

This integration supports dynamic component discovery, automatically detecting switches, covers, energy meters, inputs, and temperature sensors from any Gen2+ device.

## Supported Devices

Any Shelly Gen2+ device is supported. Components are discovered dynamically from the `/rpc/Shelly.GetStatus` response:

| Device | Components |
|--------|------------|
| Plus 1/1PM | switch:0, input:0 |
| Plus 2PM (switch mode) | switch:0,1, input:0,1 |
| Plus 2PM (cover mode) | cover:0, input:0,1 |
| Pro 3EM | em:0, emdata:0, temperature:0 |
| Plus Uni | switch:0,1, input:0,1,2 (counter) |
| Plus Plug S | switch:0 (with power metering) |

Unknown component types (e.g. `light:0`, `dimmer:0`) are silently ignored. These devices will still work — only unsupported components are skipped.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name (falls back to device name from API) |
| `HostAddress` | string | null | Device IP address or hostname |
| `Password` | string | null | Password for auth-enabled devices |
| `PollingInterval` | TimeSpan | 15 seconds | Normal polling rate |
| `RetryInterval` | TimeSpan | 30 seconds | Delay after connection error |

## Component Types

### Switch (`switch:N`)
| Property | Unit | Description |
|----------|------|-------------|
| `IsOn` | - | Relay state |
| `MeasuredPower` | Watt | Active power (PM models only, null otherwise) |
| `TotalImportedEnergy` | WattHour | Total energy (PM models only) |
| `TotalExportedEnergy` | WattHour | Total returned energy (if reported by model) |
| `ElectricalVoltage` | Volt | Voltage (if reported by model) |
| `ElectricalCurrent` | Ampere | Current (if reported by model) |
| `Temperature` | °C | Internal chip temperature (if reported) |

**Operations:** TurnOn, TurnOff

### Cover (`cover:N`)
| Property | Unit | Description |
|----------|------|-------------|
| `Position` | 0..1 | 0 = fully open, 1 = fully closed |
| `ShutterState` | - | Open, Opening, PartiallyOpen, Closing, Closed, Calibrating |
| `IsMoving` | - | Derived from power consumption (> 1W) |
| `MeasuredPower` | Watt | Active power |
| `TotalImportedEnergy` | WattHour | Total energy |
| `TotalExportedEnergy` | WattHour | Always null (not measured) |
| `ElectricalVoltage` | Volt | Voltage |
| `ElectricalCurrent` | Ampere | Current |
| `ElectricalFrequency` | Hertz | Frequency |
| `Temperature` | °C | Internal chip temperature |

**Operations:** Open, Close, Stop, SetPosition

### Energy Meter (`em:N`)
| Property | Unit | Description |
|----------|------|-------------|
| `MeasuredPower` | Watt | Active power of all phases, positive import, negative export |
| `TotalImportedEnergy` | WattHour | Imported energy, phases netted like a billing meter when the [phase netted energy script](#phase-netted-energy-counters-optional) is installed. Without it, the same value as `TotalImportedPhaseEnergy`, which matches a billing meter only while no phases flow in opposite directions. See `IsTotalEnergyPhaseNetted` |
| `TotalExportedEnergy` | WattHour | Exported energy, phases netted like a billing meter when the [phase netted energy script](#phase-netted-energy-counters-optional) is installed. Without it, the same value as `TotalExportedPhaseEnergy`. See `IsTotalEnergyPhaseNetted` |
| `ApparentPower` | VoltAmpere | Apparent power of all phases |
| `ElectricalCurrent` | Ampere | Current of all phases |
| `NeutralCurrent` | Ampere | Neutral current |
| `TotalImportedPhaseEnergy` | WattHour | Sum of the per-phase import counters (device lifetime). Phases are split by direction before summing, so the value is too high whenever phases flow in opposite directions |
| `TotalExportedPhaseEnergy` | WattHour | Sum of the per-phase export counters (device lifetime), summed like `TotalImportedPhaseEnergy` |
| `IsTotalEnergyPhaseNetted` | - | `true` while `TotalImportedEnergy` / `TotalExportedEnergy` come from the script (they can be null right after a device reboot until the script publishes), `false` while they are the per-phase sums, null while the source is unknown: before the device was checked, or after the script components disappeared (then both counters are null until the components return) |
| `Phases[3]` | - | Per-phase voltage, current, frequency, active and apparent power, power factor, `TotalImportedEnergy`, `TotalExportedEnergy` |

Note: The energy meter does not have its own temperature. On devices like the Pro 3EM, temperature is reported as a separate `temperature:0` component.

### Input (`input:N`)
| Property | Description |
|----------|-------------|
| `IsActive` | Digital input on/off |
| `TotalCount` | Counter mode total (if applicable) |
| `CountFrequency` | Counter frequency in Hz |

### Temperature Sensor (`temperature:N`)
| Property | Unit | Description |
|----------|------|-------------|
| `Temperature` | °C | Internal device temperature |

## Device State

| Property | Description |
|----------|-------------|
| `IsConnected` | Connection status |
| `SoftwareVersion` | Current firmware version |
| `AvailableSoftwareUpdate` | Available firmware update version |
| `MacAddress` | Device MAC address |
| `IpAddress` | Ethernet IP when connected by cable (Pro devices), otherwise the WiFi station IP |
| `IsWireless` | `false` on Ethernet, `true` on WiFi, `null` when neither has an IP |
| `SignalStrength` | WiFi RSSI in dBm (`null` on Ethernet) |
| `Model` | Device model identifier |
| `Uptime` | Device uptime |

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

## Troubleshooting

- **"Only Gen2+ Shelly devices are supported"**: The device is Gen1. Gen1 devices use a different REST API and are not compatible with this integration.
- **Connection timeout** (10 seconds): Verify the IP address and that the device is on the same network.
- **Authentication errors**: Set the Password configuration property if the device has authentication enabled.

## Implementation Details

### Gen2 RPC API

This integration exclusively uses the Shelly Gen2 RPC API. Gen1 devices are detected via the `gen` field in the `/shelly` endpoint response and rejected.

Key endpoints used:

| Endpoint | Purpose |
|----------|---------|
| `GET /shelly` | Device info (name, model, MAC, generation, firmware) |
| `GET /rpc/Shelly.GetStatus` | Full status of all built-in components (including energy meter totals). Virtual components are not part of it |
| `GET /rpc/Shelly.GetComponents?dynamic_only=true&include=["config","status"]` | Names and values of virtual components (phase netted energy counters), read on every poll of a device with an energy meter while both components exist. WebSocket pushes are not used for them, so they update once per poll (15 s by default) |
| `GET /rpc/Switch.Set?id={N}&on=true\|false` | Switch control |
| `GET /rpc/Cover.Open\|Close\|Stop?id={N}` | Cover control |
| `GET /rpc/Cover.GoToPosition?id={N}&pos={0-100}` | Cover position control |
| `ws://{host}/rpc` | WebSocket for real-time push notifications |

### Polling and WebSocket

HTTP polling is the **primary update mechanism**. A single `Shelly.GetStatus` call per cycle retrieves all built-in component states. On a device with an energy meter, a `Shelly.GetComponents` call follows to read the virtual phase netted energy counters, because `Shelly.GetStatus` does not contain virtual components. The counters are only read this way, not from WebSocket pushes, so they update once per poll (15 s by default). While the two components do not both exist, this call is skipped until the device configuration changes (`sys.cfg_rev`), which happens when the components are added. A firmware without `Shelly.GetComponents` (HTTP 404) stops the call in the same way. Other failures are retried on the next poll and keep the current values; before the first successful read the counters stay null. A WebSocket connection runs as a **parallel task** for real-time push updates between poll cycles.

On WebSocket connect, a full `Shelly.GetStatus` request is sent. Subsequently, the device sends `NotifyStatus` messages containing only changed properties for affected components.

If the WebSocket disconnects or fails, polling continues unaffected. The WebSocket auto-reconnects after a 30-second delay.

The polling interval dynamically decreases to 1 second when any cover is moving (detected via power consumption > 1W).

### Dynamic Component Discovery

Components are discovered by parsing JSON keys in the `Shelly.GetStatus` response (e.g. `"switch:0"`, `"cover:0"`, `"em:0"`). The top level is parsed with `JsonElement.EnumerateObject()` for dynamic key discovery, then each component value is deserialized into typed internal DTOs.

Unknown component types are silently ignored, making this forward-compatible with new Shelly device types without code changes.

### Child Subject Lifecycle

- Child subjects (switches, covers, inputs, etc.) are created on first discovery and updated in-place on subsequent polls.
- Arrays are only replaced when the component count changes (e.g. after a firmware update adds a new component).
- Energy meter phases are always 3 (a, b, c), initialized in the constructor and never replaced.

### Partial Updates

WebSocket `NotifyStatus` messages contain only changed fields for a component. To prevent nulling out existing values (e.g. voltage disappearing temporarily during a cover movement), partial updates only overwrite properties that are present (non-null) in the message. Full poll responses overwrite all properties unconditionally, except the energy meter's `TotalImportedEnergy` and `TotalExportedEnergy`, which come from the `Shelly.GetComponents` read (or from the per-phase sums without the script). For the energy meter, `em:0` and `emdata:0` pushes are applied as full values, because the device always sends all their fields (verified on a Pro 3EM with FW 2.0.1). The device pushes `emdata:0` on its own once per minute, so the lifetime counters (and the per-phase sums used without the script) update without waiting for the next poll.

Similarly, if a `NotifyStatus` only mentions one component type (e.g. `cover:0`), other component types (switches, inputs, etc.) are left untouched.

### Configuration Changes

`IConfigurable.ApplyConfigurationAsync` signals the polling loop via a `SemaphoreSlim`. The loop exits, clears all device state (child subjects, including the energy meter, are recreated on the next poll), and reinitializes with the new configuration.

## Phase Netted Energy Counters (Optional)

Only needed on a Pro 3EM whose import and export counters must match the billing meter, for example with a battery or PV system. Without it, the energy meter's `TotalImportedEnergy` and `TotalExportedEnergy` are the device's per-phase sums (`IsTotalEnergyPhaseNetted` is `false`) and everything else works as described above.

### Why

A three-phase billing meter sums the phase powers first and then splits the sum into import and export. The device's own counters do it the other way round: every phase is split by direction and the phases are added afterwards. When phases flow in opposite directions at the same time, for example a single-phase load while a battery feeds all three phases, both device counters grow although the billing meter counts nothing. The device values stay available as `TotalImportedPhaseEnergy` / `TotalExportedPhaseEnergy` and per phase on `Phases[i]`.

### Installation

The script is [phase-netted-energy.js](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/src/HomeBlaze/Namotion.Devices.Shelly/Scripts/phase-netted-energy.js). On a Pro 3EM in the triphase profile:

1. Open the device web interface and go to Settings > Scripts > Add script.
2. Paste the script, save it, and enable "Run on startup".
3. Start the script. The two components appear within seconds and HomeBlaze picks them up after its next poll. `IsTotalEnergyPhaseNetted` then becomes `true`.

The script finds the components by name and creates missing ones on a free ID. To create them yourself, add two virtual components of type number named exactly `TotalImportedEnergy` and `TotalExportedEnergy` (any free ID), with `min` 0, a large `max` (for example 999999999999), and not persisted, because a persisted component writes to flash on every publish.

To remove the script, stop and delete it and delete both components, otherwise HomeBlaze keeps reading their last values. Both counters are then null until the device was unreachable or the configuration changed, afterwards they are the per-phase sums.

### Behavior

- The script integrates the summed phase power every second and publishes the counters every 5 seconds as the virtual number components `TotalImportedEnergy` and `TotalExportedEnergy` (Wh).
- Once per minute, when the device counters update, it corrects its net (imported minus exported) against the device net. A correction only affects the counter of the direction that dominated that minute: too little is added, too much is subtracted from the next increments of the same counter. A small measurement offset between the power readings and the device counters therefore never shows up as flow in the other direction, the counters only grow, and the net matches the device counters.
- The state is saved to the device's KVS after the start reconcile, after a device counter reset, and otherwise at most once per hour.
- A script restart while the device keeps running continues from the published values.
- HomeBlaze uses the script values only while both components exist. Without them (or on a firmware without `Shelly.GetComponents`) it uses the device's per-phase sums. Because the script starts at the device's lifetime counters, switching from the sums to the script is continuous at install time. If the components disappear after their values were used in the current connection, both counters stay null until the components return, so the cumulative series does not jump back to the per-phase sums. After the device was unreachable or the configuration changed, a device without the components uses the per-phase sums again.
- If the components cannot be read (timeouts, server errors), HomeBlaze keeps the current values and retries on every poll. Before the first successful read, both counters and `IsTotalEnergyPhaseNetted` stay null.
- Right after a device reboot the components report 0 until the script publishes again. Because the script starts at the device counters, a 0 is only real while the matching device counter is 0 too, so HomeBlaze ignores a 0 otherwise (also while the device counter is not known yet) and keeps the current value.

### Limitations

- On first install the counters start at the device's own lifetime counters (`TotalImportedPhaseEnergy` / `TotalExportedPhaseEnergy`). If phases already flowed in opposite directions before, that historic excess is carried over once, equally in both counters, so the net stays exact; from then on only phase netted energy is added.
- If the script does not run after a device reboot (for example "Run on startup" is off), the counters keep their last values until it runs; if the device was unreachable in between or the configuration changed, they stay null until it runs.
- Within each second the split follows the sampled power, so very short load spikes are not split exactly like the billing meter does.
- After a device reboot (power loss, firmware update) the energy since the last save is recovered from the device counters but attributed only by its sign. Both counters then lack the minority-direction energy since the last save (up to about one hour), and both published counters can step back once by that amount. The net stays exact.
- While the script is stopped, nothing is lost: the next start recovers the period from the device counters, with the same coarser split as after a reboot.
- Editing the configuration of the components (for example renaming them) resets their values on the device. The script republishes the correct values within 5 seconds.
