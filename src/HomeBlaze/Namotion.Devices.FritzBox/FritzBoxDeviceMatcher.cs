using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using HomeBlaze.Abstractions.Networking;
using Namotion.Interceptor;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// Finds the subject of the device behind each client.
/// </summary>
internal static class FritzBoxDeviceMatcher
{
    /// <summary>
    /// Matches subjects to clients. A subject that reports the MAC address of a client (<see cref="INetworkAdapter"/>)
    /// matches that client. Any other subject matches by its IP address or its <see cref="INetworkHost.HostAddress"/>,
    /// as an IP address or as a host name, but only while it is connected (<see cref="IConnectionState"/>), as the
    /// router keeps the last address of an absent device, and only when a single client has it, a present one before
    /// absent ones. A client that several subjects match gets none, so the result does not depend on the order of
    /// <paramref name="subjects"/>, and a match by MAC address wins over one by host. The subjects of this library
    /// are never matched.
    /// </summary>
    /// <returns>The subject of each matched client.</returns>
    internal static Dictionary<FritzBoxClient, IInterceptorSubject> Match(
        IEnumerable<IInterceptorSubject> subjects, IReadOnlyCollection<FritzBoxClient> clients)
    {
        var clientIndex = new ClientIndex(clients);
        var byMacAddress = new Dictionary<FritzBoxClient, IInterceptorSubject?>();
        var byHost = new Dictionary<FritzBoxClient, IInterceptorSubject?>();
        foreach (var subject in subjects)
        {
            if (subject is not (FritzBox or FritzBoxClient or FritzBoxAccessPoint or FritzBoxWlan) &&
                subject is INetworkAdapter or INetworkHost)
            {
                ClaimClient(clientIndex, subject, byMacAddress, byHost);
            }
        }

        return Combine(byMacAddress, byHost);
    }

    private static void ClaimClient(
        ClientIndex clientIndex,
        IInterceptorSubject subject,
        Dictionary<FritzBoxClient, IInterceptorSubject?> byMacAddress,
        Dictionary<FritzBoxClient, IInterceptorSubject?> byHost)
    {
        var adapter = subject as INetworkAdapter;
        if (MacAddresses.ToHexDigits(adapter?.MacAddress) is { } macAddress &&
            clientIndex.FindByMacAddress(macAddress) is { } client)
        {
            Claim(byMacAddress, client, subject);
        }
        else if (subject is IConnectionState { IsConnected: true } &&
            clientIndex.FindByHost(adapter?.IpAddress, (subject as INetworkHost)?.HostAddress) is { } hostClient)
        {
            Claim(byHost, hostClient, subject);
        }
    }

    private static Dictionary<FritzBoxClient, IInterceptorSubject> Combine(
        Dictionary<FritzBoxClient, IInterceptorSubject?> byMacAddress, Dictionary<FritzBoxClient, IInterceptorSubject?> byHost)
    {
        var matches = new Dictionary<FritzBoxClient, IInterceptorSubject>(byMacAddress.Count + byHost.Count);
        foreach (var (client, subject) in byMacAddress)
        {
            if (subject is not null)
            {
                matches[client] = subject;
            }
        }

        foreach (var (client, subject) in byHost)
        {
            // A match by MAC address wins over one by host, also when the MAC address match is ambiguous.
            if (subject is not null && !byMacAddress.ContainsKey(client))
            {
                matches[client] = subject;
            }
        }

        return matches;
    }

    private static void Claim(Dictionary<FritzBoxClient, IInterceptorSubject?> claims, FritzBoxClient client, IInterceptorSubject subject)
    {
        // A second subject makes the client ambiguous: null keeps it claimed, so that a third cannot take it.
        claims[client] = claims.TryGetValue(client, out var claimed) && !ReferenceEquals(claimed, subject) ? null : subject;
    }

    /// <returns>
    /// The IP address in its canonical form, without brackets and port and with an IPv4 address mapped to IPv6
    /// as IPv4, or null when it is none.
    /// </returns>
    internal static string? NormalizeIpAddress(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var trimmed = host.AsSpan().Trim();
        if (!TryParseAddress(trimmed, out var address) ||
            // IPAddress also takes a short form, so a host name of digits such as "1234" would be an IPv4 address.
            (address.AddressFamily == AddressFamily.InterNetwork && trimmed.Count('.') != 3))
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private static bool TryParseAddress(ReadOnlySpan<char> host, [NotNullWhen(true)] out IPAddress? address)
    {
        if (IPAddress.TryParse(host.Trim("[]"), out address))
        {
            return true;
        }

        address = IPEndPoint.TryParse(host, out var endPoint) ? endPoint.Address : null;
        return address is not null;
    }

    /// <returns>The first label of a host name in lower case, without port, or null for an IP address.</returns>
    internal static string? GetHostLabel(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || NormalizeIpAddress(host) is not null)
        {
            return null;
        }

        var label = host.AsSpan().Trim();
        var end = label.IndexOfAny('.', ':');
        if (end >= 0)
        {
            label = label[..end];
        }

        return label.IsEmpty ? null : label.ToString().ToLowerInvariant();
    }

    private sealed class ClientIndex
    {
        private readonly Dictionary<string, FritzBoxClient> _byMacAddress = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<FritzBoxClient>> _byIpAddress = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<FritzBoxClient>> _byHostName = new(StringComparer.Ordinal);

        // Reused by every lookup of one match, which runs on one thread.
        private readonly List<FritzBoxClient> _candidates = [];

        public ClientIndex(IReadOnlyCollection<FritzBoxClient> clients)
        {
            foreach (var client in clients)
            {
                if (MacAddresses.ToHexDigits(client.MacAddress) is { } macAddress)
                {
                    _byMacAddress[macAddress] = client;
                }

                Add(_byIpAddress, NormalizeIpAddress(client.IpAddress), client);
                Add(_byHostName, GetHostLabel(client.HostName), client);
            }
        }

        public FritzBoxClient? FindByMacAddress(string macAddress) => _byMacAddress.GetValueOrDefault(macAddress);

        /// <returns>The single client that the IP address or the host address leads to, a present one first.</returns>
        public FritzBoxClient? FindByHost(string? ipAddress, string? hostAddress)
        {
            _candidates.Clear();
            AddCandidates(_byIpAddress, NormalizeIpAddress(ipAddress));
            AddCandidates(_byIpAddress, NormalizeIpAddress(hostAddress));
            AddCandidates(_byHostName, GetHostLabel(hostAddress));

            FritzBoxClient? present = null;
            var presentCount = 0;
            foreach (var candidate in _candidates)
            {
                if (candidate.IsPresent == true)
                {
                    present = candidate;
                    presentCount++;
                }
            }

            if (presentCount > 0)
            {
                return presentCount == 1 ? present : null;
            }

            return _candidates.Count == 1 ? _candidates[0] : null;
        }

        private static void Add(Dictionary<string, List<FritzBoxClient>> index, string? key, FritzBoxClient client)
        {
            if (key is null)
            {
                return;
            }

            if (!index.TryGetValue(key, out var clients))
            {
                clients = [];
                index[key] = clients;
            }

            clients.Add(client);
        }

        private void AddCandidates(Dictionary<string, List<FritzBoxClient>> index, string? key)
        {
            if (key is null || !index.TryGetValue(key, out var clients))
            {
                return;
            }

            foreach (var client in clients)
            {
                if (!_candidates.Contains(client))
                {
                    _candidates.Add(client);
                }
            }
        }
    }
}
