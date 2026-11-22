---
title: Subject Discovery
navTitle: Discovery
status: Planned
---

# Subject Discovery

## Problem

Hub subjects already create their children on their own: a `HueBridge` its lights, a `ShellyDevice` its components, a `SonosSystem` its players, an `EcowittGateway` its sensors and a `NukiBridge` its locks. What is missing is the entry point. To add a device, a user must know which subject type it needs, pick that type in the create dialog, and only then does its Setup component search the network (`FritzBoxSetupComponent`, `HueBridgeSetupComponent`, `NukiBridgeSetupComponent`).

This plan turns that around: the user asks HomeBlaze to search, sees the devices it found, and picks one. The type follows from the device.

## Prior art

| Platform | UX | Implementation |
|---|---|---|
| Home Assistant | Passive "Discovered" section on the integrations page with Add and Ignore | Matchers declared per integration (zeroconf, ssdp, dhcp, bluetooth, usb, mqtt), one central listener, `async_set_unique_id` for deduplication and IP updates |
| openHAB | Inbox with Approve and Ignore, Scan button per binding | `DiscoveryService` per binding with active and background scans, a stable thing UID and representation property |
| Homey, SmartThings | Pick a brand first, then a pairing wizard that lists found devices | Discovery strategies declared per app (mdns-sd, ssdp, mac), the platform runs the listeners |

Common lessons: shared listeners instead of one socket per integration, a stable identity per device, a found device that hands off to the normal setup flow already filled in, and an Ignore that is remembered.

## Scope

This plan covers an on-demand Discover page in the create dialog, with a contract designed so a later background inbox reuses it unchanged.

In scope (one pull request):

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
- Adding several found devices at once
- Bluetooth, USB and DHCP discovery

## Contract

In `HomeBlaze.Abstractions/Discovery`:

```csharp
public interface IDiscoveryResult
{
    /// <summary>Unique within the discoverer that returned it.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>A second line for the user, such as the host address, or null.</summary>
    string? Detail { get; }
}

public abstract class SubjectDiscoverer<TSubject, TResult> : ISubjectDiscoverer
    where TSubject : class, IInterceptorSubject
    where TResult : class, IDiscoveryResult
{
    protected abstract IAsyncEnumerable<TResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken);

    protected abstract bool Matches(TSubject existing, TResult result);
}

public sealed record DiscoveredSubject(Type SubjectType, IDiscoveryResult Result, IInterceptorSubject? ExistingSubject);
```

- A discoverer yields each result as soon as it is known.
- Duplicate detection belongs to each type. The base class keeps the existing subjects of type `TSubject`, calls `Matches` for each result and yields a `DiscoveredSubject` whose `ExistingSubject` is the first match, or null. The framework prescribes no identity property.
- Result records are type specific and implement `IDiscoveryResult` themselves. The existing `FritzBoxDiscoveryResult` and `NukiDiscoveredBridge` become results directly.
- A discoverer creates no subject. `SubjectFactory.CreateSubject` attaches the subject to the root context, where a hosted subject starts at once, so a subject is only created once the user picks a result.

In `HomeBlaze.Components.Abstractions`:

```csharp
public interface ISubjectDiscoveryComponent
{
    /// <summary>The result the user picked on the Discover page, or null when the type was picked directly.</summary>
    IDiscoveryResult? DiscoveryResult { get; set; }
}
```

A Setup or Edit component implements it with a `[Parameter]` property and casts the result to its own record type. What it fills in, preselects or skips is its own decision.

### Registry

`SubjectDiscovererRegistry` in `HomeBlaze.Services` scans `TypeProvider.Types` for non-abstract `ISubjectDiscoverer` types, as `SubjectComponentRegistry` does for components, so plugins provide discoverers without extra registration. It creates them through DI with `ActivatorUtilities`, so a constructor can ask for `IHttpClientFactory` or `ILogger<T>`.

