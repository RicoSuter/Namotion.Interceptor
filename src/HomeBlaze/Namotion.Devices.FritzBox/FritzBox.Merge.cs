using Namotion.Devices.FritzBox.Model;
using Namotion.Devices.FritzBox.Parsing;

namespace Namotion.Devices.FritzBox;

public partial class FritzBox
{
    // Read and written by the polling loop only.
    private string? _stateSerialNumber;

    /// <summary>
    /// Writes one poll onto the subjects. Existing subjects are patched in place, and a collection is replaced only
    /// when its members change. Called by the polling loop only.
    /// </summary>
    /// <param name="snapshot">The values the poll read.</param>
    /// <param name="wlanCommandGeneration">
    /// <see cref="WlanCommandGeneration"/> read before the poll started. A switch state read before a later command is
    /// stale and not written.
    /// </param>
    internal void ApplySnapshot(FritzBoxSnapshot snapshot, long wlanCommandGeneration)
    {
        if (snapshot.Router?.SerialNumber is { } serialNumber)
        {
            if (_stateSerialNumber is not null && _stateSerialNumber != serialNumber)
            {
                ClearRouterState();
            }

            _stateSerialNumber = serialNumber;
        }

        ApplyRouter(snapshot);

        var hosts = IndexHosts(snapshot.Hosts);

        // The owning collection first: a new client gets its parent edge from it before an access point shares it.
        ApplyClients(SelectClientHosts(hosts, snapshot.Mesh), snapshot.Mesh);
        ApplyAccessPoints(snapshot.Mesh, hosts);
        ApplyWlans(snapshot.Wlans, wlanCommandGeneration);
    }

    private void ClearRouterState()
    {
        _stateSerialNumber = null;
        Clients = new Dictionary<string, FritzBoxClient>(StringComparer.Ordinal);
        AccessPoints = new Dictionary<string, FritzBoxAccessPoint>(StringComparer.Ordinal);
        Wlans = new Dictionary<string, FritzBoxWlan>(StringComparer.Ordinal);
        Model = null;
        SerialNumber = null;
        HardwareRevision = null;
        SoftwareVersion = null;
        AvailableSoftwareUpdate = null;
        Uptime = null;
        IsInternetConnected = null;
        ExternalIpAddress = null;
        ExternalIpv6Address = null;
        InternetConnectionUptime = null;
        MaximumUploadRate = null;
        MaximumDownloadRate = null;
        UploadRate = null;
        DownloadRate = null;
        PresentedCertificatePin = null;
        LastUpdated = null;
    }

    private void ApplyRouter(FritzBoxSnapshot snapshot)
    {
        ApplyRouterInfo(snapshot.Router);
        AvailableSoftwareUpdate = snapshot.AvailableSoftwareUpdate;
        ApplyInternet(snapshot.Internet);
        UploadRate = snapshot.Traffic?.UploadRate;
        DownloadRate = snapshot.Traffic?.DownloadRate;
    }

    private void ApplyRouterInfo(RouterInfo? router)
    {
        Model = router?.Model;
        SerialNumber = router?.SerialNumber;
        HardwareRevision = router?.HardwareRevision;
        SoftwareVersion = router?.SoftwareVersion;
        Uptime = router?.Uptime;
    }

    private void ApplyInternet(InternetInfo? internet)
    {
        IsInternetConnected = internet?.IsConnected;
        ExternalIpAddress = internet?.ExternalIpAddress;
        ExternalIpv6Address = internet?.ExternalIpv6Address;
        InternetConnectionUptime = internet?.Uptime;
        MaximumUploadRate = internet?.MaximumUploadRate;
        MaximumDownloadRate = internet?.MaximumDownloadRate;
    }

    /// <returns>The hosts by normalized MAC address, without entries without an address; the last of repeated
    /// addresses wins.</returns>
    private static Dictionary<string, HostEntry> IndexHosts(IReadOnlyList<HostEntry> hosts)
    {
        var indexed = new Dictionary<string, HostEntry>(hosts.Count, StringComparer.Ordinal);
        foreach (var host in hosts)
        {
            if (MacAddresses.Normalize(host.MacAddress) is { } macAddress)
            {
                indexed[macAddress] = host;
            }
        }

        return indexed;
    }

    /// <returns>The hosts that are clients: every host but the addresses of mesh nodes.</returns>
    private static Dictionary<string, HostEntry> SelectClientHosts(Dictionary<string, HostEntry> hosts, MeshTopology? mesh)
    {
        if (mesh is null)
        {
            return hosts;
        }

        var selected = new Dictionary<string, HostEntry>(hosts.Count, StringComparer.Ordinal);
        foreach (var (macAddress, host) in hosts)
        {
            if (!mesh.MeshMacAddresses.Contains(macAddress))
            {
                selected[macAddress] = host;
            }
        }

        return selected;
    }

