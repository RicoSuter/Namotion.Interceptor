---
title: FRITZ!Box
icon: Router
---

# FRITZ!Box

Connects to an AVM FRITZ!Box router over its documented local TR-064 interface, without MyFRITZ or any cloud connection. One `FritzBox` lists every device the router knows as a `FritzBoxClient` with presence, shows the router itself and every FRITZ!Repeater or meshed FRITZ!Box as a `FritzBoxAccessPoint` with the clients connected to it, and reports the internet connection, current traffic rates and the WLANs. The guest WLAN can be switched on and off; everything else is read only.

## Supported Devices

- FRITZ!Box routers with FRITZ!OS that offers the TR-064 host list (`X_AVM-DE_GetHostListPath`, FRITZ!OS 7 and later).
- FRITZ!Repeaters and other FRITZ!Boxes in the mesh appear as access points when the router offers the mesh list (`X_AVM-DE_GetMeshListPath`). Without it, clients are still listed, but without their access point.
- Third-party access points report no topology. Their clients appear as LAN clients on the router port the access point is plugged into.

## Setup

1. In the FRITZ!Box web UI, enable **Home Network > Network > Network Settings > Allow access for applications**.
2. Create a FRITZ!Box user under **System > FRITZ!Box Users** with the **FRITZ!Box settings** permission. A dedicated user is recommended.
3. In HomeBlaze, create a **FRITZ!Box**. The setup searches the network for TR-064 routers (requests at `http://<host>:49000/tr64desc.xml`, the same request an opened connection makes) and also tries `fritz.box`; select the router found or enter its address by hand. Multicast often does not reach a container on a bridge network, so enter the address by hand there. The address field accepts an IPv4 address, an IPv6 address with or without brackets, or a host name, without a scheme or a port.
4. Enter the user and password and select **Test Connection**. Compare the shown certificate fingerprint with the one your browser shows for `https://<router address>` and confirm it.

The router's certificate key is pinned. When it changes, the router reports an error and the edit dialog offers **Accept New Certificate**, showing the new fingerprint to compare again.

## Configuration

| Property | Type | Default | Description |
|---|---|---|---|
| `Name` | string | `FRITZ!Box` | Title |
| `HostAddress` | string | | IP address or host name of the router. Empty stops the subject. |
| `Username` | string | | FRITZ!Box user with the FRITZ!Box settings permission |
| `Password` | string | | Password of that user, stored as a secret |
| `CertificatePin` | string | | Base64 SHA-256 of the trusted certificate's public key. Empty trusts the key the router presents next and stores it. |
| `PollingInterval` | TimeSpan | 15s | Interval of the poll. Clamped to 5 s through 1 h; zero or less uses the default. |
| `RetryInterval` | TimeSpan | 30s | Wait after a failed connection. Clamped to 5 s through 1 h; zero or less uses the default. |

```json
{
  "$type": "Namotion.Devices.FritzBox.FritzBox",
  "name": "FRITZ!Box",
  "hostAddress": "192.168.178.1",
  "username": "homeblaze",
  "password": "",
  "certificatePin": null,
  "pollingInterval": "00:00:15",
  "retryInterval": "00:00:30"
}
```

## Status

| `Status` | When | `StatusMessage` |
|---|---|---|
| Stopped | `HostAddress` empty | "No host address configured" |
| Starting | Connecting | "Connecting..." |
| Running | Polling | Empty; "Data may be stale (n of 3): ..." after failed polls; a warning when the trusted certificate could not be saved |
| Error | Connection failed, login failed, certificate changed, or firmware too old | The cause |

## Subject Model

```
FritzBox
├── Clients[MAC]                 every device the router lists
├── AccessPoints[MAC]            the router and every meshed FRITZ! device
│     └── Clients[MAC]           the present clients connected there, the same subjects as above
└── Wlans[Role]                  2.4GHz, 5GHz, 6GHz and Guest
```

