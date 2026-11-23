---
title: Writing a Subject Discovery
navTitle: Subject Discovery
---

# Writing a Subject Discovery

A subject discovery finds devices of one subject type on the network, so that the user can pick them on the Discover page of the create dialog instead of typing their address. It is one class in the device library and an opt-in on the type's Setup or Edit component. There is nothing to register: subject discoveries are found among the loaded types, as components are.

Write one when the device announces itself (mDNS, SSDP, UDP) or a vendor service lists it. The design, the internals and the reasons behind them are in [the Subject Discovery design](../architecture/design/discovery.md).

## The result

A result describes one found device. It is a record of your own that implements `IDiscoveryResult<TSubject>` and carries whatever the setup needs, such as the host address, an ID or the model.

```csharp
public interface IDiscoveryResult
{
    /// <summary>Identifies the device within the subject discovery that found it, the same in every scan.</summary>
    string Key { get; }

    /// <summary>The name the user is shown for the device.</summary>
    string DisplayName { get; }

    /// <summary>A second line for the user, such as the host address, or null.</summary>
    string? Detail { get; }

    /// <summary>What the subject created for the result includes, shown under its row, not selectable.</summary>
    IReadOnlyList<DiscoveredItem> IncludedItems => [];

    /// <summary>Whether <paramref name="subject"/> already represents this device; false for a subject of another type.</summary>
    bool Matches(IInterceptorSubject subject);
}

public interface IDiscoveryResult<in TSubject> : IDiscoveryResult
    where TSubject : IInterceptorSubject
{
    bool Matches(TSubject subject);
}

public sealed record DiscoveredItem(string DisplayName, string? Detail);
```

