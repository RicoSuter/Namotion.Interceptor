---
title: Subject Discovery
navTitle: Discovery
status: Implemented
---

# Subject Discovery

## Problem

Hub subjects already create their children on their own: a `HueBridge` its lights, a `ShellyDevice` its components, a `SonosSystem` its players, an `EcowittGateway` its sensors and a `NukiBridge` its locks. What is missing is the entry point. To add a device, a user must know which subject type it needs, pick that type in the create dialog, and only then does its Setup component search the network (`FritzBoxSetupComponent`, `HueBridgeSetupComponent`, `NukiBridgeSetupComponent`).

Discovery turns that around: the user asks HomeBlaze to search, sees the devices it found, and picks one. The type follows from the device.

## Prior art

| Platform | UX | Implementation |
|---|---|---|
| Home Assistant | Passive "Discovered" section on the integrations page with Add and Ignore | Matchers declared per integration (zeroconf, ssdp, dhcp, bluetooth, usb, mqtt), one central listener, `async_set_unique_id` for deduplication and IP updates |
| openHAB | Inbox with Approve and Ignore, Scan button per binding | `DiscoveryService` per binding with active and background scans, a stable thing UID and representation property |
| Homey, SmartThings | Pick a brand first, then a pairing wizard that lists found devices | Discovery strategies declared per app (mdns-sd, ssdp, mac), the platform runs the listeners |

Common lessons: shared listeners instead of one socket per integration, a stable identity per device, a found device that hands off to the normal setup flow already filled in, and an Ignore that is remembered.

## Scope and follow-ups

This design covers an on-demand Discover page in the create dialog, with a contract designed so a later background inbox reuses it unchanged.

Implemented (one pull request):

- The discovery contract, registry and scan session
- SSDP and mDNS services
- The Discover page in the create dialog and the handoff to the Setup or Edit component
- Subject discoveries for FRITZ!Box (SSDP), Hue (mDNS), Nuki (cloud lookup) and Shelly (mDNS)

Implemented in the follow-up pull request, stacked on the first:

- Sonos (SSDP), which adds the response headers to `SsdpResponse`
- A UDP service for broadcast requests and broadcast announcements
- Ecowitt (its UDP broadcast request)
- myStrom (the UDP announcements of its switches)
- Included items: the speakers of a Sonos household and the repeaters of a FRITZ!Box mesh, shown under their row

Out of scope, with the contract kept compatible:

