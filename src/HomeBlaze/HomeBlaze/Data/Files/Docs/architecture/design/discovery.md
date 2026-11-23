---
title: Subject Discovery
navTitle: Discovery
status: Implemented
---

# Subject Discovery

## Problem

Hub subjects create their children on their own, such as a `HueBridge` its lights. What was missing is the entry point: to add a device, a user had to know which subject type it needs and pick that type before anything searched the network. Discovery turns that around: the user asks HomeBlaze to search, sees the devices it found, and picks one. The type follows from the device.

## Prior art

| Platform | UX | Implementation |
|---|---|---|
| Home Assistant | Passive "Discovered" section on the integrations page with Add and Ignore | Matchers declared per integration (zeroconf, ssdp, dhcp, bluetooth, usb, mqtt), one central listener, `async_set_unique_id` for deduplication and IP updates |
| openHAB | Inbox with Approve and Ignore, Scan button per binding | `DiscoveryService` per binding with active and background scans, a stable thing UID and representation property |
| Homey, SmartThings | Pick a brand first, then a pairing wizard that lists found devices | Discovery strategies declared per app (mdns-sd, ssdp, mac), the platform runs the listeners |

Common lessons: shared listeners instead of one socket per integration, a stable identity per device, a found device that hands off to the normal setup flow already filled in, and an Ignore that is remembered.

## Scope

Discovery is an on-demand Discover page in the create dialog. The contract leaves room, without a change, for a background inbox with a notification badge, an Ignore that remembers `IDiscoveryResult.Key`, and updating the address of a known device that moved through `DiscoveredSubject.ExistingSubject`. None of these exist yet, and neither do Bluetooth, USB or DHCP discovery.