- `Key` is a stable identity of the device, such as a MAC address, a serial number or a bridge ID. The scan drops a result with a key your subject discovery already returned in it, compared ordinally, so yielding a device twice is fine, and normalize the case where the device's identity ignores it, such as a MAC address.
- `DisplayName` is the title of the row and the suggested name of the subject, when it is a valid and plain name, with a number appended while it is taken ("FRITZ!Repeater 1200 AX 2").
- `Detail` is shown under it, after the type's display name.
- `IncludedItems` lists what comes with the device when the user creates it, such as the speakers of a Sonos household or the repeaters of a FRITZ!Box mesh. The page shows them collapsed under the row, and they cannot be picked on their own. Sort them with `DiscoveredItem.DisplayOrder`, so the list reads the same in every scan. Leave it out when there is nothing to list.
- `Matches` decides whether a subject already represents the device, by the type's own rule, such as the same MAC address, which `MacAddressComparison.AreEqual` in `HomeBlaze.Abstractions.Networking` compares ignoring case and separators, or the same host. Implement only `Matches(TSubject)`: `IDiscoveryResult<TSubject>` implements the untyped one, which is false for a subject of another type. The caller matches: the scan asks each result about the existing subjects of the type and shows the first match as "already added" with a link to it, and the create dialog asks the rows of the type not added yet about each subject it creates. Match on a stable identity that the running subject reads from the device, so a device that moved is still recognized once its subject has read that identity; HomeBlaze stores no identity of its own for discovery. Whether the host address counts as well is the type's rule: `ShellyDiscoveredDevice` uses it only while the subject has not read the device ID, so a device that replaced another at the same address is listed as new, while `FritzBoxDiscoveryResult` accepts either the unique device name of the connected router or the host address, because its subject connects to whatever answers at that address. A `Matches` that throws ends its subject discovery during a scan and counts as no match in the dialog, see [Error handling](../architecture/design/discovery.md#error-handling). Keep it a quick comparison of values, without I/O or blocking: it runs on the scan's threads and on the dialog's renderer.

## The subject discovery

Derive from `SubjectDiscovery<TSubject, TResult>` in `HomeBlaze.Abstractions.Discovery` and implement `DiscoverAsync`:

```csharp
public abstract class SubjectDiscovery<TSubject, TResult> : ISubjectDiscovery
    where TSubject : class, IInterceptorSubject
    where TResult : class, IDiscoveryResult<TSubject>
{
    protected abstract IAsyncEnumerable<TResult> DiscoverAsync(CancellationToken cancellationToken);
}
```

The base class implements `ISubjectDiscovery`, the contract the scan calls:

```csharp
public interface ISubjectDiscovery
{
    Type SubjectType { get; }

    IAsyncEnumerable<IDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);
}
```

`DiscoverAsync` yields each device as soon as it is known, so the page fills while the scan runs.

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

## Services

`HomeBlaze.Abstractions.Discovery` has one service per kind of search, registered as a singleton. Inject the ones you need: a subject discovery is created through dependency injection, so its constructor takes them, as in [the example](#example).

```csharp
public interface ISsdpSearch
{
    Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken);
}

public sealed record SsdpResponse(Uri Location, string? UniqueServiceName, IReadOnlyDictionary<string, string> Headers);

public interface IMulticastDnsBrowser
{
    Task<IReadOnlyList<MulticastDnsServiceInstance>> BrowseAsync(string serviceType, CancellationToken cancellationToken);
}

public sealed record MulticastDnsServiceInstance(
    string InstanceName, string? HostName, IReadOnlyList<IPAddress> Addresses, IReadOnlyDictionary<string, string> Properties);

public interface IUdpDiscovery
{
    IAsyncEnumerable<UdpDatagram> BroadcastAsync(int port, ReadOnlyMemory<byte> request, TimeSpan window, CancellationToken cancellationToken);

    IAsyncEnumerable<UdpDatagram> ListenAsync(int port, TimeSpan window, CancellationToken cancellationToken);
}

public readonly record struct UdpDatagram(ReadOnlyMemory<byte> Payload, IPAddress SenderAddress);
```

- `ISsdpSearch.SearchAsync` sends an M-SEARCH for the search target, such as `urn:dslforum-org:device:InternetGatewayDevice:1`, listens for about 3 seconds and returns the answers for exactly that target. `Headers` are looked up by name ignoring case, such as a vendor header like `X-RINCON-HOUSEHOLD`; the values of a repeated header are joined by `, `, and headers are capped in number and length.
- `IMulticastDnsBrowser.BrowseAsync` browses a service type, such as `_hue._tcp`, and returns each instance with its host name, addresses and TXT records in `Properties`.
- `IUdpDiscovery.BroadcastAsync` sends your request to the given port as a broadcast on every IPv4 adapter, repeats it once after about a second, and yields each answer as it arrives within the window, with the address it came from. A device that receives both requests answers twice, which the deduplication by `Key` absorbs.
- `IUdpDiscovery.ListenAsync` listens on a port with address reuse for devices that announce themselves, and yields each datagram that arrives within the window. Whether the port is shared depends on the platform: on Linux only with other listeners that also use address reuse.
- Each UDP payload is a copy that stays valid. A socket error during the exchange ends the call quietly with nothing more yielded. A listening socket that `ListenAsync` could not open or bind, such as on a port another program holds without address reuse, throws a `SocketException` from the enumeration; let it propagate, so the Discover page shows the search as failed. The call itself throws `ArgumentOutOfRangeException` for a port outside 1 to 65535, a request outside 1 to 1024 bytes, or a window of zero or less or of more than 30 seconds, and otherwise the enumeration throws only for the caller's cancellation.
- What one query or UDP call returns is capped, see [the Subject Discovery design](../architecture/design/discovery.md#scan-session-and-services).

## Your own I/O

A subject discovery can do I/O of its own, such as asking a vendor's cloud service or reading a description from a device it found. Its constructor can ask for any other singleton or transient service, such as `IHttpClientFactory`, `ILogger<T>` or `TimeProvider`; it is created once from the root container, so not a scoped one:

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
- **Cancellation.** Honor the token. A scan ends when every subject discovery has completed, and at the latest after 30 seconds, also for a subject discovery that ignores it, see [Scan session](../architecture/design/discovery.md#scan-session-and-services).
- **Failure.** A subject discovery that throws ends only itself, and the page shows one "search failed" line for its type, see [Error handling](../architecture/design/discovery.md#error-handling).
- **No state.** One instance, created on first use, serves overlapping scans, so keep no state between calls.
- **One per type.** A subject type has at most one subject discovery: the first by full type name, ordinally, that can be created is used, and the others are left out with a logged error, see [Registry](../architecture/design/discovery.md#registry).

## Testing

- Keep parsing in `internal static` methods, which the test project of the library reaches through `InternalsVisibleTo`, as `ShellyDeviceDiscovery.ToDevices` is tested.
- Check the matching rule by calling `result.Matches(subject)` directly on a result you construct, as `ShellyDeviceDiscoveryTests` does.
- To run a whole subject discovery, construct it with fakes of the services it takes, such as `ISsdpSearch`, `IMulticastDnsBrowser` or `IUdpDiscovery`, and call it through `ISubjectDiscovery.DiscoverAsync(cancellationToken)`, which is what the scan calls. The scan adds the deduplication by key and the matching against the existing subjects.
- The E2E host replaces `ISsdpSearch`, `IMulticastDnsBrowser` and `IUdpDiscovery` with empty ones, so a subject discovery that only uses them finds nothing there and needs no setup. One that calls an address of its own, such as a vendor's cloud service, needs a stub in `WebTestingHostFactory`, as the Nuki discovery service has; that stub only reaches an `HttpClient` from `IHttpClientFactory`. The FRITZ!Box subject discovery asks `fritz.box` only when the name resolves to an address on the local network: on a CI runner it makes only a name lookup, and behind a FRITZ!Box the router answers.

## Example

A subject discovery for a plug that announces `_acme._tcp` with its ID in a TXT record:

```csharp
public sealed class AcmePlugDiscovery(IMulticastDnsBrowser multicastDnsBrowser) : SubjectDiscovery<AcmePlug, AcmeDiscoveredPlug>
{
    protected override async IAsyncEnumerable<AcmeDiscoveredPlug> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var instance in await multicastDnsBrowser.BrowseAsync("_acme._tcp", cancellationToken))
        {
            if (instance.Properties.TryGetValue("id", out var id) && instance.Addresses.Count > 0)
            {
                yield return new AcmeDiscoveredPlug(id, instance.Addresses[0].ToString(), instance.InstanceName);
            }
        }
    }
}

public sealed record AcmeDiscoveredPlug(string DeviceId, string HostAddress, string DisplayName) : IDiscoveryResult<AcmePlug>
{
    public string Key => DeviceId.ToLowerInvariant();

    public string? Detail => HostAddress;

    // ReportedDeviceId is state the running subject read from the plug, null until it connected.
    public bool Matches(AcmePlug subject) =>
        string.IsNullOrWhiteSpace(subject.ReportedDeviceId)
            ? string.Equals(subject.HostAddress?.Trim(), HostAddress, StringComparison.OrdinalIgnoreCase)
            : string.Equals(subject.ReportedDeviceId, DeviceId, StringComparison.OrdinalIgnoreCase);
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
