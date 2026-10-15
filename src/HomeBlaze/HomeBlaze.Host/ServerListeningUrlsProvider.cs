using HomeBlaze.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace HomeBlaze.Host;

/// <summary>
/// Reads the addresses the web server listens on.
/// </summary>
internal sealed class ServerListeningUrlsProvider(IServer server) : IListeningUrlsProvider
{
    public IReadOnlyList<string> GetListeningUrls() =>
        server.Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];
}