The device libraries implement the subject discoveries; what each one finds and how it recognizes an added device is on its device page, such as [FRITZ!Box](../../devices/FritzBox.md#discovery), [Philips Hue](../../devices/PhilipsHue.md#discovery), [Nuki](../../devices/Nuki.md#discovery), [Shelly](../../devices/Shelly.md#discovery), [Sonos](../../devices/Sonos.md#discovery), [Ecowitt](../../devices/Ecowitt.md#discovery) and [myStrom](../../devices/MyStrom.md#discovery).

## Contract

The API and the rules for implementers are in [Writing a Subject Discovery](../../development/subject-discovery.md). The design decisions behind it:

- A subject discovery only reads the network and creates no subject. `SubjectFactory.CreateSubject` attaches a subject to the root context, where a hosted subject starts at once, so a subject is created only once the user picks a result.
- The scan, not the subject discovery, decides whether a device is already added: it asks each result's `Matches` about the existing subjects of the type, and reports the first match as `DiscoveredSubject.ExistingSubject`. The framework prescribes no identity property, so each type keeps its own rule.
- Matching uses what a subject already has, its configuration and what the running subject read from the device. No identity is persisted for discovery, so none can go stale when the user edits a host address or a device is replaced, and a scan sends no requests to running subjects. The cost is that a device that moved shows as new while its subject does not hold its identity; the device pages say when.
- A row is something the user creates. What comes with it, such as the speakers of a household or the repeaters of a mesh, is listed in `IncludedItems`, shown under the row and never created or picked on its own.
- `DiscoveredSubject` lives in `HomeBlaze.Services.Discovery`, next to the scan, because only the scan and the dialog use it.

### Registry

`SubjectDiscoveryRegistry` in `HomeBlaze.Services` finds the non-abstract `ISubjectDiscovery` types in `TypeProvider.Types`, as `SubjectComponentRegistry` does for components, so plugins add subject discoveries without registration. It creates each once, on first use, through `ActivatorUtilities`, so all subject discovery types must be loaded before then.

It leaves out, with one logged error each the first time the create dialog asks, and without blocking the app:

- A subject discovery that cannot be constructed.
- One whose subject type has no Setup or Edit component implementing `ISubjectDiscoveryComponent` (`SubjectComponentRegistration.TakesDiscoveryResults`), because a picked result would open a page that ignores it.
- A second one for a subject type, because the Discover page tells results apart by subject type and `Key`, which is unique only within one subject discovery. The types are ordered by full name, ordinally, and the first that is created and accepted wins, so the load order does not decide.

The Discover page also leaves out results whose type `SubjectTypeCatalog.GetCreatableTypes` does not offer in the dialog.

## Scan session and services

`SubjectDiscoveryService.ScanAsync` runs all subject discoveries of one scan in parallel and passes each the scan's token. The Discover page owns the scans it starts: only Scan again and closing the dialog cancel one, so Back, picking a row or switching to All types leave it running, and its results are complete when the user returns. The deadline of 30 seconds, from an injected `TimeProvider`, is a cap, not a wait: a scan ends as soon as every subject discovery has completed.

`ISsdpSearch`, `IMulticastDnsBrowser` and `IUdpDiscovery` are singletons in `HomeBlaze.Services`, which each subject discovery takes through its constructor, so no subject discovery opens a socket of its own and a host can replace them, as the E2E host does. No two subject discoveries ask the same question, so nothing is shared; merging identical in-flight queries, if ever needed, fits inside the services without a contract change.

- SSDP uses Rssdp and searches every IPv4 adapter.
- mDNS is a minimal implementation of our own, without a package or reflection. It sends legacy unicast queries from an ephemeral port, so devices answer straight back and HomeBlaze does not bind port 5353, which Avahi holds on most Linux hosts.
- UDP is our own as well. A broadcast goes to the subnet broadcast address of each adapter, because a limited broadcast to `255.255.255.255` leaves only on the default route on Linux. A listener binds its port with address reuse. Sharing the port depends on the platform: Linux shares it only with other listeners that use address reuse, while on Windows address reuse binds over a listener that does not demand exclusive use.

Any host on the network can answer, so what one query can make HomeBlaze hold or do is capped:

- An SSDP search returns at most 64 answers, each with at most 32 headers of at most 256 characters.
- An mDNS browse reads at most 512 messages per adapter, keeps at most 4096 records and 256 KB of names and TXT entries, and returns at most 64 instances.
- A UDP exchange reads at most 512 datagrams and yields only those of at most 1024 bytes.
- A subject discovery that reads further, such as a device description, bounds the number of requests, their size and their time, and requests only addresses it built from an answer itself. The FRITZ!Box subject discovery asks `fritz.box` only when the name resolves to a private, link-local or unique local address, so without a FRITZ!Box it sends no request to the internet.

## Discover page

The picker page of `SubjectSetupDialog` offers All types or Discover when the registry has a subject discovery for a type creatable there, and never for a fixed type (`SubjectSetupOptions.Type`). `SubjectDiscoveryPage` stays in the tree while the configure page is shown, so its results survive Back.

- The first visit starts a scan. Rows that are not added yet come first, each part ordered by type, then by name ignoring case and `Key`, so the list moves little while results arrive. An added row is disabled and links to its subject.
- A row with included items has an expander next to it, as a sibling, because a button inside a button is invalid HTML. Which rows are expanded is kept by subject type and `Key`, so it survives the re-renders while results stream in.
- A hint always says that a container on a bridge network receives no multicast or broadcast, so HomeBlaze needs host networking there or the devices are added by address; a container still has an adapter that claims multicast support, so the case cannot be detected. It also says, without naming a vendor, that some device types are found through their maker's online service, because the page otherwise reads as local only.

### Handoff

Picking a row opens the configure page for the result's type, as picking the type does. `SubjectEditPanel` passes the result as the `DiscoveryResult` parameter only to a component for which `SubjectComponentRegistration.TakesDiscoveryResults` is true, because `DynamicComponent` rejects a parameter the component does not declare; the registry uses the same property. The component decides what it fills in. The name field suggests the result's `DisplayName` when it is a valid name, numbered while it is taken. Setup components keep their own search for when the type is picked directly.

### Creating several devices

During a first setup a user adds many found devices, so the folder Create operation keeps the dialog open between them instead of reopening it and scanning once per device.

- `SubjectSetupOptions.OnCreatedAsync` stores each created subject while the dialog stays open. `ISubjectSetupService.CreateSubjectsAsync` opts in; the canvas, grid and markdown callers do not and still close the dialog on Create.
- A subject created from a Discover row returns to the Discover page. Its row, and every other row of a matching type whose result `Matches` the new subject, shows as added and links to it. A running scan carries on, and Scan again recognizes the new subject.
- A subject created from the type list closes the dialog. When the callback throws, the dialog reports the error and stays on the configure page; the subject counts as handed over only when it has a parent afterwards.

## Error handling

- Each subject discovery runs in its own task. An exception other than cancellation ends that subject discovery only: it is logged as a warning and shown as one line, such as "Nuki Bridge: search failed", and the results it yielded stay listed. A `Matches` that throws during a scan counts as such a failure; in the dialog it counts as no match.
- A subject discovery that ignores its cancellation is abandoned at the deadline: the scan ends, and its later results and exceptions are dropped, an exception logged at debug level. An exception after the scan ended, such as from a disposed socket, is not shown.
- The services fail soft: no usable adapter or a socket error during an exchange returns nothing, with one debug log entry. A listening port that cannot be opened or bound throws instead, so the user learns that another program holds it. A broadcast binds an ephemeral port, which no other program holds.
- A cloud lookup fails soft too: a network without internet access is no failed search, so a service that cannot be reached, answers with an error, sends an oversized answer or times out yields nothing. Other exceptions, such as an invalid answer, still fail the subject discovery.
- Every string from the network is untrusted, and Blazor renders it as text.

## Testing

The scan, the registry, the services' parsing and caps, and each subject discovery are unit tested with fakes of the services and a `TimeProvider` the test advances; `UdpDiscovery.ListenAsync` is also tested on loopback. This repository has no component test framework, so the Discover page keeps its logic in `SubjectDiscoveryList`, `SubjectNameOptions` and `SubjectEditPanel.CreateHostedComponentParameters`, which unit tests cover. E2E tests drive the page with a fake subject discovery, on a host whose network services are empty and whose cloud lookups are stubbed, so a scan there neither searches the machine's network nor binds its ports. How to test a subject discovery is in [Writing a Subject Discovery](../../development/subject-discovery.md#testing).

All subject discoveries were verified manually on a real network; `BroadcastAsync` is verified only that way. Docker bridge networking is untested.
