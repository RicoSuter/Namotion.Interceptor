---
title: Writing a Subject Discovery
navTitle: Subject Discovery
---

# Writing a Subject Discovery

A subject discovery finds devices of one subject type on the network, so that the user can pick them on the Discover page of the create dialog instead of typing their address. It is one class in the device library and an opt-in on the type's Setup or Edit component. There is nothing to register: subject discoveries are found among the loaded types, as components are.

Write one when the device announces itself (mDNS, SSDP, UDP) or a vendor service lists it. The design, the internals and the reasons behind them are in [Subject Discovery](../architecture/design/discovery.md).

## The result

A result describes one found device. It is a record of your own that implements `IDiscoveryResult` and carries whatever the setup needs, such as the host address, an ID or the model.

```csharp
public interface IDiscoveryResult
{
    /// <summary>Unique within the subject discovery that returned it.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>A second line for the user, such as the host address, or null.</summary>
    string? Detail { get; }

    /// <summary>What the subject created for the result includes, shown under its row, not selectable.</summary>
    IReadOnlyList<DiscoveredItem> IncludedItems => [];
}

public sealed record DiscoveredItem(string DisplayName, string? Detail);
```

- `Key` is a stable identity of the device, such as a MAC address, a serial number or a bridge ID. Results with a key that was already returned in the scan are dropped, compared ordinally, so normalize the case where the device's identity ignores it, such as a MAC address.
- `DisplayName` is the title of the row and the suggested name of the subject, when it is a valid, unused and plain name.
- `Detail` is shown under it, after the type's display name.
- `IncludedItems` lists what comes with the device when the user creates it, such as the speakers of a Sonos household or the repeaters of a FRITZ!Box mesh. The page shows them collapsed under the row, and they cannot be picked on their own. Leave it out when there is nothing to list.

## The subject discovery

Derive from `SubjectDiscovery<TSubject, TResult>` in `HomeBlaze.Abstractions.Discovery` and implement two methods:

```csharp
public abstract class SubjectDiscovery<TSubject, TResult> : ISubjectDiscovery
    where TSubject : class, IInterceptorSubject
    where TResult : class, IDiscoveryResult
{
    protected abstract IAsyncEnumerable<TResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken);

    protected abstract bool Matches(TSubject existing, TResult result);
}
```

- `DiscoverAsync` yields each device as soon as it is known, so the page fills while the scan runs.
- `Matches` decides whether an existing subject already represents the device, by the type's own rule, such as the same MAC address or the same host. It is only called with existing subjects of type `TSubject`, and the first match is shown as "already added" with a link to it. Match on a stable identity, so a device that moved is still recognized. Whether the host address counts as well is the type's rule: `ShellyDeviceDiscovery` uses it only when no identity is known, so a device that replaced another at the same address is listed as new, while `FritzBoxDiscovery` accepts either, because its subject connects to whatever answers at that address.

A subject discovery creates no subject. The subject is created when the user picks the result, and the result is handed to the component.

## The component

The Setup component of the type, or its Edit component when it has none, implements `ISubjectDiscoveryComponent` from `HomeBlaze.Components.Abstractions` and fills in its fields from the result:

```razor
@using global::HomeBlaze.Abstractions.Discovery
@implements ISubjectDiscoveryComponent

@code {
    [Parameter]
    public bool IsCreating { get; set; }

    [Parameter]
    public IDiscoveryResult? DiscoveryResult { get; set; }

    protected override void OnInitialized()
    {
        if (IsCreating && DiscoveryResult is AcmeDiscoveredPlug discovered)
        {
            _hostAddress = discovered.HostAddress;
        }
    }
}
```

