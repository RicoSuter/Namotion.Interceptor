using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Derives the agent route from the addresses the web server listens on, preferring a loopback address.
/// </summary>
public sealed class ServerAgentEndpointAddress : IAgentEndpointAddress
{
    private readonly IServer _server;

    public ServerAgentEndpointAddress(IServer server)
    {
        _server = server;
    }

    public Uri? GetAgentEndpoint() =>
        Resolve(_server.Features.Get<IServerAddressesFeature>()?.Addresses ?? []);

    /// <summary>
    /// Picks the first usable address in this order: a loopback or wildcard binding over HTTP, then over HTTPS, then
    /// any other IP binding over HTTP, then over HTTPS. Wildcard and localhost hosts become 127.0.0.1; bindings that
    /// are not IP endpoints, such as Unix sockets and named pipes, are skipped.
    /// </summary>
    public static Uri? Resolve(IEnumerable<string> addresses)
    {
        UriBuilder? best = null;
        var bestRank = int.MaxValue;
        foreach (var address in addresses)
        {
            var candidate = TryCreateCandidate(address, out var rank);
            if (candidate is not null && rank < bestRank)
            {
                best = candidate;
                bestRank = rank;
            }
        }

        if (best is null)
        {
            return null;
        }

        best.Path = AgentMcpEndpoint.Route;
        return best.Uri;
    }

    private static UriBuilder? TryCreateCandidate(string address, out int rank)
    {
        rank = int.MaxValue;

        // Kestrel reports wildcard bindings as "+" or "*", which Uri cannot parse.
        address = address.Replace("://+", "://0.0.0.0", StringComparison.Ordinal)
            .Replace("://*", "://0.0.0.0", StringComparison.Ordinal);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var isHttp = uri.Scheme == Uri.UriSchemeHttp;
        if (!isHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        bool isLocal;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            isLocal = true;
        }
        else if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 &&
            IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ipAddress))
        {
            isLocal = IPAddress.IsLoopback(ipAddress) || ipAddress.Equals(IPAddress.Any) || ipAddress.Equals(IPAddress.IPv6Any);
        }
        else
        {
            return null;
        }

        rank = (isLocal ? 0 : 2) + (isHttp ? 0 : 1);
        var builder = new UriBuilder(uri);
        if (builder.Host is "0.0.0.0" or "[::]" || string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            builder.Host = "127.0.0.1";
        }

        return builder;
    }
}
