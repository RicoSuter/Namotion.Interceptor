---
title: Writing a Discoverer
navTitle: Discoverers
---

# Writing a Discoverer

A discoverer finds devices of one subject type on the network, so that the user can pick them on the Discover page of the create dialog instead of typing their address. It is one class in the device library and an opt-in on the type's Setup or Edit component. There is nothing to register: discoverers are found among the loaded types, as components are.

Write one when the device announces itself (mDNS, SSDP, UDP) or a vendor service lists it. The design, the internals and the reasons behind them are in [Subject Discovery](../architecture/design/discovery.md).

## The result

A result describes one found device. It is a record of your own that implements `IDiscoveryResult` and carries whatever the setup needs, such as the host address, an ID or the model.

```csharp
public interface IDiscoveryResult
{
    /// <summary>Unique within the discoverer that returned it.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>A second line for the user, such as the host address, or null.</summary>
    string? Detail { get; }

    /// <summary>What the subject created for the result includes, shown under its row, not selectable.</summary>
    IReadOnlyList<DiscoveredItem> IncludedItems => [];
}

public sealed record DiscoveredItem(string DisplayName, string? Detail);
```

- `Key` is a stable identity of the device, such as a MAC address, a serial number or a bridge ID. Results with a key that was already returned in the scan are dropped, compared ordinally.
- `DisplayName` is the title of the row and the suggested name of the subject, when it is a valid and unused name.
- `Detail` is shown under it, after the type's display name.
- `IncludedItems` lists what comes with the device when the user creates it, such as the speakers of a Sonos household or the repeaters of a FRITZ!Box mesh. The page shows them collapsed under the row, and they cannot be picked on their own. Leave it out when there is nothing to list.

## The discoverer

Derive from `SubjectDiscoverer<TSubject, TResult>` in `HomeBlaze.Abstractions.Discovery` and implement two methods:

```csharp
public abstract class SubjectDiscoverer<TSubject, TResult> : ISubjectDiscoverer
    where TSubject : class, IInterceptorSubject
    where TResult : class, IDiscoveryResult
{
    protected abstract IAsyncEnumerable<TResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken);

    protected abstract bool Matches(TSubject existing, TResult result);
}
```

- `DiscoverAsync` yields each device as soon as it is known, so the page fills while the scan runs.
- `Matches` decides whether an existing subject already represents the device, by the type's own rule, such as the same MAC address or the same host. It is only called with existing subjects of type `TSubject`, and the first match is shown as "already added" with a link to it. Match on a stable identity first and the host address second, because an address can change.

A discoverer creates no subject. The subject is created when the user picks the result, and the result is handed to the component.

## The component

The Setup component of the type, or its Edit component when it has none, implements `ISubjectDiscoveryComponent` from `HomeBlaze.Components.Abstractions` and fills in its fields from the result:

```razor
@implements ISubjectDiscoveryComponent

@code {
    [Parameter]
    public IDiscoveryResult? DiscoveryResult { get; set; }

    protected override void OnParametersSet()
    {
        if (IsCreating && DiscoveryResult is AcmeDiscoveredPlug discovered)
        {
            _hostAddress = discovered.HostAddress;
        }
    }
}
```

`DiscoveryResult` is null when the user picked the type directly. What the component fills in, preselects or skips is its own decision. A discoverer whose type has no Setup or Edit component implementing the interface is not used, and an error is logged.

In a razor file of a `*.HomeBlaze` project, write `@using global::HomeBlaze.Abstractions.Discovery`, because `HomeBlaze` alone resolves to the project namespace.

## Shared services

`DiscoveryScan` gives every discoverer of a scan the same services. A question that was already asked in the scan returns the same in-flight answer, so two discoverers searching for the same thing cause one query on the network.

```csharp
public sealed class DiscoveryScan
{
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
- UDP calls are not shared, because each one is its own exchange. A call reads at most 512 datagrams of at most 1024 bytes, each payload a copy that stays valid. A socket error, such as a port another program holds without address reuse, ends the call quietly with nothing more yielded; only the caller's cancellation throws. The window is at most 30 seconds.
- What one query returns is capped, see [Subject Discovery](../architecture/design/discovery.md#scan-session-and-shared-services).

## Your own I/O

A discoverer can do I/O of its own, such as asking a vendor's cloud service or reading a description from a device it found. It is created through dependency injection, so its constructor can ask for services such as `IHttpClientFactory`, `ILogger<T>` or `TimeProvider`:

```csharp
public sealed class AcmeCloudDiscoverer : SubjectDiscoverer<AcmeHub, AcmeDiscoveredHub>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AcmeCloudDiscoverer(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    // ...
}
```

`NukiBridgeDiscoverer` asks the Nuki discovery service this way.

## Rules

- **Read only.** A discoverer only asks; it never changes a device.
- **Untrusted answers.** Any host on the network can answer. Parse defensively and never throw on received data, bound what you read and keep, and only request addresses you built yourself from an answer, never a URL a device hands you as is.
- **Cancellation.** Honor the token. A scan ends when every discoverer has completed, and at the latest after 30 seconds, also for a discoverer that ignores it.
- **Failure.** A discoverer that throws ends only itself, and the page shows one "search failed" line for its type.
- **No state.** One instance, created on first use, serves overlapping scans, so keep no state between calls.
- **One per type.** A subject type has at most one discoverer. Of several, the first by full type name is used and the others are left out with a logged error.

## Testing

- Keep parsing and matching in `internal static` methods, which the test project of the library reaches through `InternalsVisibleTo`, as `ShellyDeviceDiscoverer.ToDevices` and `IsSameDevice` are tested.
- To run a whole discoverer, create a `DiscoveryScan` with fake `ISsdpSearch`, `IMulticastDnsBrowser` and `IUdpDiscovery` implementations and call it through `ISubjectDiscoverer.DiscoverAsync(scan, existingSubjects, cancellationToken)`, which also applies the deduplication and `Matches`.
- The E2E host replaces the shared services with empty ones, so a discoverer that only uses them finds nothing there and needs no setup. One that does its own I/O needs a stub in `WebTestingHostFactory`, as the Nuki discovery service has.

## Example

A discoverer for a plug that announces `_acme._tcp` with its ID in a TXT record:

```csharp
public sealed class AcmePlugDiscoverer : SubjectDiscoverer<AcmePlug, AcmeDiscoveredPlug>
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
        string.Equals(existing.DeviceId, result.DeviceId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(existing.HostAddress, result.HostAddress, StringComparison.OrdinalIgnoreCase);
}

public sealed record AcmeDiscoveredPlug(string DeviceId, string HostAddress, string DisplayName) : IDiscoveryResult
{
    public string Key => DeviceId;

    public string? Detail => HostAddress;
}
```

The discoverers in this repository show each kind of search:

| Discoverer | Search |
|---|---|
| `FritzBoxDiscoverer` | SSDP, with the repeaters as included items |
| `SonosHouseholdDiscoverer` | SSDP grouped by a header, with the speakers as included items, read from each speaker's description |
| `HueBridgeDiscoverer`, `ShellyDeviceDiscoverer` | mDNS |
| `EcowittGatewayDiscoverer` | UDP broadcast request |
| `MyStromSwitchDiscoverer` | UDP announcements |
| `NukiBridgeDiscoverer` | Vendor cloud service |
