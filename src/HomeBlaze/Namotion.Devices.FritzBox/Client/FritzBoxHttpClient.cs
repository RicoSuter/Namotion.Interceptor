using System.Net;
using System.Net.Security;

namespace Namotion.Devices.FritzBox.Client;

internal static class FritzBoxHttpClient
{
    /// <summary>
    /// Creates the client for the authenticated HTTPS requests: Digest credentials, a pooled connection limit, no
    /// redirects and the pinned key instead of chain validation. Its timeout is infinite, so each request must bring
    /// its own.
    /// </summary>
    internal static HttpClient Create(FritzBoxConnectionOptions options, PinnedCertificateValidator certificateValidator, int maximumAnswerBytes)
    {
        var handler = new SocketsHttpHandler
        {
            Credentials = new NetworkCredential(options.Username ?? string.Empty, options.Password ?? string.Empty),
            AllowAutoRedirect = false,
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
}