- A background inbox with a notification badge
- Ignore, which will remember `IDiscoveryResult.Key`
- Updating the address of a known device that moved, which will use `DiscoveredSubject.ExistingSubject`
- Adding several found devices in one step; they are added one after the other, see [Creating several devices](#creating-several-devices)
- Bluetooth, USB and DHCP discovery

## Contract

The API and the rules for implementers are in [Writing a Subject Discovery](../../development/subject-discovery.md): `SubjectDiscovery<TSubject, TResult>` with `DiscoverAsync`, the result records implementing `IDiscoveryResult<TSubject>` with `Matches`, and `ISubjectDiscoveryComponent` on the Setup or Edit component. The base class implements `ISubjectDiscovery`, with `SubjectType` and an untyped `DiscoverAsync`. `IDiscoveryResult<TSubject>` implements the untyped `IDiscoveryResult.Matches`, which is false for a subject of another type. `SubjectDiscoveryService` reports each result as `DiscoveredSubject(SubjectType, Result, ExistingSubject)`, which lives next to it in `HomeBlaze.Services.Discovery` because only the scan and the dialog use it.

- A subject discovery yields each result as soon as it is known, and may yield a device more than once. The scan drops a result whose `Key` the same subject discovery already returned in it, compared ordinally.
- Duplicate detection belongs to each type, through the `Matches` rule of its result. The caller runs the loop: the scan keeps the existing subjects of the subject type once per subject discovery, asks each result whether one of them matches and reports a `DiscoveredSubject` whose `ExistingSubject` is the first match, or null. The framework prescribes no identity property.
- Result records are type specific and implement `IDiscoveryResult<TSubject>` themselves. The existing `FritzBoxDiscoveryResult` and `NukiDiscoveredBridge` become results directly.
- A row is something the user creates. What comes with it, such as the speakers of a Sonos household or the repeaters of a FRITZ!Box mesh, is listed in `IncludedItems`, which defaults to an empty list without allocating. Included items are shown, not created or selected on their own.
- A subject discovery creates no subject. `SubjectFactory.CreateSubject` attaches the subject to the root context, where a hosted subject starts at once, so a subject is only created once the user picks a result.
- A Setup or Edit component takes the picked result through `ISubjectDiscoveryComponent` and casts it to its own record type. What it fills in, preselects or skips is its own decision.

### Registry

`SubjectDiscoveryRegistry` in `HomeBlaze.Services` scans `TypeProvider.Types` for non-abstract `ISubjectDiscovery` types, as `SubjectComponentRegistry` does for components, so plugins provide subject discoveries without extra registration. It creates them through DI with `ActivatorUtilities`, so a constructor can take the services in [Writing a Subject Discovery](../../development/subject-discovery.md#services) or any other service that is not scoped, see [Your own I/O](../../development/subject-discovery.md#your-own-io). Like the components, the subject discoveries are created once, on first use, so all subject discovery types must be loaded before; types added later are not scanned.

It leaves out, with one logged error each, written the first time the create dialog asks for subject discoveries rather than at startup, and without blocking the app:

- A subject discovery that cannot be constructed
- A subject discovery whose subject type has no Setup or Edit component implementing `ISubjectDiscoveryComponent` (`SubjectComponentRegistration.TakesDiscoveryResults`), because a picked result would otherwise open a page that ignores it
- A second subject discovery for a subject type that already has one, because the Discover page tells results apart by subject type and `Key`, which is unique only within one subject discovery. The subject discovery types are ordered by their full name, ordinally, and the first one that is created and accepted wins, so the load order does not decide

The Discover page further leaves out results whose type is not creatable in the dialog, as `SubjectTypeCatalog.GetCreatableTypes` decides (excluded from creation, or single instance and already present).

## Scan session and services

`SubjectDiscoveryService.ScanAsync` runs one scan and passes each subject discovery the token of the scan, which is canceled when the scan ends; the Discover page owns the cancellation of the scans it starts.

- A scan is canceled only by Scan again and by closing the dialog, which disposes the page. Back, picking a row, creating a subject from a row and switching to All types leave it running while the page is hidden, so its results are complete when the user returns.
- Its deadline is 30 seconds, taken from an injected `TimeProvider`. The deadline is a cap, not a wait: the scan ends as soon as every subject discovery has completed, normally after about 6 seconds, the listening window of the myStrom subject discovery.

`ISsdpSearch`, `IMulticastDnsBrowser` and `IUdpDiscovery`, in `HomeBlaze.Abstractions/Discovery`, are described for implementers in [Writing a Subject Discovery](../../development/subject-discovery.md#services). They are implemented in `HomeBlaze.Services` and registered as singletons, and each subject discovery takes the ones it uses through its constructor. Every query is sent: no two subject discoveries ask the same question, so nothing is shared between them, and a UDP call is its own exchange in any case, because a subject discovery streams the datagrams of its own request or port. As the services are singletons, merging identical in-flight queries, if ever needed, belongs inside their implementations without a contract change.

SSDP uses Rssdp, which `HomeBlaze.Services` references, and searches every IPv4 adapter with a listening window of about 3 seconds.

mDNS is a minimal implementation of our own, with no package and no reflection. It sends a PTR query for the service type to `224.0.0.251:5353` on each IPv4 adapter, repeats it after 1 and 3 seconds as RFC 6762 recommends, and combines the PTR, SRV, TXT and A records of the answers into `MulticastDnsServiceInstance`. Queries are sent from an ephemeral port as legacy unicast queries, so devices answer straight back and HomeBlaze does not bind port 5353, which Avahi holds on most Linux hosts. During development, real Shelly Gen2 devices and a Hue bridge answered legacy unicast queries on a Linux host with Avahi running. Binding port 5353 with address reuse remains the documented fallback, and has not been needed so far.

UDP is a small implementation of our own over IPv4, so the subject discoveries open no sockets themselves and a host can replace the service, as the E2E host does. `BroadcastAsync` sends the request from one socket bound to an ephemeral port to the broadcast address of the subnet of each adapter that is up, is no loopback and supports multicast, such as `192.168.1.255`, so the request leaves on every adapter and not only on that of the default route, as a limited broadcast to `255.255.255.255` does on Linux. Point-to-point links (/31 and /32) have no broadcast address and are skipped, and when no listed adapter has a subnet broadcast address the request goes to `255.255.255.255`. It repeats the request once after 1 second against packet loss and yields the answers that arrive at the socket within the window, so a device that receives both requests answers twice. `ListenAsync` binds the port on any IPv4 address with address reuse, set before binding, and yields the datagrams that arrive within the window. Whether the port is shared depends on the platform: on Linux, concurrent scans and other listeners on the host that also use address reuse share it, while a listener that holds it without address reuse makes the bind fail; on Windows, address reuse binds over a listener that does not demand exclusive use. A port that cannot be bound throws a `SocketException` from the enumeration, so the scan shows the subject discovery as failed, such as "myStrom switch: search failed", and logs a warning; a user then knows that another program holds the port. Both yield each datagram as it arrives; the end of the window ends the enumeration normally and a cancellation by the caller throws.

Any host on the network can answer, so what one query can make HomeBlaze hold or do is capped:

- An SSDP search returns at most 64 answers for the searched target, each with at most 32 headers. A header value is cut to 256 characters, and a header whose name is longer is dropped. The answers Rssdp collects during the window of about 3 seconds are not capped.
- A UDP exchange reads at most 512 datagrams and yields only those of at most 1024 bytes; longer ones count towards the limit but are skipped, also where the platform cuts them to the buffer instead of failing the receive. A request is at most 1024 bytes and a window at most 30 seconds, and other values throw when the method is called.
- An mDNS browse reads at most 512 messages per adapter and 256 records per message, keeps at most 4096 records and 256 KB of names and TXT entries, after which further records are dropped, and returns at most 64 instances.
- `FritzBoxDiscovery` describes at most 32 locations per search, one of them reserved for `fritz.box`, and reads a bounded description from each. It asks `fritz.box` only when the name resolves to a private IPv4, link-local or IPv6 unique local address: without a FRITZ!Box the name resolves to AVM's public server or not at all, so the `fritz.box` fallback sends no request to the internet.
- The Sonos subject discovery reads at most 64 device descriptions per scan, each of at most 64 KB, all within one deadline of 2 seconds, and keeps at most 64 characters of a room or model name.

## Discover page

The picker page of `SubjectSetupDialog` gets a toggle at the top: All types or Discover. It is shown only when the registry has a subject discovery for a type creatable in this dialog, and never when the dialog is opened for a fixed type (`SubjectSetupOptions.Type`).

`SubjectDiscoveryPage` stays in the tree while the configure page is shown, like the picker page, so its results survive Back.

- The first visit starts a scan. Scan again cancels the running scan and starts a new one. Results are kept until then.
- All subject discoveries run in parallel. Each row shows the icon of the type from `SubjectTypeCatalog`, the result's `DisplayName` and `Detail`, and the type's display name. Rows that are not added yet come first, and each part is ordered by the type's display name, then by the result's `DisplayName` ignoring case and the result's `Key`, so the list moves little while results arrive.
- A row whose `ExistingSubject` is set is shown disabled as "already added" and links to that subject.
- A row with included items has an expander next to it, an icon button with `aria-expanded` and a label such as "Show 10 included items". The row is a container holding the select button, or the "already added" content, and the expander as siblings, because a button inside a button is invalid HTML. The items are collapsed by default and render indented and muted under the row, each as its name and detail, with no control of their own; an "already added" row shows them the same way. Which rows are expanded is kept by subject type and result key, so it survives the re-renders while results stream in, and Scan again collapses them.
- While the scan runs, the header shows a progress indicator; afterwards it shows the number of devices found.
- With nothing found, the page says so and points to All types for adding a device by its address. Battery devices that sleep only answer while awake, which the hint mentions.
- The page always shows a hint that a container on a bridge network receives no multicast and no broadcast, so devices there are added by address or HomeBlaze runs with host networking. A container still has an adapter that claims multicast support, so the case cannot be detected reliably.
- The hint also says, without naming a vendor, that some device types are found through their maker's online service, because a scan otherwise reads as local only. The Nuki subject discovery asks the Nuki cloud lookup, which lists the bridges seen from the public IP address of HomeBlaze.

### Handoff

Picking a row opens the configure page for the result's type, as picking the type does, and keeps the `DiscoveredSubject`.

- `SubjectEditPanel` has a dedicated `DiscoveryResult` parameter, not an `EditorParameters` entry. It passes the result on as the `DiscoveryResult` parameter of the hosted component only when `SubjectComponentRegistration.TakesDiscoveryResults` is true for that component, and never otherwise, because `DynamicComponent` rejects a parameter the component does not declare. That property is the one place that decides whether a component takes results, and the registry uses it as well.
- The name field suggests the result's `DisplayName` when that is a valid name, with a number from 2 appended while it is taken, such as "FRITZ!Repeater 1200 AX 2". It suggests the type's usual name when the `DisplayName` is not valid or not plain. A name the user typed is kept.
- Back returns to the page the type was picked on and discards the created subject, as today.
- Validation, `BeforeSave` and persisting are unchanged.

### Creating several devices

During the first setup a user adds many found devices, so the folder Create operation keeps the dialog open between them instead of opening it, and scanning, once per device.

- `SubjectSetupOptions.OnCreatedAsync` stores each created subject while the dialog is open. Create applies the edits and the configuration, as without `BeforeSave`, and then awaits the callback. `ShowAsync` returns null when it is set. `ShowAsync` throws an `ArgumentException` when it is set without `NameField` or together with `BeforeSave`.
- `ISubjectSetupService.CreateSubjectsAsync` passes its callback on as `OnCreatedAsync`, bound to its own cancellation token, and `CreateSubjectsAndAddToStorageAsync` stores each subject as `{name}.json`. The canvas, grid and markdown callers do not opt in and still close the dialog on Create.
- A subject created from a Discover row returns the dialog to the Discover page and shows "Created" with its name. The row of the device is shown as already added, with a link to the new subject, also when the subject was configured to differ from the device. Every other row not added yet, of a subject type the new subject is an instance of, is asked whether its result matches it, and is shown as added as well, with a link to the new subject, when it does. The added rows move after the rows that are not added, and every row keeps its expanded state. A running scan carries on, and its later results are matched against the subjects that existed when it started. Scan again recognizes the new subject, a single instance type is no longer offered in the type list, the used name is rejected, and the next device gets its own suggested name.
- A subject created from the type list closes the dialog, as before.
- Once a subject was created, Cancel reads Done on the picker page. On the configure page it still reads Cancel, as it discards the form.
- When the callback throws, the dialog reports the error and stays on the configure page. As with `BeforeSave`, the subject counts as handed over only when it has a parent afterwards; otherwise it is discarded later.

## Error handling

- Each subject discovery runs in its own task. An exception other than cancellation ends that subject discovery only: it is logged with the type of the subject discovery and shown as one line on the page, such as "Nuki Bridge: search failed". Results it yielded before stay listed.
- An `OperationCanceledException` from a canceled session is neither logged nor shown.
- An exception thrown once the scan has been canceled or has reached its deadline, such as from a library that disposes its socket, counts as the end of the scan: it is logged at debug level and not shown.
- The SSDP, mDNS and UDP services fail soft: no usable adapter or a socket error returns an empty answer, or ends a UDP call with nothing more yielded, and writes one debug log entry instead of throwing. The exception is a port that `IUdpDiscovery.ListenAsync` cannot bind, which throws, so its subject discovery fails as above. `BroadcastAsync` binds an ephemeral port, which no other program holds, so it stays fail soft.
- A cloud lookup fails soft as well: the Nuki subject discovery finds nothing when the Nuki cloud lookup cannot be reached, answers with an error status or does not answer within 10 seconds, because a network without internet access is not a failed search. An answer larger than the size limit counts as unreachable too, as the HTTP client reports it as a failed request. Other exceptions, such as an answer that is not valid JSON, still end the subject discovery.
- A `Matches` call that throws during a scan ends its subject discovery like any other exception. When the dialog matches the rows against a subject it created, a `Matches` call that throws is logged at debug level and counts as no match.
- A subject discovery that never completes is canceled at the session deadline. The scan ends then even when the subject discovery ignores its cancellation; the later results and failures of the abandoned subject discovery are dropped, a later exception is still logged, at debug level, and no callback of `ScanAsync` runs once it has returned.
- Every string from the network is untrusted. Blazor renders it as text, never as markup.

## v1 subject discoveries

| Type | Discovery | Result | `Matches` of the result |
|---|---|---|---|
| `FritzBox` | `FritzBoxDiscovery` through the injected `ISsdpSearch`, plus the `fritz.box` fallback | `FritzBoxDiscoveryResult`, key `Udn` or else host address | The UDN of the TR-064 description the connected subject loaded, or the host address: either one matching is enough, so a connected router is recognized under another address, such as `fritz.box` for its IP address, and a router replaced at the same address counts as added |
| `HueBridge` | mDNS `_hue._tcp`, local only | A new record with bridge ID and address, key bridge ID | The persisted `BridgeId`, ignoring case; `HueBridge` itself compares bridge IDs ignoring case as well |
| `NukiBridge` | The Nuki cloud lookup, which runs when the user first opens the Discover page and on Scan again | `NukiDiscoveredBridge`, key bridge ID | The host address and port |
| `ShellyDevice` | mDNS `_shelly._tcp` | `ShellyDiscoveredDevice(DeviceId, HostAddress, Application, DisplayName)`, key device ID | The device ID the running subject read from the device, otherwise the host address |
| `SonosSystem` | SSDP `urn:schemas-upnp-org:device:ZonePlayer:1` through the injected `ISsdpSearch`, then the device description of each speaker | `SonosDiscoveredHousehold` with `HouseholdId`, `SeedHost` and the speakers as included items (the speakers themselves stay internal), one per household, key household ID, named after the room of its seed speaker, such as `Sonos (Living Room)`, or `Sonos household` without one | Any player or satellite RINCON ID the running subject knows, or the host of its `SeedHost`, ignoring case |
| `EcowittGateway` | The broadcast request of the Ecowitt local API to UDP port 46000, through the injected `IUdpDiscovery` | `EcowittDiscoveredGateway(MacAddress, HostAddress, DeviceName, FirmwareVersion)`, key MAC address | The MAC address the running subject read from the gateway, ignoring case and separators, or the host address, ignoring case: either one matching is enough |
| `MyStromSwitch` | The announcements myStrom devices broadcast to UDP port 7979 about every 5 seconds, received through the injected `IUdpDiscovery` | `MyStromDiscoveredSwitch(MacAddress, HostAddress, DeviceType)`, key MAC address | The MAC address the running subject read from the switch, ignoring case and separators, or the host address, ignoring case: either one matching is enough |

- A Gen2+ Shelly announces two `_shelly._tcp` instances with the same SRV host, one named by its device ID and one by its user-given name. The subject discovery takes the device ID from the SRV host name (without `.local`, lower case) and skips an instance without one, whose name could be the user-given one; it merges the instances into one result and shows the user-given name, or the device ID when there is none. `Application` comes from the `app` TXT record.
- Matching uses only what a subject already has: its configured host address and the identity the running subject read from the device, the `DeviceInfo.Id` of a Shelly and the UDN of the TR-064 description the FRITZ!Box connection loaded. No identity is persisted for discovery, so a stored one cannot go stale when the user edits the host address or a device is replaced, and discovery adds no requests to a running subject. The cost is that a device that moved to another address can show as new: a Shelly that moved before its subject connected since HomeBlaze started, as the subject keeps the ID it read until its host address changes, and a FRITZ!Box router while its subject is not connected to it.
- `FritzBoxDiscovery` is the subject discovery of `FritzBox` and keeps a static `SearchAsync`, which takes the SSDP search as a parameter, so the Setup component shares its parsing and description. `NukiBridgeDiscovery` likewise keeps the static members the Setup component calls.
- In a FRITZ!Box mesh, the clients of the repeaters appear under the router, so a repeater is rarely a subject of its own. Discovery cannot see which mesh a repeater belongs to, so when it finds exactly one router, `FritzBoxDiscovery` lists the repeaters as included items of that router, each with its friendly name or model and its host address, instead of as rows. With several routers or none, each repeater stays a row, and the Setup component explains the mesh when one is picked. `FritzBoxDiscovery.SearchAsync` itself still returns the repeaters as results.
- The Setup components keep their own search for when the type is picked directly. The Hue Setup component keeps the HueApi locator with its network scan fallback.
- Every Sonos speaker answers the search, satellites included, and names its household in the `X-RINCON-HOUSEHOLD` header. The subject discovery skips an answer whose USN does not start with `uuid:RINCON_` and 1 to 32 hex digits (a speaker has 17), that has no such header, or whose LOCATION is no plain HTTP address with a host and port, such as `urn:x`, because the speaker is asked at that host and port. As a speaker announces an IP address, the host must be an IPv4 or IPv6 literal that is not a loopback or unspecified address, so a hostile answer cannot point the requests at a host name, such as one on the internet, or at HomeBlaze's own host. It takes the speaker's RINCON ID from the USN and groups the speakers by household ID. The seed host is that of the speaker with the lowest RINCON ID, so a household gives the same seed in every scan in which that speaker answers. `SonosSystem` has no household setting, so the seed is what pins a new system to the household.
- The SSDP answer of a speaker carries only IDs, so the Sonos subject discovery reads the device description of each speaker for its `roomName` and `modelName`. Each speaker becomes an included item named by its room, with its model and IP address as detail, ordered by room. The SSDP answer carries no sender address, so its LOCATION is the only hint where the speaker is, and anything on the network can advertise any host there. As `FritzBoxDiscovery` does, the subject discovery therefore takes only the host and port from the LOCATION and requests the fixed path `/xml/device_description.xml` over plain HTTP, the path a connection reads, ignoring the advertised scheme, path and query; its own handler follows no redirect, so a redirect fails the request, and uses no proxy. The requests run in parallel within one deadline of 2 seconds, bounded by the scan token, and read at most 64 KB each; the parser refuses a document type declaration and nests at most 64 levels, at most 64 descriptions are read per scan, and a room or model name is cut to 64 characters. When a description does not answer in time, answers with an error or a redirect, is too large, names an unknown character set or cannot be parsed, the item falls back to the speaker's RINCON ID and IP address, so the household is still listed.
- The Ecowitt subject discovery broadcasts the request `FF FF 12 00 04 16` (header, command `CMD_BROADCAST`, size, checksum) to port 46000 through `IUdpDiscovery.BroadcastAsync`, which repeats it after 1 second, and listens for 3 seconds. Each gateway answers with a unicast frame that carries its MAC address, IPv4 address, API port and a name such as `GW2000A-WIFIE157 V3.3.3`; the first word is shown as the name and the rest as the firmware version. As for myStrom, the host address is the address the reply came from, not the address the frame names. The subject discovery drops a frame whose header, command, size, name length or checksum is wrong; a valid frame is at most 274 bytes long, well within the cap of the service. A gateway also announces the same frame on UDP port 59387, which the subject discovery does not listen to, so it binds no fixed port. The broadcast never leaves a Docker bridge network.
- The myStrom subject discovery listens on UDP port 7979 through `IUdpDiscovery.ListenAsync`, which on Linux shares the port with concurrent scans and other listeners that use address reuse, for 6 seconds, longer than the announcement interval of about 5 seconds. An announcement is exactly 8 bytes: the MAC address (6), the numeric device type (1) and status flags (1). It carries no IP address, so the host address is the sender of the datagram. The subject discovery keeps the types that `MyStromSwitch` supports, 106 (Switch CH v2, `WS2`) and 107 (Switch EU, `WSE`), and drops the others, such as bulbs, buttons, LED strips and motion sensors; the [myStrom REST API documentation](https://api.mystrom.ch) lists the report, relay and temperature requests the subject polls for these two only, so the Switch CH v1 (101) and the Switch Zero (120) are left out. A switch announces no name, so it is shown as `myStrom Switch` with the last six digits of its MAC address, and its type and address as the second line. The subject discovery drops any datagram that is not exactly 8 bytes long. A host firewall must allow inbound UDP on port 7979, and the broadcasts never reach a Docker bridge network.
- `FritzBoxSetupComponent`, `HueBridgeSetupComponent`, `NukiBridgeSetupComponent`, `ShellyDeviceEditComponent`, `SonosSystemEditComponent`, `EcowittGatewayEditComponent` and `MyStromSwitchEditComponent` implement `ISubjectDiscoveryComponent`. The Setup components preselect the result in their lists; the Shelly component fills in the host address and, when the device has a user-given name, the name, the Sonos component `SeedHost`, the Ecowitt and myStrom components the host address.
- `SonosSystemEditComponent` rejects an `EventPort` that another `SonosSystem` known to the registry already uses, through `SonosSystem.FindSystemUsingEventPort`, because a household picked on the Discover page keeps the default port, and of two systems on one port only the first receives events. It does not fill in a free port.

## Testing

Unit tests, without sockets except where named:

- The base class yields the typed results, and the untyped `IDiscoveryResult.Matches` is false for a subject of another type
- `SubjectDiscoveryService` drops repeated keys per subject discovery, sets `ExistingSubject` from the result's `Matches` against the subjects of the type, and treats a throwing `Matches` as a failure of the subject discovery
- The registry finds subject discoveries through `TypeProvider` and applies its exclusion rules
- `SubjectDiscoveryService` isolates failing subject discoveries, honors cancellation and ends at its deadline, driven by a `TimeProvider` the test advances
- mDNS packet parsing from captured bytes, including name compression, malformed packets and the caps
- Each subject discovery constructed with fake `ISsdpSearch`, `IMulticastDnsBrowser`, `IUdpDiscovery` or HTTP handlers, the `Matches` rule of its result called directly, the FRITZ!Box grouping of repeaters and the Sonos descriptions with their timeout, size cap, hostile documents and fallback
- Two Sonos tests that run the subject discovery with its own HTTP handler against a loopback HTTP server, to show that it requests the fixed description path and follows no redirect; their deadline runs on a `FakeTimeProvider` that is never advanced
- The argument checks of the UDP service and the broadcast address of a subnet

Loopback tests of `UdpDiscovery.ListenAsync`, on a free port with datagrams sent to `127.0.0.1`: an overlong datagram is skipped and the next one yielded, an empty datagram is yielded with its sender address, the end of the window ends the enumeration normally, the caller's cancellation during a receive throws, the 512-datagram cap ends the enumeration, and a port another socket holds without address reuse (with exclusive use on Windows) throws a `SocketException`. `BroadcastAsync` sends to the broadcast addresses of the real adapters, so it is verified manually, see below.

This repository has no component test framework, so the Discover page keeps its logic in `SubjectDiscoveryList` (ordering, marking the picked row and every row whose result matches the new subject added, a throwing `Matches`, failures, expanded rows, the expander label), `SubjectNameOptions.SuggestName` (the name) and `SubjectEditPanel.CreateHostedComponentParameters` (passing the result), which unit tests cover. The E2E test covers the page itself.

Two E2E tests with a fake subject discovery that finds two devices and a hub with included items. The first opens Discover from the folder Create, picks the first device, creates it, sees the dialog return to the Discover page with the device listed as added after the others and Done in the footer, creates the second device without a new scan, finds both stored subjects, and sees Scan again match both devices to them; creating from the type list still closes the dialog, which the other create tests cover. The second expands the hub, on a desktop and a phone viewport, and finds its items shown and not selectable. The E2E host replaces the SSDP, mDNS and UDP services with empty ones and stubs the Nuki cloud lookup, so a scan there neither searches the network of the machine nor binds its ports, which a fixture test checks.

Manual verification against a real network: the Discover page found a FRITZ!Box with three repeaters, a Hue bridge, a Nuki bridge and several Shelly Gen2 devices, each Shelly once, under its user-given name when it has one. The Sonos subject discovery, run against the same network from a console, found one household with the 10 speakers that answered, chose the same seed speaker in repeated scans, and recognized a system whose `SeedHost` named one of them. The Ecowitt subject discovery, run from a console, found a GW2000A within 30 ms in each scan and recognized a gateway configured with its address. The myStrom subject discovery, run from a console, found all 7 WiFi Switch CH v2 within 5 seconds in each of two scans, also with two scans at once, and yielded nothing, with one debug log entry, while another socket held port 7979 without address reuse. After both moved to `IUdpDiscovery`, a console run of both found the GW2000A within 16 ms through the broadcast to `192.168.1.255`, which it answered twice, and all 7 switches in each of two scans, also with two myStrom subject discoveries at once; the service again yielded nothing, with one debug log entry, while port 7979 was held without address reuse, and rethrew a cancellation by the caller. A held port has since been changed to throw, which the loopback test covers. On the same network, the Discover page itself listed the Sonos household with all 10 speakers by room under it and the FRITZ!Box router with its three repeaters folded under it as included items, and found the Ecowitt GW2000A and the 7 myStrom switches as well. Docker bridge networking was not tested; the page shows a hint for it.