Clients and access points are keyed by MAC address in upper case, so their paths stay stable across restarts. A client that moves to another access point keeps its subject; only the two access point lists change. History records a client under `FritzBox.Clients`, wherever it is connected. See [Shared Child Subjects](../development/building-subjects.md#shared-child-subjects) for the general rule.

## Widgets

- **FRITZ!Box:** connection, internet state, present and total clients, current upload and download rates, external IP, a link to the router's own web UI.
- **Access point:** model, uplink type and speed, number of connected clients.
- **Client:** presence, IP address, access point name and band or LAN port, link speed.
- **WLAN:** band, channel and clients; the guest WLAN has an on/off switch.

## Interfaces

| Subject | Interfaces |
|---|---|
| `FritzBox` | `IConfigurable`, `IMonitoredService`, `IConnectionState`, `IHubDevice`, `IDeviceInfo`, `ISoftwareState`, `IUptimeState`, `ITitleProvider`, `IIconProvider`, `ILastUpdatedProvider` |
| `FritzBoxClient` | `IPresenceSensor`, `INetworkAdapter`, `ITitleProvider`, `IIconProvider` |
| `FritzBoxAccessPoint` | `IDeviceInfo`, `ISoftwareState`, `INetworkAdapter`, `IConnectionState`, `ITitleProvider`, `IIconProvider` |
| `FritzBoxWlan` | `ISwitchState`, `ITitleProvider` |
| `FritzBoxGuestWlan` | `ISwitchState`, `ISwitchController`, `ITitleProvider` |

## State

### FritzBox

| Property | Unit | Description |
|---|---|---|
| `IsInternetConnected` | | Whether the internet connection is up |
| `ExternalIpAddress`, `ExternalIpv6Address` | | Addresses of the internet connection |
| `InternetConnectionUptime` | | Time since the connection was established |
| `MaximumUploadRate`, `MaximumDownloadRate` | Mbit/s | Line speed |
| `UploadRate`, `DownloadRate` | kB/s | Current traffic, converted from the bytes per second the router reports |
| `ClientCount`, `PresentClientCount` | | All listed and currently present clients |
| `Model`, `SerialNumber`, `SoftwareVersion`, `AvailableSoftwareUpdate`, `Uptime` | | Router identity and firmware |
| `PresentedCertificatePin` | | The key the router presents while it is not the trusted one |
| `PresentedCertificateFingerprint` | | SHA-256 fingerprint of `PresentedCertificatePin`'s certificate, the form browsers show, to compare before accepting it in the edit dialog |

### FritzBoxClient

| Property | Unit | Description |
|---|---|---|
| `IsPresent` | | Whether the router sees the device as active |
| `HostName`, `IpAddress`, `MacAddress` | | Identity |
| `InterfaceType`, `IsWireless`, `IsGuest` | | How it is connected |
| `AccessPointName`, `AccessPointMacAddress` | | Where it is connected, empty while the device is absent or the mesh list does not place it |
| `Band` | | WLAN band, empty for a wired device, or while absent or unplaced |
| `Port` | | LAN port, taken from the mesh list position when the mesh list places the device there; otherwise falls back to the host list entry's port while the device is present. Empty for a wireless device or when neither source reports one |
| `LinkSpeed` | Mbit/s | Current link speed |

### FritzBoxAccessPoint

| Property | Unit | Description |
|---|---|---|
| `IsConnected` | | Whether the device is part of the mesh |
| `MeshRole` | | Master for the router, Slave for repeaters |
| `UplinkType`, `UplinkSpeed` | Mbit/s | Link towards the router |
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
| `FritzBoxGuestWlan` | `TurnOn`, `TurnOff`, `Toggle` | Switches the guest WLAN. The main bands cannot be switched, because switching one off can cut off the connection HomeBlaze itself uses. |

## How It Works

1. The subject reads `http://<router>:49000/tr64desc.xml` without authentication and asks the router for its HTTPS port over the same unauthenticated HTTP connection.
2. It logs in once with HTTP Digest over HTTPS (`DeviceInfo:1 GetInfo`) and checks the certificate key, then asks the router which internet connection is active. Without a configured password, the connection fails before any login attempt, so a missing password never adds to the router's login lockout.
3. **Certificate trust:** with no `CertificatePin` configured, the connection trusts the key the first handshake presents and refuses any later handshake on that connection with a different key. The trusted key is written to `CertificatePin` and saved through the subject's configuration writer; when no writer is available (for example, a standalone host), it is kept in memory only, and `StatusMessage` carries a warning until a writer saves it, retried on every reconnect. A key that differs from a configured pin fails the connection and is shown in `PresentedCertificatePin` and `PresentedCertificateFingerprint` for the edit dialog's **Accept New Certificate**.
4. Every poll reads, in parallel: device information, firmware updates, the internet connection, the online monitor, every WLAN, the host list and the mesh list. An action the firmware does not offer only leaves its value empty.
5. Existing subjects are updated in place. A collection is replaced only when clients, access points or WLANs appear or disappear.

## Network and Firewall

HomeBlaze connects to the router on TCP port 49000 (description, unauthenticated) and on the HTTPS port the router reports, usually 49443. Discovery uses SSDP multicast on UDP port 1900.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "No password configured" | `Password` is empty; nothing is attempted until one is set. |
| "Login failed" | Wrong user or password, the user lacks the FRITZ!Box settings permission, or "Allow access for applications" is off. A login refused while the connection was already running (for example, the password or permission changed) is treated the same way. The next attempt waits 15 minutes, doubling up to 2 hours, because every failed login lengthens the router's lockout; saving the configuration retries at once. |
| "presented a certificate other than the trusted one" | The router's certificate key changed. Check why, then use **Accept New Certificate** in the edit dialog; the connection waits until then. |
| "does not offer its host list" | The firmware is too old. Update FRITZ!OS. |
| Clients have no access point | The router does not offer the mesh list, or the client is connected through a third-party access point. A mesh list that fails to download, is oversized or malformed is treated the same as a missing one and does not fail the poll; a connection failure or timeout downloading it does fail the poll. |
| "Data may be stale (n of 3)" | A poll failed; after three in a row the connection is reset and retried after `RetryInterval`. |
| Setup finds no router | Multicast does not reach HomeBlaze, for example in a container on a bridge network. Enter the address by hand. |

## Limitations

- Phones and tablets that use private, rotating MAC addresses appear as a new client for every address, and the router keeps the old ones.
- Byte totals are not reported, only current rates.
- Between two repeaters, the uplink is chosen by priority rather than document order: a link to the master first, then a link on an interface named `UPLINK...`, then the first connected mesh link.

## Follow-ups

- Byte totals from a 64-bit counter source.
- Hiding long-inactive clients.
- Wake-on-LAN and internet blocking per client.
- Reboot and reconnect.

## References

- [AVM TR-064 documentation](https://avm.de/service/schnittstellen/)
