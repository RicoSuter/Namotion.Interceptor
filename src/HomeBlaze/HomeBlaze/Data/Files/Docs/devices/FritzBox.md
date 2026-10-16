---
title: FRITZ!Box
icon: Router
---

# FRITZ!Box

Connects to an AVM FRITZ!Box router over its documented local TR-064 interface, without MyFRITZ or any cloud connection. One `FritzBox` lists every device the router knows as a `FritzBoxClient` with presence, shows the router itself and every FRITZ!Repeater or meshed FRITZ!Box as a `FritzBoxAccessPoint` with the clients connected to it, and reports the internet connection, current traffic rates and the WLANs. The guest WLAN can be switched on and off. Turning it on opens the network to guests, so turning it on and toggling it need confirmation in the operations panel and cannot be called by AI agents or over MCP, see [Operations](#operations); the switch on the dashboard widget acts directly. Everything else is read only.

## Supported Devices

- FRITZ!Box routers with FRITZ!OS that offers the TR-064 host list (`X_AVM-DE_GetHostListPath`, FRITZ!OS 7 and later).
- FRITZ!Repeaters and other FRITZ!Boxes in the mesh appear as access points when the router offers the mesh list (`X_AVM-DE_GetMeshListPath`). Without it, clients are still listed, but without their access point.
- Third-party access points report no topology. Their clients appear as LAN clients on the router port the access point is plugged into.

## Setup

1. In the FRITZ!Box web UI, enable **Home Network > Network > Network Settings > Allow access for applications**.
2. Create a FRITZ!Box user under **System > FRITZ!Box Users** with the **FRITZ!Box settings** permission. A dedicated user is recommended.
3. In HomeBlaze, create a **FRITZ!Box**. The setup searches the network for routers and also tries `fritz.box`; select the one found or enter its address by hand. Repeaters are listed last and marked: in a FRITZ!Box mesh their clients already appear under the router, so add a repeater only when your router is not a FRITZ!Box. The address field accepts an IPv4 address, an IPv6 address, or a host name, without a scheme or a port.
4. Enter the user and password and select **Test Connection**.
5. The router's certificate is self-signed, so your browser normally warns about it; open `https://<router address>` once and read the certificate details from that warning page or the padlock. Compare that SHA-256 fingerprint with the one HomeBlaze shows here, and tick the confirmation box only once they match, then save.

The router's certificate key is pinned on first connection; see [Certificate Trust](#certificate-trust) for how it is trusted and how to accept a new one.

## Configuration

| Property | Type | Default | Description |
|---|---|---|---|
| `Name` | string | `FRITZ!Box` | Title |
| `HostAddress` | string | | IP address or host name of the router. Empty stops the subject. |
| `Username` | string | | FRITZ!Box user with the FRITZ!Box settings permission |
| `Password` | string | | Password of that user, stored as a secret |
| `PublicKeyPin` | string | | Base64 SHA-256 of the trusted certificate's public key. Empty trusts the key the router presents next and stores it. |
| `PollingInterval` | TimeSpan | 15 seconds | How often the router is polled |
| `RetryInterval` | TimeSpan | 30 seconds | Wait after a failed connection |

Both intervals accept 5 seconds to 1 hour; a value outside that range is clamped, and zero or less uses the default.

```json
{
  "$type": "Namotion.Devices.FritzBox.FritzBox",
  "name": "FRITZ!Box",
  "hostAddress": "192.168.178.1",
  "username": "homeblaze",
  "password": null,
  "publicKeyPin": null,
  "pollingInterval": "00:00:15",
  "retryInterval": "00:00:30"
}
```

## Status

| `Status` | When | `StatusMessage` |
|---|---|---|
| Stopped | `HostAddress` empty | "No host address configured" |
| Starting | Connecting | "Connecting..." |
| Running | Polling | Empty; "Data may be stale (n of 3): ..." after failed polls; or a warning when the trusted certificate could not be saved |
| Error | No password configured, connection failed, login failed, certificate changed, or firmware too old | The cause |

## Subject Model

```
FritzBox
├── Clients[MAC]                 every device the router lists, except the mesh access points themselves
├── AccessPoints[MAC]            the router and every meshed FRITZ! device
│     └── Clients[MAC]           the present clients connected there, the same subjects as above
└── Wlans[Role]                  2.4GHz, 5GHz, 6GHz and Guest
```

Clients and access points are keyed by MAC address in upper case, so their paths stay stable across restarts. A client that moves to another access point keeps its subject; only the two access point lists change. History records a client under `FritzBox.Clients`, wherever it is connected. See [Shared Child Subjects](../development/building-subjects.md#shared-child-subjects) for the general rule.

WLANs are keyed by the band their radio reports: `2.4GHz`, `5GHz` or `6GHz`. When the router offers several WLAN services, the last one is taken as the guest network and keyed `Guest`. A radio that reports no known band is keyed by its position instead: the first three as `2.4GHz`, `5GHz` and `6GHz`, any later one as `WLAN<n>`, where n is its position. A second radio on a band already taken gets its position appended, for example `5GHz-3` for the second 5 GHz radio of a FRITZ!Box 4060.

## Widgets

- **FRITZ!Box:** connection, internet state, present and total clients, current upload and download rates, external IP, a link to the router's own web UI.
- **Access point:** model, uplink type and speed, number of connected clients.
- **Client:** presence, IP address, access point name and band or LAN port, link speed.
- **WLAN:** band, channel and clients; the guest WLAN has an on/off switch, which acts directly without confirmation.

## Interfaces

| Subject | Interfaces |
|---|---|
| `FritzBox` | `IConfigurable`, `IMonitoredService`, `IConnectionState`, `INetworkHost`, `IHubDevice`, `IDeviceInfo`, `ISoftwareState`, `IUptimeState`, `ITitleProvider`, `IIconProvider`, `ILastUpdatedProvider` |
| `FritzBoxClient` | `IPresenceSensor`, `INetworkAdapter`, `ITitleProvider`, `IIconProvider` |
| `FritzBoxAccessPoint` | `IDeviceInfo`, `ISoftwareState`, `INetworkAdapter`, `IConnectionState`, `ITitleProvider`, `IIconProvider` |
| `FritzBoxWlan` | `ISwitchState`, `ITitleProvider` |
| `FritzBoxGuestWlan` | `ISwitchState`, `ISwitchController`, `ITitleProvider` |

## State

### FritzBox

| Property | Unit | Description |
|---|---|---|
| `IsInternetConnected` | | Whether the internet connection is up |
| `ExternalIpAddress`, `ExternalIpv6Address` | | Addresses of the internet connection; `ExternalIpv6Address` is empty when the connection has no public IPv6 address |
| `InternetConnectionUptime` | | Time since the connection was established |
| `MaximumUploadRate`, `MaximumDownloadRate` | Mbit/s | Line speed |
| `UploadRate`, `DownloadRate` | kB/s | Current traffic, converted from the bytes per second the router reports |
| `ClientCount`, `PresentClientCount` | | All listed and currently present clients |
| `Manufacturer`, `Model`, `SerialNumber`, `HardwareRevision` | | Router identity |
| `SoftwareVersion`, `AvailableSoftwareUpdate`, `Uptime` | | Firmware and uptime |
| `PresentedPublicKeyPin` | | The key the router presents while it is not the trusted one |
| `PresentedCertificateFingerprint` | | SHA-256 fingerprint of `PresentedPublicKeyPin`'s certificate, the form browsers show, to compare before accepting it in the edit dialog |

### FritzBoxClient

| Property | Unit | Description |
|---|---|---|
| `IsPresent` | | Whether the router sees the device as active |
| `HostName`, `IpAddress`, `MacAddress` | | Identity |
| `InterfaceType`, `IsWireless`, `IsGuest` | | How it is connected |
| `AccessPointName`, `AccessPointMacAddress` | | Where it is connected, empty while absent or unplaced |
| `Band`, `Port` | | WLAN band or LAN port, empty while absent; a client the mesh list does not place has no band but can have the host list's port, see [How It Works](#how-it-works) |
| `LinkSpeed` | Mbit/s | Current link speed; a client the mesh list does not place falls back to the host list's speed while present |
| `DevicePath` | | Path of the subject of this device, see [Linked Devices](#linked-devices) |

### FritzBoxAccessPoint

| Property | Unit | Description |
|---|---|---|
| `IsConnected` | | Whether the device is part of the mesh |
| `MeshRole` | | Master for the router, Slave for repeaters |
| `UplinkType` | | Link type towards the router (LAN or WLAN) |
| `UplinkSpeed` | Mbit/s | Current speed of that link |
| `ClientCount` | | Present clients connected here |

### FritzBoxWlan

| Property | Unit | Description |
|---|---|---|
| `IsOn` | | Whether the WLAN is enabled |
| `Ssid`, `Band`, `Channel` | | Radio settings |
| `ClientCount` | | Connected clients |

## Operations

| Subject | Operation | Description |
|---|---|---|
| `FritzBoxGuestWlan` | `TurnOn`, `TurnOff`, `Toggle` | Switches the guest WLAN. `TurnOn` and `Toggle` require confirmation in the operations panel and cannot be called by AI agents or over MCP, `TurnOff` does not; the switch on the dashboard widget acts directly. The main bands cannot be switched, because switching one off can cut off the connection HomeBlaze itself uses. |

Operations that require confirmation cannot be called through MCP, neither by [AI agents](../administration/ai-agents.md#tools-and-permissions) nor by external clients; turning the guest WLAN off stays available to them.

## How It Works

The router is polled every `PollingInterval`, read only. Each poll starts with its own login check, so a password changed on the router costs one failed login, not one per request. The mesh list places each present client at an access point with its band or LAN port and link speed; a client it does not place, for example because the router offers no mesh list or the client sits behind a third-party access point, falls back to the host list's port and link speed while present. Existing subjects are updated in place.

### Linked Devices

After each poll, a client's `DevicePath` points to the subject of the same device: by MAC address (`INetworkAdapter`), otherwise by IP address or host name (`INetworkHost`) while that subject is connected. A client that several subjects match is not linked.

### Certificate Trust

The router's certificate is self-signed, so its public key, not its certificate chain, is what gets checked against `PublicKeyPin`.

- **No `PublicKeyPin` configured:** the connection trusts the key its first handshake presents, and refuses a later handshake on that connection with a different key. The trusted key is written to `PublicKeyPin` and saved through the subject's configuration storage; when that storage is not available, for example in a standalone host, the key is kept in memory only, and `StatusMessage` carries a warning until a save succeeds, retried on every reconnect.
- **A key other than the configured `PublicKeyPin`:** the connection fails and the key is shown in `PresentedPublicKeyPin` and `PresentedCertificateFingerprint`. Accepting it with **Accept New Certificate** in the edit dialog takes effect once the dialog is saved.

## Network and Firewall

HomeBlaze connects to the router on TCP port 49000 (description, unauthenticated) and on the HTTPS port the router reports, usually 49443. Discovery uses SSDP multicast on UDP port 1900.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The FRITZ!Box did not answer, or setup finds no router | The address is wrong, "Allow access for applications" is off, or port 49000 is blocked by a firewall; check those first. Enter the address by hand when discovery cannot reach the router, for example in a container on a bridge network. |
| "No password configured." | `Password` is empty, so the router is not contacted. Saving a password connects at once. |
| "Login failed: check user, password and the FRITZ!Box settings permission." | Wrong user or password, or the user lacks the permission. Shown the same way when a later poll is refused, for example after the password or permission changed while the connection was running. The next attempt waits 15 minutes, doubling up to 2 hours, because every failed login lengthens the router's lockout; saving the configuration retries at once. |
| "The FRITZ!Box presented a certificate other than the trusted one..." | The router's certificate key changed; see [Certificate Trust](#certificate-trust). |
| Certificate error right after changing `HostAddress` | After changing the address to another router, accept its certificate (the pinned key belongs to the previous router). |
| "The FRITZ!Box does not offer its host list..." | The firmware is too old. Update FRITZ!OS, then save the configuration or restart HomeBlaze, since the subject only reconnects on a configuration change. |
| Clients have no access point | The router does not offer the mesh list, the mesh list failed to download or was malformed, or the client is connected through a third-party access point. |
| "Data may be stale (n of 3)" | A poll failed; after three in a row the connection is reset and retried after `RetryInterval`. |
| Internet values, rates and line speed stay empty | The subject is a FRITZ!Repeater, which offers no internet services, or the user cannot read those services on the router. |
| "The FRITZ!Box is not connected. Try again when its status is Running." when switching the guest WLAN | The router is not connected right now; the switch works again once `Status` is Running. |
| "The device refused SetEnable..." when switching the guest WLAN | The router refused the command with the UPnP error shown; "action not authorized (UPnP error 606)" means the user lacks the FRITZ!Box settings permission. |

## Limitations

- Phones and tablets that use private, rotating MAC addresses appear as a new client for every address, and the router keeps the old ones.
- Byte totals are not reported, only current rates.
- The mesh list does not say which way a link between two repeaters points, so the uplink is guessed: a link to the router first, then a link on an interface named `UPLINK...`, then the first connected link. A repeater daisy-chained behind another repeater can therefore show the wrong uplink.
- SSDP discovery uses IPv4 only.

## Follow-ups

- Byte totals from a 64-bit counter source.
- Hiding long-inactive clients.
- Wake-on-LAN and internet blocking per client.
- Reboot and reconnect.

## References

- [AVM TR-064 documentation](https://avm.de/service/schnittstellen/)
