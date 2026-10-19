namespace Namotion.Devices.Nuki.Client;

internal static class NukiBridgeHttpClient
{
    /// <summary>
    /// Creates the client for requests to a bridge. It opens a new connection for every request, because the HTTP
    /// server of the bridge corrupts a response on a reused connection.
    /// </summary>
    public static HttpClient Create() =>
        // A zero lifetime disposes a connection once its response is read instead of returning it to the pool. A
        // request's "Connection: close" header would not, as the handler only honors that header on the response.
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero });
}
