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
- Shared SSDP and mDNS services
- The Discover page in the create dialog and the handoff to the Setup or Edit component
- Discoverers for FRITZ!Box (SSDP), Hue (mDNS), Nuki (cloud lookup) and Shelly (mDNS)

Follow-up pull requests, stacked on the first, one each:

- Sonos (SSDP)
- myStrom (its UDP announcements)
- Ecowitt (its UDP broadcast discovery)

Out of scope, with the contract kept compatible:

- A background inbox with a notification badge
- Ignore, which will remember `IDiscoveryResult.Key`
- Updating the address of a known device that moved, which will use `DiscoveredSubject.ExistingSubject`
- Adding several found devices in one step; they are added one after the other, see [Creating several devices](#creating-several-devices)
- Bluetooth, USB and DHCP discovery

## Contract

The API and the rules for implementers are in [Writing a Discoverer](../../development/discoverers.md): `SubjectDiscoverer<TSubject, TResult>` with `DiscoverAsync` and `Matches`, the result records implementing `IDiscoveryResult`, and `ISubjectDiscoveryComponent` on the Setup or Edit component. The base class reports each result as `DiscoveredSubject(SubjectType, Result, ExistingSubject)`.

- A discoverer yields each result as soon as it is known.
- Duplicate detection belongs to each type. The base class keeps the existing subjects of type `TSubject`, calls `Matches` for each result and yields a `DiscoveredSubject` whose `ExistingSubject` is the first match, or null. The framework prescribes no identity property.
- Result records are type specific and implement `IDiscoveryResult` themselves. The existing `FritzBoxDiscoveryResult` and `NukiDiscoveredBridge` become results directly.
- A discoverer creates no subject. `SubjectFactory.CreateSubject` attaches the subject to the root context, where a hosted subject starts at once, so a subject is only created once the user picks a result.
- A Setup or Edit component takes the picked result through `ISubjectDiscoveryComponent` and casts it to its own record type. What it fills in, preselects or skips is its own decision.

### Registry

`SubjectDiscovererRegistry` in `HomeBlaze.Services` scans `TypeProvider.Types` for non-abstract `ISubjectDiscoverer` types, as `SubjectComponentRegistry` does for components, so plugins provide discoverers without extra registration. It creates them through DI with `ActivatorUtilities`, so a constructor can ask for `IHttpClientFactory` or `ILogger<T>`. Like the components, the discoverers are created once, on first use, so all discoverer types must be loaded before; types added later are not scanned.

It leaves out, with one logged error each, written the first time the create dialog asks for discoverers rather than at startup, and without blocking the app:

- A discoverer that cannot be constructed
- A discoverer whose subject type has no Setup or Edit component implementing `ISubjectDiscoveryComponent` (`SubjectComponentRegistration.TakesDiscoveryResults`), because a picked result would otherwise open a page that ignores it

The Discover page further leaves out results whose type is not creatable in the dialog, as `SubjectTypeCatalog.GetCreatableTypes` decides (excluded from creation, or single instance and already present).

## Scan session and shared services

`DiscoveryScan` represents one scan. `SubjectDiscoveryService.ScanAsync` creates and owns it; the Discover page owns the cancellation of the scans it starts.

- A scan is canceled only by Scan again and by closing the dialog, which disposes the page. Back, picking a row, creating a subject from a row and switching to All types leave it running while the page is hidden, so its results are complete when the user returns.
- Its deadline is 30 seconds, taken from an injected `TimeProvider`. The deadline is a cap, not a wait: the scan ends as soon as every discoverer has completed, normally after 3 to 5 seconds.
- It exposes the shared services below. Within one session, a query that was already sent returns the same in-flight task, so two discoverers asking for the same question cause one query. SSDP queries are shared by exact search target, mDNS queries by service type ignoring case.

`ISsdpSearch` and `IMulticastDnsBrowser`, in `HomeBlaze.Abstractions/Discovery`, are described for implementers in [Writing a Discoverer](../../development/discoverers.md#shared-services). They are implemented in `HomeBlaze.Services`.

SSDP uses Rssdp, which `HomeBlaze.Services` references, and searches every IPv4 adapter with a listening window of about 3 seconds.

mDNS is a minimal implementation of our own, with no package and no reflection. It sends a PTR query for the service type to `224.0.0.251:5353` on each IPv4 adapter, repeats it after 1 and 3 seconds as RFC 6762 recommends, and combines the PTR, SRV, TXT and A records of the answers into `MulticastDnsService`. Queries are sent from an ephemeral port as legacy unicast queries, so devices answer straight back and HomeBlaze does not bind port 5353, which Avahi holds on most Linux hosts. During development, real Shelly Gen2 devices and a Hue bridge answered legacy unicast queries on a Linux host with Avahi running. Binding port 5353 with address reuse remains the documented fallback, and has not been needed so far.

Any host on the network can answer, so what one query can make HomeBlaze hold or do is capped:

- An SSDP search returns at most 64 answers for the searched target. The answers Rssdp collects during the window of about 3 seconds are not capped.
- An mDNS browse reads at most 512 messages per adapter and 256 records per message, keeps at most 4096 records and 256 KB of names and TXT entries, after which further records are dropped, and returns at most 64 instances.
- `FritzBoxDiscovery` describes at most 32 locations per search, one of them reserved for `fritz.box`, and reads a bounded description from each.

## Discover page

The picker page of `SubjectSetupDialog` gets a toggle at the top: All types or Discover. It is shown only when the registry has a discoverer for a type creatable in this dialog, and never when the dialog is opened for a fixed type (`SubjectSetupOptions.Type`).

`SubjectDiscoveryPage` stays in the tree while the configure page is shown, like the picker page, so its results survive Back.

- The first visit starts a scan. Scan again cancels the running scan and starts a new one. Results are kept until then.
- All discoverers run in parallel. Each row shows the icon of the type from `SubjectTypeCatalog`, the result's `DisplayName` and `Detail`, and the type's display name. Rows that are not added yet come first, and each part is ordered by the type's display name, then by the result's `DisplayName` ignoring case and the result's `Key`, so the list moves little while results arrive.
- A row whose `ExistingSubject` is set is shown disabled as "already added" and links to that subject.
- While the scan runs, the header shows a progress indicator; afterwards it shows the number of devices found.
- With nothing found, the page says so and points to All types for adding a device by its address. Battery devices that sleep only answer while awake, which the hint mentions.
- The page always shows a hint that a container on a bridge network receives no multicast, so devices there are added by address or HomeBlaze runs with host networking. A container still has an adapter that claims multicast support, so the case cannot be detected reliably.

### Handoff

Picking a row opens the configure page for the result's type, as picking the type does, and keeps the `DiscoveredSubject`.

- `SubjectEditPanel` has a dedicated `DiscoveryResult` parameter, not an `EditorParameters` entry. It passes the result on as the `DiscoveryResult` parameter of the hosted component only when `SubjectComponentRegistration.TakesDiscoveryResults` is true for that component, and never otherwise, because `DynamicComponent` rejects a parameter the component does not declare. That property is the one place that decides whether a component takes results, and the registry uses it as well.
- The name field suggests the result's `DisplayName` when that is a valid, unused name, and the type's usual suggestion otherwise. A name the user typed is kept.
- Back returns to the page the type was picked on and discards the created subject, as today.
- Validation, `BeforeSave` and persisting are unchanged.

### Creating several devices

During the first setup a user adds many found devices, so the folder Create operation keeps the dialog open between them instead of opening it, and scanning, once per device.

- `SubjectSetupOptions.OnCreatedAsync` stores each created subject while the dialog is open. Create applies the edits and the configuration, as without `BeforeSave`, and then awaits the callback. `ShowAsync` returns null when it is set. `ShowAsync` throws an `ArgumentException` when it is set without `NameField` or together with `BeforeSave`.
- `ISubjectSetupService.CreateSubjectsAsync` passes its callback on as `OnCreatedAsync`, with its own cancellation token, and `CreateSubjectsAndAddToStorageAsync` stores each subject as `{name}.json`. The canvas, grid and markdown callers do not opt in and still close the dialog on Create.
- A subject created from a Discover row returns the dialog to the Discover page and shows "Created" with its name. The row of the device is shown as already added, with a link to the new subject, and moves after the rows that are not added. A running scan carries on. Scan again recognizes the new subject, a single instance type is no longer offered in the type list, the used name is rejected, and the next device gets its own suggested name.
- A subject created from the type list closes the dialog, as before.
- Once a subject was created, Cancel reads Done on the picker page. On the configure page it still reads Cancel, as it discards the form.
- When the callback throws, the dialog reports the error and stays on the configure page. As with `BeforeSave`, the subject counts as handed over only when it has a parent afterwards; otherwise it is discarded later.

## Error handling

- Each discoverer runs in its own task. An exception other than cancellation ends that discoverer only: it is logged with the discoverer's type and shown as one line on the page, such as "Nuki Bridge: search failed". Results it yielded before stay listed.
- An `OperationCanceledException` from a canceled session is neither logged nor shown.
- An exception thrown once the scan has been canceled or has reached its deadline, such as from a library that disposes its socket, counts as the end of the scan: it is logged at debug level and not shown.
- The shared services fail soft: no usable adapter or a socket error returns an empty answer and one debug log entry, not an exception.
- A `Matches` call that throws ends its discoverer like any other exception.
- A discoverer that never completes is canceled at the session deadline. The scan ends then even when the discoverer ignores its cancellation; the abandoned discoverer's later results and failures are dropped, a later exception is still logged, at debug level, and no callback of `ScanAsync` runs once it has returned.
- Every string from the network is untrusted. Blazor renders it as text, never as markup.

## v1 discoverers

| Type | Discovery | Result | Matches |
|---|---|---|---|
| `FritzBox` | `FritzBoxDiscovery` through the shared `ISsdpSearch`, plus the `fritz.box` fallback | `FritzBoxDiscoveryResult`, key `Udn` or else host address | A new persisted `Udn`, or the host address: either one matching is enough, so a router that moved is recognized by its UDN and a router replaced at the same address counts as added |
| `HueBridge` | mDNS `_hue._tcp`, local only | A new record with bridge ID and address, key bridge ID | The persisted `BridgeId`, ignoring case; `HueBridge` itself compares bridge IDs ignoring case as well |
| `NukiBridge` | The Nuki cloud lookup, which runs when the user first opens the Discover page and on Scan again | `NukiDiscoveredBridge`, key bridge ID | The host address and port |
| `ShellyDevice` | mDNS `_shelly._tcp` | `ShellyDiscoveredDevice(DeviceId, HostAddress, Application, DisplayName)`, key device ID | The device ID the running subject read from the device, otherwise a new persisted `DeviceId`, otherwise the host address |

- A Gen2+ Shelly announces two `_shelly._tcp` instances with the same SRV host, one named by its device ID and one by its user-given name. The discoverer takes the device ID from the SRV host name (without `.local`, lower case) and skips an instance without one, whose name could be the user-given one; it merges the instances into one result and shows the user-given name, or the device ID when there is none. `Application` comes from the `app` TXT record.
- `FritzBox.Udn` and `ShellyDevice.DeviceId` are new `[Configuration]` properties, written by the component that receives the result. Shelly prefers the device ID the running subject read from the device over the stored one, so a Shelly created before this change is recognized while it is connected, and a stored ID that went stale when the device at the address was replaced does not decide. Subjects do not write their own configuration for discovery.
- `FritzBoxDiscovery.DiscoverAsync` takes the SSDP search as a parameter, so the Setup component and the discoverer share parsing and description.
- The Setup components keep their own search for when the type is picked directly. The Hue Setup component keeps the HueApi locator with its network scan fallback.
- `FritzBoxSetupComponent`, `HueBridgeSetupComponent`, `NukiBridgeSetupComponent` and `ShellyDeviceEditComponent` implement `ISubjectDiscoveryComponent`. The Setup components preselect the result in their lists; the Shelly component fills in the host address and device ID.

## Breaking changes

- `FritzBoxDiscovery.DiscoverAsync(TimeSpan, CancellationToken)` is now `DiscoverAsync(ISsdpSearch, TimeSpan timeout, CancellationToken)`. `timeout` now limits only each description request; the SSDP window is fixed at about 3 seconds. Migration: resolve `ISsdpSearch` from dependency injection (`AddHomeBlazeServices` registers it; the implementation is internal), or pass `DiscoveryScan.Ssdp` inside a discoverer.
- `Namotion.Devices.FritzBox` no longer depends on Rssdp. Migration: add your own package reference if you used it transitively.
- `FritzBoxSetupComponent` now injects `ISsdpSearch`. Migration: a host that shows it must call `AddHomeBlazeServices` or `AddHomeBlazeHostServices`.
- `ISubjectSetupService.CreateSubjectAsync(IStorageContainer, CancellationToken)`, which returned one `CreateSubjectResult?`, is replaced by `CreateSubjectsAsync(IStorageContainer, Func<CreateSubjectResult, CancellationToken, Task> addSubjectAsync, CancellationToken)`, which passes each created subject to the callback, also while the dialog stays open for the next one. Migration: an implementation passes the callback on, such as to `SubjectSetupOptions.OnCreatedAsync`; a caller moves the code that handled the result into the callback.
- `SubjectCreatorExtensions.CreateSubjectAndAddToStorageAsync` is renamed to `CreateSubjectsAndAddToStorageAsync`. Migration: rename the call.

Not breaking:

- `HomeBlaze.Components.Abstractions` now references `HomeBlaze.Abstractions`.
- `HueBridge` matches bridge IDs ignoring case.
- `FritzBox.Udn` and `ShellyDevice.DeviceId` are new configuration properties; stored subjects without them load unchanged.
- `SubjectSetupOptions.OnCreatedAsync` is a new, optional option.

## Testing

Unit tests, without sockets:

- The base class keeps only subjects of its type and sets `ExistingSubject` from `Matches`
- The registry finds discoverers through `TypeProvider` and applies its exclusion rules
- `DiscoveryScan` shares in-flight queries between discoverers
- `SubjectDiscoveryService` isolates failing discoverers, honors cancellation and ends at its deadline, driven by a `TimeProvider` the test advances
- mDNS packet parsing from captured bytes, including name compression, malformed packets and the caps
- Each discoverer against fake `ISsdpSearch`, `IMulticastDnsBrowser` and HTTP handlers, including its `Matches` rules

This repository has no component test framework, so the Discover page keeps its logic in `SubjectDiscoveryList` (ordering, marking a row added, failures), `SubjectNameOptions.SuggestName` (the name) and `SubjectEditPanel.CreateHostedComponentParameters` (passing the result), which unit tests cover. The E2E test covers the page itself.

One E2E test with a fake discoverer that finds two devices: open Discover from the folder Create, pick the first result, create, see the dialog return to the Discover page with the device listed as added after the other one and Done in the footer, create the second device without a new scan, and find both stored subjects. Creating from the type list still closes the dialog, which the other create tests cover.

Manual verification against a real network: the Discover page found a FRITZ!Box with three repeaters, a Hue bridge, a Nuki bridge and several Shelly Gen2 devices, each Shelly once, under its user-given name when it has one. Docker bridge networking was not tested; the page shows a hint for it.