It leaves out, with one logged error at startup and without blocking the app:

- A discoverer that cannot be constructed
- A discoverer whose subject type has no Setup or Edit component implementing `ISubjectDiscoveryComponent`, because a picked result would otherwise open a page that ignores it

The Discover page further leaves out results whose type is not creatable in the dialog, as `SubjectTypeCatalog.GetCreatableTypes` decides (excluded from creation, or single instance and already present).

## Scan session and shared services

`DiscoveryScan` represents one press of Scan and is owned by the Discover page.

- Its cancellation token is canceled by Scan again, Back and closing the dialog.
- Its deadline is 30 seconds, taken from an injected `TimeProvider`. The deadline is a cap, not a wait: the scan ends as soon as every discoverer has completed, normally after 3 to 5 seconds.
- It exposes the shared services below. Within one session, a query that was already sent returns the same in-flight task, so two discoverers asking for the same search target or service type cause one query.

In `HomeBlaze.Abstractions/Discovery`, implemented in `HomeBlaze.Services`:

```csharp
public interface ISsdpSearch
{
    Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken);
}

public interface IMulticastDnsBrowser
{
    Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken);
}
```

- `SsdpResponse` carries the location, unique service name, search target and headers.
- `MulticastDnsService` carries the instance name, host name, addresses, port and TXT records.

SSDP uses Rssdp, which the FRITZ!Box library already depends on, and searches every IPv4 adapter with a listening window of about 3 seconds.

mDNS is a minimal implementation of our own, with no package and no reflection. It sends a PTR query for the service type to `224.0.0.251:5353` on each IPv4 adapter, repeats it after 1 and 3 seconds as RFC 6762 recommends, and combines the PTR, SRV, TXT and A records of the answers into `MulticastDnsService`. Queries are sent from an ephemeral port as legacy unicast queries, so devices answer straight back and HomeBlaze does not bind port 5353, which Avahi holds on most Linux hosts. Whether Shelly devices answer legacy unicast queries is checked against real hardware first; if they do not, the browser binds port 5353 with address reuse instead.

Both services cap the number and size of answers per session, as `FritzBoxDiscovery` caps locations today, because any host on the network can answer.

## Discover page

The picker page of `SubjectSetupDialog` gets a toggle at the top: All types or Discover. It is shown only when the registry has a discoverer for a type creatable in this dialog, and never when the dialog is opened for a fixed type (`SubjectSetupOptions.Type`).

`SubjectDiscoveryPage` stays in the tree while the configure page is shown, like the picker page, so its results survive Back.

- The first visit starts a scan. Scan again cancels the running scan and starts a new one. Results are kept until then.
- All discoverers run in parallel. Each row shows the icon of the type from `SubjectTypeCatalog`, the result's `DisplayName` and `Detail`, and the type's display name. Rows are ordered by type and then by name.
- A row whose `ExistingSubject` is set is shown disabled as "already added" and links to that subject.
- While the scan runs, the header shows a progress indicator; afterwards it shows the number of devices found.
- With nothing found, the page says so and points to All types for adding a device by its address. Battery devices that sleep only answer while awake, which the hint mentions.
- When every multicast query failed because no adapter supports multicast, the empty state says multicast seems blocked and suggests host networking or adding by address. Docker bridge networking is the usual cause.

### Handoff

Picking a row calls the existing `SelectType` for the result's type and keeps the `DiscoveredSubject`.

- `SubjectEditPanel` passes `["DiscoveryResult"] = result` through `EditorParameters` when the hosted component implements `ISubjectDiscoveryComponent`, and never otherwise, because `DynamicComponent` rejects a parameter the component does not declare.
- The name field suggests the result's `DisplayName` when that is a valid, unused name, and the type's usual suggestion otherwise. A name the user typed is kept.
- Back returns to the page the type was picked on and discards the created subject, as today.
- Validation, Create, `BeforeSave` and persisting are unchanged.

## Error handling

