---
title: myStrom WiFi Switch
icon: Power
---

# myStrom WiFi Switch

The myStrom WiFi Switch is a smart plug with built-in power metering, temperature sensing, and relay control over a local HTTP API.

## Discovery

The Discover page of the create dialog finds myStrom WiFi Switch CH v2 (`WS2`) and Switch EU (`WSE`) devices by the 8-byte announcement each one broadcasts to UDP port 7979 about every 5 seconds, so it listens for about 6 seconds. Other myStrom devices, such as bulbs, buttons and motion sensors, are not listed. A switch announces no name, so it is shown as `myStrom Switch` with the last six digits of its MAC address, such as `myStrom Switch 18ADAC`, together with its type and IP address. Picking one fills in the host address. A switch that is already added is recognized by its MAC address, which the subject reads from the switch once it has connected, or by its host address.

The announcements are broadcasts, so HomeBlaze in a Docker container finds switches only with host networking, and a firewall on the HomeBlaze host must allow inbound UDP on port 7979.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name |
| `HostAddress` | string | null | Device IP address or host name |
| `AllowTurnOff` | bool | true | Safety flag to prevent relay shutdown |
| `PollingInterval` | TimeSpan | 15 seconds | How often to poll the device |
| `RetryInterval` | TimeSpan | 30 seconds | Delay after connection error |

A connection to the device that cannot be established is detected within 10 seconds, for polling and for operations. A poll that the device does not answer within 10 seconds counts as failed, so the switch is shown as an error and reconnected instead of keeping its last values. Operations may take longer than 10 seconds once connected.

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
| `TotalImportedEnergy` | WattHour | Energy since the last device boot (restarts at 0 on reboot) |
| `TotalExportedEnergy` | WattHour | Always null (the device does not measure export) |
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
- `INetworkHost` - configured host address
- `IMonitoredService` - service status
- `IUptimeState` - device uptime

## JSON Configuration Example

```json
{
  "$type": "Namotion.Devices.MyStrom.MyStromSwitch",
  "name": "Kitchen Switch",
  "hostAddress": "192.168.1.59",
  "allowTurnOff": true,
  "pollingInterval": "00:00:15",
  "retryInterval": "00:00:30"
}
```

## HTTP API

The switch exposes a local HTTP API on port 80:

| Endpoint | Purpose |
|----------|---------|
| `GET /info` | Device information (MAC, firmware, network), read on connect and again when the uptime falls, so a restart after a firmware update shows the new version |
| `GET /report` | Power, relay state, temperature, energy |
| `GET /api/v1/temperature` | Detailed temperature with compensation |
| `GET /relay?state=1` | Turn relay on |
| `GET /relay?state=0` | Turn relay off |
