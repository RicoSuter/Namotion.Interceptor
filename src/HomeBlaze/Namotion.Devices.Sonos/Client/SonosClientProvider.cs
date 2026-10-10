using Microsoft.Extensions.Logging;
using Sonos.Base;

namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// Hands Sonos.Base the one HttpClient of the current connection, so all service calls share its handler and
/// timeout. Sonos.Base never disposes the client it is given. Events go through SonosEventListener instead of
/// a Sonos.Base event bus.
/// </summary>
internal sealed class SonosClientProvider(HttpClient httpClient) : ISonosServiceProvider
{
    public HttpClient GetHttpClient() => httpClient;

    public IHttpClientFactory? GetHttpClientFactory() => null;

    public ILogger<TCategoryName>? CreateLogger<TCategoryName>() => null;

    public ILogger? CreateLogger(string categoryName) => null;

    public ILoggerFactory? GetLoggerFactory() => null;

    public ISonosEventBus? GetSonosEventBus() => null;
}
