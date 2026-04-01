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
| `MeasuredEnergyConsumed` | WattHour | Total energy (PM models only) |
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
| `ElectricalVoltage` | Volt | Voltage |
| `ElectricalCurrent` | Ampere | Current |
| `ElectricalFrequency` | Hertz | Frequency |
| `Temperature` | °C | Internal chip temperature |

**Operations:** Open, Close, Stop, SetPosition

### Energy Meter (`em:N`)
| Property | Unit | Description |
|----------|------|-------------|
| `MeasuredPower` | Watt | Total active power (sum of phases) |
| `MeasuredEnergyConsumed` | WattHour | Total energy consumed (from `emdata:N`) |
| `TotalReturnedEnergy` | WattHour | Total energy returned (from `emdata:N`) |
| `Phases[3]` | - | Per-phase voltage, current, frequency, power, power factor |

Note: The energy meter does not have its own temperature. On devices like the Pro 3EM, temperature is reported as a separate `temperature:0` component.

### Input (`input:N`)
| Property | Description |
|----------|-------------|
| `State` | Digital input on/off |
| `CountTotal` | Counter mode total (if applicable) |
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
| `IpAddress` | WiFi station IP |
| `SignalStrength` | WiFi RSSI in dBm |
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
- **Connection timeout**: Verify the IP address and that the device is on the same network.
- **Authentication errors**: Set the Password configuration property if the device has authentication enabled.

## Implementation Details

### Gen2 RPC API

This integration exclusively uses the Shelly Gen2 RPC API. Gen1 devices are detected via the `gen` field in the `/shelly` endpoint response and rejected.

Key endpoints used:

| Endpoint | Purpose |
|----------|---------|
| `GET /shelly` | Device info (name, model, MAC, generation, firmware) |
| `GET /rpc/Shelly.GetStatus` | Full status of all components in a single call |
| `GET /rpc/EMData.GetStatus?id=0` | Energy meter cumulative totals (only if `em:N` present) |
| `GET /rpc/Switch.Set?id={N}&on=true\|false` | Switch control |
| `GET /rpc/Cover.Open\|Close\|Stop?id={N}` | Cover control |
| `GET /rpc/Cover.GoToPosition?id={N}&pos={0-100}` | Cover position control |
| `ws://{host}/rpc` | WebSocket for real-time push notifications |

### Polling and WebSocket

HTTP polling is the **primary update mechanism**. A single `Shelly.GetStatus` call per cycle retrieves all component states. A WebSocket connection runs as a **parallel task** for real-time push updates between poll cycles.

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

WebSocket `NotifyStatus` messages contain only changed fields for a component. To prevent nulling out existing values (e.g. voltage disappearing temporarily during a cover movement), partial updates only overwrite properties that are present (non-null) in the message. Full poll responses overwrite all properties unconditionally.

Similarly, if a `NotifyStatus` only mentions one component type (e.g. `cover:0`), other component types (switches, inputs, etc.) are left untouched.

### Position Mapping

The Shelly API uses `current_pos` where 0 = fully closed and 100 = fully open. HomeBlaze uses a 0..1 decimal scale where 0 = fully open and 1 = fully closed. The mapping is: `Position = (100 - current_pos) / 100`.

### Configuration Changes

`IConfigurable.ApplyConfigurationAsync` signals the polling loop via a `SemaphoreSlim`. The loop exits, clears cached device info, and reinitializes with the new configuration.
