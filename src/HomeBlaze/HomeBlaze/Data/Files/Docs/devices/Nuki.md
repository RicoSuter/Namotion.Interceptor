---
title: Nuki
icon: Lock
---

# Nuki

The Nuki integration reads and controls Nuki smart locks and Nuki Openers through the HTTP API of a Nuki bridge in the local network. It supports Nuki Smart Lock 1.0, 2.0, 3.0 and 4.0, the Nuki Smart Door and the Nuki Opener. One `NukiBridge` shows every smart lock and Smart Door as a `NukiSmartLock` and every Opener as a `NukiOpener`.

## Setup

1. Enable the HTTP API of the bridge in the Nuki app (Manage my devices, Bridge).
2. Create a **Nuki bridge** in HomeBlaze. The setup lists the bridges that the Nuki discovery service knows for your network; select one or enter the address of the bridge.
3. Select **Pair** and press the button on the bridge within 30 seconds.

The setup reads the token from the bridge and chooses the token mode. It tries `Encrypted` first. When the bridge refuses the encrypted token, the setup reads the bridge type with the plain token and picks `Plain` for a software bridge. When it cannot confirm the mode, the setup shows a warning with the reason: a hardware bridge that refuses the encrypted token keeps `Encrypted`, because its clock is then likely off, and its requests fail until the clock is right or you switch to `Plain` in the settings; a bridge of an unknown type gets `Plain`, which it accepted. See [Security](#security).

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `HostAddress` | string | null | IP address or host name of the bridge, optionally with the port (8080 by default), for example `192.168.1.50`, `nuki-bridge:8080` or `[fd00::5]:8080` for an IPv6 address with a port. A leading `http://` and a trailing slash are ignored; a path, a query, a user name or spaces are refused, and the port must be between 1 and 65535 |
| `Token` | string (secret) | null | API token of the bridge |
| `TokenMode` | `Encrypted` or `Plain` | `Encrypted` | How requests carry the token, see [Security](#security) |
| `PollingInterval` | TimeSpan | 10 seconds | How often the device list is read. Keep it below 30 seconds, the time the bridge shows a ring of an Opener |
| `RetryInterval` | TimeSpan | 30 seconds | Delay before reconnecting after the connection failed |

Both intervals accept 1 second to 1 hour; values outside that range set in JSON are clamped, and zero or less uses the default.

The settings ask for the token again after the host address changes, so that the stored token is never sent to another host.

## Bridge

| Property | Description |
|----------|-------------|
| `IsConnected`, `Status`, `StatusMessage` | Connection to the bridge. It counts as connected once the bridge info and the first device list are read. After 3 failed polls in a row the bridge reconnects |
| `BridgeType` | `Hardware` or `Software` |
| `HardwareId`, `ServerId` | IDs of the bridge |
| `SoftwareVersion`, `WifiFirmwareVersion` | Firmware versions (the app version for a software bridge) |
| `Uptime`, `IsServerConnected` | Uptime and connection to the Nuki servers |
| `IpAddress` | IP address from `HostAddress` |
| `Power` | 1 W while connected |
| `Locks` | Smart locks and Smart Doors (`NukiSmartLock`), keyed by their Nuki ID |
| `Openers` | Openers (`NukiOpener`), keyed by their Nuki ID |

## Devices

Smart locks and Openers share these properties:

| Property | Description |
|----------|-------------|
| `IsConnected`, `Status`, `StatusMessage` | Whether the device is usable: the bridge is connected and reports a state for the device. Otherwise `StatusMessage` tells which of the two is missing |
| `NukiId`, `DeviceType`, `SoftwareVersion`, `LastUpdated` | Identity, firmware and the time of the last state |
| `IsBatteryCritical` | Whether the batteries are at a critical level |

The operations of a device are disabled while it is not connected.

A device is a child of its bridge, so its path is the path of the bridge, then `Locks` or `Openers` and its Nuki ID in brackets. For a bridge stored as `Devices/Nuki.json`, the lock with the Nuki ID 123456789 is `/Devices/Nuki/Locks[123456789]`, and its state is `/Devices/Nuki/Locks[123456789]/LockState`. The Nuki ID shows in the subject browser.

## Smart Lock

`NukiSmartLock` implements `IDoorLock`, `IDoorLatchController`, `IDoorSensor` and `IBatteryState`, and has the properties of [Devices](#devices) too.

| Property | Description |
|----------|-------------|
| `LockState` | Locked, Locking, Unlocked, Unlocking, Unlatched, Unlatching or Jammed (motor blocked). Unknown while uncalibrated |
| `IsLocked` | True when locked, false in every other known state, also while the lock moves or is jammed, unknown while `LockState` is unknown |
| `DoorState`, `IsClosed` | State of the door sensor: Open or Closed, unknown without a calibrated sensor |
| `BatteryLevel` | Charge of the batteries |
| `IsBatteryCritical`, `IsBatteryCharging`, `IsKeypadBatteryCritical` | Battery flags of the lock and its keypad |

| Operation | Confirmation | Description |
|-----------|--------------|-------------|
| `Lock` | No | Locks the door |
| `Unlock` | Yes | Unlocks the door |
| `ToggleLock` | Yes | Unlocks a locked door, locks an unlocked one |
| `Unlatch` | Yes | Unlocks the door and pulls the latch |
| `LockNGo` | Yes | Unlocks the door and locks it again after a short time |
| `LockNGoWithUnlatch` | Yes | Like `LockNGo`, and pulls the latch |

While an operation runs, `LockState` shows its transition (for example Locking). The bridge answers once the lock has finished, and the state is read from the bridge right after it. An operation that the lock reports as failed shows an error. An operation is never sent twice: when the bridge answers that it is busy, the operation fails and can be run again, while reads are retried once.

`LockNGo` and `LockNGoWithUnlatch` are available while the lock is locked. While the lock is jammed, `Lock` and `Unlock` stay available, so that the move can be retried from HomeBlaze as in the Nuki app; `Unlock` still asks for confirmation. A click on the [DoorLock](../administration/widgets/door-lock.md) tile of a jammed lock locks it.

## Opener

`NukiOpener` has the properties of [Devices](#devices) and these:

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

While the Opener opens the door (`Opening`), all its operations are disabled.

To react to a ring, watch `LastRingTime` for a change. The bridge reports a ring for 30 seconds, so `IsRinging` can miss a ring when the poll falls outside that time, for example while a lock action holds the bridge, and two rings within 30 seconds show as one.

## Security

The bridge offers no HTTPS, so requests and responses travel unencrypted in the local network, and anyone who can read that traffic sees the states of the devices. The local network is the security boundary: pair on a trusted network, and keep the bridge and HomeBlaze in a network that untrusted devices cannot reach.

- **Encrypted**: After pairing the token itself is never sent. Each request carries the token encrypted with XSalsa20-Poly1305 together with the time and a random number, which the bridge accepts once and within 60 seconds, so it refuses a recorded request that it has already received. The bridge clock must be within 60 seconds of the HomeBlaze clock. The encrypted token is not bound to the request, though: an attacker who can intercept a request and keep it from reaching the bridge can use its encrypted token for any action within 60 seconds. A short token, and bridge tokens are typically only 6 characters long, can also be guessed offline from one recorded request.
- **Plain**: Software bridges only accept `Plain`, which sends the token with every request, so anyone who records one request can control the devices.
- **Pairing**: The bridge returns the token in plain text when you pair. When the bridge refuses the encrypted token, the setup sends the token once in plain text to read the bridge type, see [Setup](#setup).
- **Storage**: The token is stored in plain text in the JSON file of the bridge, like other secrets; protect the data folder accordingly.
- **Logs**: .NET hides the query of a request URI in its HTTP logs, and with it a plain token, unless the environment variable `DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION` is set.

## Widgets

- **Bridge**: The `NukiBridge` widget shows the connection, the number of locks and Openers, and the firmware.
- **Smart lock**: The `NukiSmartLock` widget shows the lock state, the door, the battery level and battery warnings.
- **Opener**: The `NukiOpener` widget shows the Opener state, continuous mode and the last ring.

Embed them with a `Widget` block as described in [Widget](../administration/widgets/widget.md). The [DoorLock](../administration/widgets/door-lock.md) tile shows a lock and its door and locks or unlocks it with one click. A `Widget` block takes the path of any device and the tile the path of a smart lock, see [Devices](#devices). For example, the tile for a lock of a bridge stored as `Devices/Nuki.json`:

~~~~markdown
:: HomeBlaze.Components.DoorLock Path=/Devices/Nuki/Locks[123456789]
::
~~~~

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
| `GET /lockAction` | Lock actions, waiting for the result. Never sent again when the bridge is busy |
| `GET /auth` | Pairing with the button of the bridge |
| `https://api.nuki.io/discover/bridges` | Discovery service used by the setup |

Responses larger than 1 MB are refused. `/lockState` is not used, because it wakes the lock and drains its batteries. Callbacks are not used, because they need an HTTP endpoint that the bridge can reach.