    private void ApplyClients(Dictionary<string, HostEntry> hosts, MeshTopology? mesh)
    {
        var current = Clients;
        var isChanged = !HasSameKeys(current, hosts);
        var next = isChanged ? new Dictionary<string, FritzBoxClient>(hosts.Count, StringComparer.Ordinal) : current;
        foreach (var (macAddress, host) in hosts)
        {
            if (!current.TryGetValue(macAddress, out var client))
            {
                client = new FritzBoxClient(macAddress);
            }

            ClientPosition? position = null;
            if (host.IsActive)
            {
                mesh?.ClientPositions.TryGetValue(macAddress, out position);
            }

            client.Update(host, position);
            if (isChanged)
            {
                next[macAddress] = client;
            }
        }

        if (isChanged)
        {
            Clients = next;
        }
    }

    private void ApplyAccessPoints(MeshTopology? mesh, Dictionary<string, HostEntry> hosts)
    {
        var infos = new Dictionary<string, AccessPointInfo>(StringComparer.Ordinal);
        foreach (var info in mesh?.AccessPoints ?? [])
        {
            infos[info.MacAddress] = info;
        }

        var current = AccessPoints;
        var isChanged = !HasSameKeys(current, infos);
        var next = isChanged ? new Dictionary<string, FritzBoxAccessPoint>(infos.Count, StringComparer.Ordinal) : current;
        foreach (var (macAddress, info) in infos)
        {
            if (!current.TryGetValue(macAddress, out var accessPoint))
            {
                accessPoint = new FritzBoxAccessPoint(macAddress);
            }

            accessPoint.Update(info, FindIpAddress(info, hosts));
            ApplyAccessPointClients(accessPoint);
            if (isChanged)
            {
                next[macAddress] = accessPoint;
            }
        }

        if (isChanged)
        {
            AccessPoints = next;
        }
    }

    private void ApplyAccessPointClients(FritzBoxAccessPoint accessPoint)
    {
        if (HasSameClients(accessPoint))
        {
            return;
        }

        var next = new Dictionary<string, FritzBoxClient>(StringComparer.Ordinal);
        foreach (var client in Clients.Values)
        {
            if (client.AccessPointMacAddress == accessPoint.MacAddress)
            {
                next[client.MacAddress] = client;
            }
        }

        accessPoint.Clients = next;
    }

    /// <returns>Whether the access point already holds exactly the clients positioned at it.</returns>
    private bool HasSameClients(FritzBoxAccessPoint accessPoint)
    {
        var current = accessPoint.Clients;
        var count = 0;
        foreach (var client in Clients.Values)
        {
            if (client.AccessPointMacAddress != accessPoint.MacAddress)
            {
                continue;
            }

            if (!current.TryGetValue(client.MacAddress, out var existing) || !ReferenceEquals(existing, client))
            {
                return false;
            }

            count++;
        }

        return count == current.Count;
    }

    private void ApplyWlans(IReadOnlyList<WlanInfo> wlans, long wlanCommandGeneration)
    {
        var infos = new Dictionary<string, WlanInfo>(StringComparer.Ordinal);
        foreach (var wlan in wlans)
        {
            infos[wlan.Role] = wlan;
        }

        var current = Wlans;
        var isChanged = !HasSameKeys(current, infos);
        var next = isChanged ? new Dictionary<string, FritzBoxWlan>(infos.Count, StringComparer.Ordinal) : current;

        // A command raises the generation under this lock, so it cannot complete between the check and the write
        // and have its new switch state overwritten by this older poll.
        lock (_wlanSwitchStateLock)
        {
            var isSwitchStateCurrent = wlanCommandGeneration == WlanCommandGeneration;
            foreach (var (role, info) in infos)
            {
                var isNew = !current.TryGetValue(role, out var wlan);
                wlan ??= role == FritzBoxWlanRoles.Guest
                    ? new FritzBoxGuestWlan(this, info.ServiceType)
                    : new FritzBoxWlan(role, info.ServiceType);
                wlan.Update(info, isSwitchStateCurrent || isNew);
                if (isChanged)
                {
                    next[role] = wlan;
                }
            }
        }

        if (isChanged)
        {
            Wlans = next;
        }
    }

    /// <returns>The IP address the host list reports for any address of the access point, its device address
    /// first.</returns>
    private static string? FindIpAddress(AccessPointInfo info, Dictionary<string, HostEntry> hosts)
    {
        if (GetIpAddress(info.MacAddress, hosts) is { } ipAddress)
        {
            return ipAddress;
        }

        foreach (var macAddress in info.MacAddresses)
        {
            if (GetIpAddress(macAddress, hosts) is { } interfaceIpAddress)
            {
                return interfaceIpAddress;
            }
        }

        return null;
    }

    private static string? GetIpAddress(string macAddress, Dictionary<string, HostEntry> hosts) =>
        hosts.TryGetValue(macAddress, out var host) ? StringValues.NullIfBlank(host.IpAddress) : null;

    private static bool HasSameKeys<TCurrent, TNext>(Dictionary<string, TCurrent> current, Dictionary<string, TNext> next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        foreach (var key in next.Keys)
        {
            if (!current.ContainsKey(key))
            {
                return false;
            }
        }

        return true;
    }
}
