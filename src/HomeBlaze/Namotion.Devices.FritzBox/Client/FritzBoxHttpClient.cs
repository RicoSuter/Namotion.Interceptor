using System.Net;
using System.Net.Security;

namespace Namotion.Devices.FritzBox.Client;

internal static class FritzBoxHttpClient
{
    /// <summary>
    /// Creates the client for the authenticated HTTPS requests against a router on the local network: Digest
    /// credentials, a pooled connection limit, no redirects, no proxy and the pinned key instead of chain validation.
    /// Its timeout is infinite, so each request must bring its own.
    /// </summary>
    internal static HttpClient Create(FritzBoxConnectionOptions options, PinnedCertificateValidator certificateValidator, int maximumAnswerBytes)
    {
        var handler = new SocketsHttpHandler
        {
            Credentials = new NetworkCredential(options.Username ?? string.Empty, options.Password ?? string.Empty),
            AllowAutoRedirect = false,
            UseProxy = false,
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = certificateValidator.Validate }
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = maximumAnswerBytes
        };
    }

    /// <summary>
    /// Creates a client for an unauthenticated plain HTTP request against a router on the local network: no
    /// redirects and no proxy, since a redirect or a proxied route could point away from the router that was asked.
    /// </summary>
    internal static HttpClient CreatePlain(TimeSpan timeout, int maximumResponseBytes)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        return new HttpClient(handler)
        {
            Timeout = timeout,
            MaxResponseContentBufferSize = maximumResponseBytes
        };
    }
}
