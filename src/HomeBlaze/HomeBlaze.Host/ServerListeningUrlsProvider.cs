using HomeBlaze.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace HomeBlaze.Host;

/// <summary>
/// Reads the addresses the web server listens on.
/// </summary>
internal sealed class ServerListeningUrlsProvider(IServer server) : IListeningUrlsProvider
{
    // Kestrel's address list is not synchronized; it is only read after the server started, which the hosted service order guarantees.
    public IReadOnlyList<string> GetListeningUrls() =>
        server.Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];
}