`DiscoveryResult` is null when the user picked the type directly. Read it in `OnInitialized`: `OnParametersSet` runs again on every render of the dialog, such as each keystroke in its name field, and would overwrite what the user typed. What the component fills in, preselects or skips is its own decision. A subject discovery whose type has no Setup or Edit component implementing the interface is not used, and an error is logged, see [Registry](../architecture/design/discovery.md#registry).

The `global::` prefix is needed in a razor file of a `*.HomeBlaze` project, because `HomeBlaze` alone resolves to the project namespace.

## Shared services

`DiscoveryScan` gives every subject discovery of a scan the same services. An SSDP or mDNS question that was already asked in the scan returns the same in-flight answer, so two subject discoveries searching for the same thing cause one query on the network.

```csharp
public sealed class DiscoveryScan
{
    public DiscoveryScan(
        ISsdpSearch ssdpSearch, IMulticastDnsBrowser multicastDnsBrowser, IUdpDiscovery udpDiscovery, CancellationToken cancellationToken);

    public ISsdpSearch Ssdp { get; }
    public IMulticastDnsBrowser MulticastDns { get; }
    public IUdpDiscovery Udp { get; }
}

public interface ISsdpSearch
{
    Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken);
}

public sealed record SsdpResponse(Uri Location, string? UniqueServiceName, IReadOnlyDictionary<string, string> Headers);

public interface IMulticastDnsBrowser
{
    Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken);
}

public sealed record MulticastDnsService(
    string InstanceName, string? HostName, int Port, IReadOnlyList<IPAddress> Addresses, IReadOnlyDictionary<string, string> Properties);

public interface IUdpDiscovery
{
    IAsyncEnumerable<UdpDatagram> BroadcastAsync(int port, ReadOnlyMemory<byte> request, TimeSpan window, CancellationToken cancellationToken);

    IAsyncEnumerable<UdpDatagram> ListenAsync(int port, TimeSpan window, CancellationToken cancellationToken);
}

public readonly record struct UdpDatagram(ReadOnlyMemory<byte> Payload, IPAddress SenderAddress);
```

- `Ssdp.SearchAsync` sends an M-SEARCH for the search target, such as `urn:dslforum-org:device:InternetGatewayDevice:1`, listens for about 3 seconds and returns the answers for exactly that target. `Headers` are looked up by name ignoring case, such as a vendor header like `X-RINCON-HOUSEHOLD`; the values of a repeated header are joined by `, `, and headers are capped in number and length. Searches are shared by exact search target.
- `MulticastDns.BrowseAsync` browses a service type, such as `_hue._tcp`, and returns each instance with its host name, port, addresses and TXT records in `Properties`. Browses are shared by service type ignoring case.
- `Udp.BroadcastAsync` sends your request to the given port as a broadcast on every IPv4 adapter, repeats it once after about a second, and yields each answer as it arrives within the window, with the address it came from. A device that receives both requests answers twice, which the deduplication by `Key` absorbs.
- `Udp.ListenAsync` listens on a port with address reuse for devices that announce themselves, and yields each datagram that arrives within the window.
- UDP calls are not shared, because each one is its own exchange. Each payload is a copy that stays valid. A socket error, such as a port another program holds without address reuse, ends the call quietly with nothing more yielded. The call itself throws `ArgumentOutOfRangeException` for a port outside 1 to 65535, a request outside 1 to 1024 bytes, or a window of zero or less or of more than 30 seconds, and the enumeration throws only for the caller's cancellation.
- What one query or UDP call returns is capped, see [Subject Discovery](../architecture/design/discovery.md#scan-session-and-shared-services).

## Your own I/O

A subject discovery can do I/O of its own, such as asking a vendor's cloud service or reading a description from a device it found. It is created through dependency injection, so its constructor can ask for services such as `IHttpClientFactory`, `ILogger<T>` or `TimeProvider`:

```csharp
public sealed class AcmeCloudDiscovery : SubjectDiscovery<AcmeHub, AcmeDiscoveredHub>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AcmeCloudDiscovery(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    // ...
}
```

`NukiBridgeDiscovery` asks the Nuki discovery service this way.

## Rules

- **Read only.** A subject discovery only asks; it never changes a device.
- **Untrusted answers.** Any host on the network can answer. Parse defensively and never throw on received data, bound what you read and keep, and only request addresses you built yourself from an answer, never a URL a device hands you as is.
- **Cancellation.** Honor the token. A scan ends when every subject discovery has completed, and at the latest after 30 seconds, also for a subject discovery that ignores it, see [Scan session](../architecture/design/discovery.md#scan-session-and-shared-services).
- **Failure.** A subject discovery that throws ends only itself, and the page shows one "search failed" line for its type, see [Error handling](../architecture/design/discovery.md#error-handling).
- **No state.** One instance, created on first use, serves overlapping scans, so keep no state between calls.
- **One per type.** A subject type has at most one subject discovery: the first by full type name, ordinally, that can be created is used, and the others are left out with a logged error, see [Registry](../architecture/design/discovery.md#registry).

## Testing

- Keep parsing and matching in `internal static` methods, which the test project of the library reaches through `InternalsVisibleTo`, as `ShellyDeviceDiscovery.ToDevices` and `IsSameDevice` are tested.
- To run a whole subject discovery, create a `DiscoveryScan` with fake `ISsdpSearch`, `IMulticastDnsBrowser` and `IUdpDiscovery` implementations and call it through `ISubjectDiscovery.DiscoverAsync(scan, existingSubjects, cancellationToken)`, which also applies the deduplication and `Matches`.
- The E2E host replaces the shared services with empty ones, so a subject discovery that only uses them finds nothing there and needs no setup. One that calls an address of its own, such as a vendor's cloud service, needs a stub in `WebTestingHostFactory`, as the Nuki discovery service has; that stub only reaches an `HttpClient` from `IHttpClientFactory`. The FRITZ!Box subject discovery asks `fritz.box` only when the name resolves to an address on the local network, so in the E2E host it makes a name lookup and no request.

## Example

A subject discovery for a plug that announces `_acme._tcp` with its ID in a TXT record:

```csharp
public sealed class AcmePlugDiscovery : SubjectDiscovery<AcmePlug, AcmeDiscoveredPlug>
{
    protected override async IAsyncEnumerable<AcmeDiscoveredPlug> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var service in await scan.MulticastDns.BrowseAsync("_acme._tcp", cancellationToken))
        {
            if (service.Properties.TryGetValue("id", out var id) && service.Addresses.Count > 0)
            {
                yield return new AcmeDiscoveredPlug(id, service.Addresses[0].ToString(), service.InstanceName);
            }
        }
    }

    protected override bool Matches(AcmePlug existing, AcmeDiscoveredPlug result) =>
        string.IsNullOrWhiteSpace(existing.DeviceId)
            ? string.Equals(existing.HostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase)
            : string.Equals(existing.DeviceId.Trim(), result.DeviceId, StringComparison.OrdinalIgnoreCase);
}

public sealed record AcmeDiscoveredPlug(string DeviceId, string HostAddress, string DisplayName) : IDiscoveryResult
{
    public string Key => DeviceId.ToLowerInvariant();

    public string? Detail => HostAddress;
}
```

The subject discoveries in this repository show each kind of search:

| Subject discovery | Search |
|---|---|
| `FritzBoxDiscovery` | SSDP and `fritz.box`, with the repeaters as included items when one router is found |
| `SonosHouseholdDiscovery` | SSDP grouped by a header, with the speakers as included items, read from each speaker's description |
| `HueBridgeDiscovery`, `ShellyDeviceDiscovery` | mDNS |
| `EcowittGatewayDiscovery` | UDP broadcast request |
| `MyStromSwitchDiscovery` | UDP announcements |
| `NukiBridgeDiscovery` | Vendor cloud service |
