---
title: Nuki
icon: Lock
---

# Nuki

The Nuki integration reads and controls Nuki smart locks and Nuki Openers through the HTTP API of a Nuki bridge in the local network. It supports Nuki Smart Lock 1.0, 2.0, 3.0 and 4.0, the Nuki Smart Door and the Nuki Opener.

## Setup

1. Enable the HTTP API of the bridge in the Nuki app (Manage my devices, Bridge).
2. Create a **Nuki bridge** in HomeBlaze. The setup lists the bridges that the Nuki discovery service knows for your network; select one or enter the address of the bridge.
3. Select **Pair** and press the button on the bridge within 30 seconds.

The setup reads the token from the bridge and chooses the token mode: `Encrypted` for a hardware bridge, `Plain` for a software bridge.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `HostAddress` | string | null | IP address of the bridge, optionally with the port (8080 by default) |
| `Token` | string (secret) | null | API token of the bridge |
| `TokenMode` | `Encrypted` or `Plain` | `Encrypted` | How requests carry the token, see [Security](#security) |
| `PollingInterval` | TimeSpan | 10 seconds | How often the device list is read. Keep it below 30 seconds, the time the bridge shows a ring of an Opener |
| `RetryInterval` | TimeSpan | 30 seconds | Delay before reconnecting after the connection failed |

## Bridge

| Property | Description |
|----------|-------------|
| `IsConnected`, `Status`, `StatusMessage` | Connection to the bridge. After 3 failed polls in a row the bridge reconnects |
| `BridgeType` | `Hardware` or `Software` |
| `HardwareId`, `ServerId` | IDs of the bridge |
| `SoftwareVersion`, `WifiFirmwareVersion` | Firmware versions (the app version for a software bridge) |
| `Uptime`, `IsServerConnected` | Uptime and connection to the Nuki servers |
| `IpAddress` | IP address from `HostAddress` |
| `Power` | 1 W while connected |
| `Locks` | Smart locks and Smart Doors, keyed by their Nuki ID |
| `Openers` | Openers, keyed by their Nuki ID |

## Smart Lock

Implements `IDoorLock`, `IDoorLatchController`, `IDoorSensor` and `IBatteryState`.

| Property | Description |
|----------|-------------|
| `LockState` | Locked, Locking, Unlocked, Unlocking, Unlatched, Unlatching or Jammed (motor blocked). Unknown while uncalibrated |
| `IsLocked` | True when locked, false when unlocked, unlatched or unlatching |
| `DoorState`, `IsClosed` | State of the door sensor: Open or Closed, unknown without a calibrated sensor |
| `BatteryLevel` | Charge of the batteries |
| `IsBatteryCritical`, `IsBatteryCharging`, `IsKeypadBatteryCritical` | Battery flags of the lock and its keypad |
| `NukiId`, `DeviceType`, `SoftwareVersion`, `LastUpdated` | Identity, firmware and the time of the last state |

| Operation | Confirmation | Description |
|-----------|--------------|-------------|
| `Lock` | No | Locks the door |
| `Unlock` | Yes | Unlocks the door |
| `ToggleLock` | Yes | Unlocks a locked door, locks an unlocked one |
| `Unlatch` | Yes | Unlocks the door and pulls the latch |
| `LockNGo` | No | Unlocks the door and locks it again after a short time |
| `LockNGoWithUnlatch` | Yes | Like `LockNGo`, and pulls the latch |

While an operation runs, `LockState` shows its transition (for example Locking). The bridge answers once the lock has finished, and the state is read from the bridge right after it. An operation that the lock reports as failed shows an error.

## Opener

| Property | Description |
|----------|-------------|
| `OpenerState` | Untrained, Online, RingToOpenActive, Open, Opening or BootRun |
| `IsContinuousModeActive` | Whether every ring opens the door |
| `IsRinging`, `LastRingTime` | Whether someone rang within the last 30 seconds, and when someone last rang |

| Operation | Confirmation | Description |
|-----------|--------------|-------------|
| `OpenDoor` | Yes | Actuates the electric strike |
| `ActivateRingToOpen` | Yes | Opens the door on the next ring |
| `DeactivateRingToOpen` | No | Stops ring to open |
| `ActivateContinuousMode` | Yes | Opens the door on every ring |
| `DeactivateContinuousMode` | No | Stops continuous mode |

## Security

The bridge offers no HTTPS, so all requests travel unencrypted in the local network. With `TokenMode` `Encrypted` the token itself is never sent: each request carries a token encrypted with XSalsa20-Poly1305 that is valid once and for 60 seconds, so a recorded request cannot be replayed. The bridge clock must be within 60 seconds of the HomeBlaze clock. Software bridges only accept `Plain`, which sends the token with every request. The states and operations remain readable by anyone in the network either way.

## Tile

The [DoorLock](../administration/widgets/door-lock.md) widget shows a lock and its door as a tile and locks or unlocks it with one click.

## JSON Configuration Example

```json
{
  "$type": "Namotion.Devices.Nuki.NukiBridge",
  "hostAddress": "192.168.1.50:8080",
  "token": "your-token",
  "tokenMode": "Encrypted",
  "pollingInterval": "00:00:10",
  "retryInterval": "00:00:30"
}
```

## HTTP API

| Endpoint | Purpose |
|----------|---------|
| `GET /info` | Bridge type, IDs, versions, uptime |
| `GET /list` | Devices with their last known state, cached by the bridge |
| `GET /lockAction` | Lock actions, waiting for the result |
| `GET /auth` | Pairing with the button of the bridge |
| `https://api.nuki.io/discover/bridges` | Discovery service used by the setup |

`/lockState` is not used, because it wakes the lock and drains its batteries. Callbacks are not used, because they need an HTTP endpoint that the bridge can reach.