- Each discoverer runs in its own task. An exception other than cancellation ends that discoverer only: it is logged with the discoverer's type and shown as one line on the page, such as "Nuki: lookup failed". Results it yielded before stay listed.
- An `OperationCanceledException` from a canceled session is neither logged nor shown.
- The shared services fail soft: no usable adapter or a socket error returns an empty answer and one debug log entry, not an exception.
- A `Matches` call that throws leaves that result out and is logged like a discoverer failure.
- A discoverer that never completes is canceled at the session deadline.
- Every string from the network is untrusted. Blazor renders it as text, never as markup.

## v1 discoverers

| Type | Discovery | Result | Matches |
|---|---|---|---|
| `FritzBox` | `FritzBoxDiscovery` through the shared `ISsdpSearch`, plus the `fritz.box` fallback | `FritzBoxDiscoveryResult`, key `Udn` or else host address | A new persisted `Udn` when both sides have one, otherwise the host address |
| `HueBridge` | mDNS `_hue._tcp`, local only | A new record with bridge ID and address, key bridge ID | The persisted `BridgeId`, ignoring case |
| `NukiBridge` | The Nuki cloud lookup, which runs only because the user pressed Scan | `NukiDiscoveredBridge`, key bridge ID | The host address, plus the bridge ID once the implementation confirms which runtime identity of the bridge it corresponds to |
| `ShellyDevice` | mDNS `_shelly._tcp`; the TXT records carry the device ID, model and generation | A new record with device ID, model and address, key device ID | A new persisted `DeviceId`, otherwise the host address |

- `FritzBox.Udn` and `ShellyDevice.DeviceId` are new `[Configuration]` properties. The component that receives the result writes them, and the subject fills them in on its first successful connect when they are empty, so subjects created before this change gain them.
- `FritzBoxDiscovery.DiscoverAsync` takes the SSDP search as a parameter, so the Setup component and the discoverer share parsing and description.
- The Setup components keep their own search for when the type is picked directly. The Hue Setup component keeps the HueApi locator with its network scan fallback.
- `FritzBoxSetupComponent`, `HueBridgeSetupComponent`, `NukiBridgeSetupComponent` and `ShellyDeviceEditComponent` implement `ISubjectDiscoveryComponent`. The Setup components preselect the result in their lists; the Shelly component fills in the host address and device ID.

## Breaking changes

- `FritzBoxDiscovery.DiscoverAsync` takes an `ISsdpSearch`. Migration: pass the built-in implementation.

The new configuration properties are additions; stored subjects without them load unchanged.

## Testing

Unit tests, without sockets:

- The base class keeps only subjects of its type and sets `ExistingSubject` from `Matches`
- The registry finds discoverers through `TypeProvider` and applies its exclusion rules
- `DiscoveryScan` shares in-flight queries, isolates failing discoverers, honors cancellation and ends at its deadline, driven by a `TimeProvider` the test advances
- mDNS packet parsing from captured bytes, including name compression, malformed packets and the caps
- Each discoverer against fake `ISsdpSearch`, `IMulticastDnsBrowser` and HTTP handlers, including its `Matches` rules

Component tests for the Discover page with fake discoverers: rows streaming in, already added rows, the failure line, the blocked multicast hint, and `DiscoveryResult` reaching only components that implement `ISubjectDiscoveryComponent`.

One E2E test with a fake discoverer: open Discover, pick the result, create, and find the stored subject.

Manual verification before the pull request is finalized, agreed while planning: a real Shelly (which also settles the legacy unicast question), FRITZ!Box, Hue bridge and Nuki bridge, run directly and under Docker bridge networking.

## Documentation

- `architecture/design/discovery.md` becomes the canonical description once implemented, and this plan is removed or marked implemented.
- The device pages of FRITZ!Box, Hue, Nuki and Shelly each state how the device is discovered and matched.
- The `create-homeblaze-library` skill describes writing a discoverer.
