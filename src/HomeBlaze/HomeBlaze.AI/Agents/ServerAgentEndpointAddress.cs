using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// Derives the agent route from the addresses the web server listens on, with wildcard and localhost hosts
/// rewritten to the IPv4 loopback address.
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
    /// Picks the first HTTP address (HTTPS when there is none) and points it at the agent route; wildcard and
    /// localhost hosts become 127.0.0.1.
    /// </summary>
    public static Uri? Resolve(IEnumerable<string> addresses)
    {
        var list = addresses.ToList();
        var address = list.FirstOrDefault(value => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault();
        if (address is null)
        {
            return null;
        }

        // Kestrel reports wildcard bindings as "+" or "*", which Uri cannot parse.
        address = address.Replace("://+", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://*", "://127.0.0.1", StringComparison.Ordinal);
        var builder = new UriBuilder(address);
        if (builder.Host is "0.0.0.0" or "[::]" or "localhost")
        {
            builder.Host = "127.0.0.1";
        }

        builder.Path = AgentMcpEndpoint.Route;
        return builder.Uri;
    }
}
