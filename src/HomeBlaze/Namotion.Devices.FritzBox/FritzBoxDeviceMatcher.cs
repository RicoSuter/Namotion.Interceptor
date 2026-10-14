using System.Net;
using HomeBlaze.Abstractions.Networking;
using Namotion.Interceptor;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// Finds the subject of the device behind each client.
/// </summary>
internal static class FritzBoxDeviceMatcher
{
    /// <summary>
    /// Matches subjects to clients. A subject that reports a MAC address (<see cref="INetworkAdapter"/>) matches the
    /// client with that address only. A subject without one matches by its IP address or by its
    /// <see cref="INetworkHost.HostAddress"/>, as an IP address or as a host name, and only when a single client has
    /// it, a present one before absent ones. A client that several subjects match gets none, so the result does not
    /// depend on the order of <paramref name="subjects"/>. The subjects of this library are never matched.
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
            if (subject is FritzBox or FritzBoxClient or FritzBoxAccessPoint or FritzBoxWlan)
            {
                continue;
            }

            if (MacAddresses.ToHexDigits((subject as INetworkAdapter)?.MacAddress) is { } macAddress)
            {
                if (clientIndex.FindByMacAddress(macAddress) is { } client)
                {
                    Claim(byMacAddress, client, subject);
                }
            }
            else if (clientIndex.FindByHost(subject) is { } client)
            {
                Claim(byHost, client, subject);
            }
        }

        return Combine(byMacAddress, byHost);
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

    /// <returns>The IP address in its canonical form, without brackets and port, or null when it is none.</returns>
    internal static string? NormalizeIpAddress(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var trimmed = host.AsSpan().Trim();
        if (IPAddress.TryParse(trimmed.Trim("[]"), out var address))
        {
            return address.ToString();
        }

        return IPEndPoint.TryParse(trimmed, out var endPoint) ? endPoint.Address.ToString() : null;
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

        /// <returns>The single client the subject's IP address or host address leads to, a present one first.</returns>
        public FritzBoxClient? FindByHost(IInterceptorSubject subject)
        {
            var hostAddress = (subject as INetworkHost)?.HostAddress;
            var candidates = new HashSet<FritzBoxClient>();
            AddCandidates(candidates, _byIpAddress, NormalizeIpAddress((subject as INetworkAdapter)?.IpAddress));
            AddCandidates(candidates, _byIpAddress, NormalizeIpAddress(hostAddress));
            AddCandidates(candidates, _byHostName, GetHostLabel(hostAddress));

            FritzBoxClient? present = null;
            var presentCount = 0;
            foreach (var candidate in candidates)
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

            return candidates.Count == 1 ? candidates.First() : null;
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

        private static void AddCandidates(HashSet<FritzBoxClient> candidates, Dictionary<string, List<FritzBoxClient>> index, string? key)
        {
            if (key is not null && index.TryGetValue(key, out var clients))
            {
                candidates.UnionWith(clients);
            }
        }
    }
}
