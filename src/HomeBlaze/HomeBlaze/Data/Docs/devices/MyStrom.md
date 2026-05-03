---
title: myStrom WiFi Switch
icon: Power
---

# myStrom WiFi Switch

The myStrom WiFi Switch is a smart plug with built-in power metering, temperature sensing, and relay control over a local HTTP API.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name |
| `IpAddress` | string | null | Device IP address |
| `AllowTurnOff` | bool | true | Safety flag to prevent relay shutdown |
| `PollingInterval` | TimeSpan | 15 seconds | How often to poll the device |
| `RetryInterval` | TimeSpan | 30 seconds | Delay after connection error |

## Operations

| Operation | Description |
|-----------|-------------|
| `TurnOn` | Turns the relay on |
| `TurnOff` | Turns the relay off (respects `AllowTurnOff` flag) |

## State Properties

| Property | Unit | Description |
|----------|------|-------------|
| `IsOn` | - | Relay state (on/off) |
| `MeasuredPower` | Watt | Current power consumption |
| `MeasuredEnergyConsumed` | WattHour | Cumulative energy since device boot |
| `Temperature` | C | Compensated temperature reading |
| `Uptime` | TimeSpan | Device uptime since last boot |
| `IsConnected` | - | Connection status |
| `MacAddress` | - | Device MAC address |
| `FirmwareVersion` | - | Firmware version |
| `DeviceType` | - | Device type identifier (e.g., "WS2") |

## Interfaces

- `IPowerRelay` - relay on/off control
- `IPowerMeter` - measured power and energy
- `ITemperatureSensor` - temperature reading
- `IConnectionState` - connection tracking
- `INetworkAdapter` - network information
- `IMonitoredService` - service status

## JSON Configuration Example

```json
{
  "$type": "Namotion.Devices.MyStrom.MyStromSwitch",
  "name": "Kitchen Switch",
  "ipAddress": "192.168.1.59",
  "allowTurnOff": true,
  "pollingInterval": "00:00:15",
  "retryInterval": "00:00:30"
}
```

## HTTP API

The switch exposes a local HTTP API on port 80:

| Endpoint | Purpose |
|----------|---------|
| `GET /info` | Device information (MAC, firmware, network) |
| `GET /report` | Power, relay state, temperature, energy |
| `GET /api/v1/temperature` | Detailed temperature with compensation |
| `GET /relay?state=1` | Turn relay on |
| `GET /relay?state=0` | Turn relay off |
