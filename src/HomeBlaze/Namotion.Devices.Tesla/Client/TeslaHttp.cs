using System.Net;
using System.Security.Authentication;

namespace Namotion.Devices.Tesla.Client;

/// <summary>
/// Creates the HTTP client for Tesla: since June 2026 Tesla rejects requests that do not use HTTP/2 and TLS 1.3.
/// </summary>
internal static class TeslaHttp
{
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(5);

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        SslOptions = { EnabledSslProtocols = SslProtocols.Tls13 },
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        // A refresh that follows a redirect produced unusable tokens in other Owner API clients.
        AllowAutoRedirect = false
    })
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Creates a request with the HTTP version of the client: a manually created request defaults to HTTP/1.1,
    /// which Tesla rejects.
    /// </summary>
    public static HttpRequestMessage CreateRequest(this HttpClient httpClient, HttpMethod method, string url) => new(method, url)
    {
        Version = httpClient.DefaultRequestVersion,
        VersionPolicy = httpClient.DefaultVersionPolicy
    };

    /// <summary>
    /// Reads the Retry-After header of a 429 response in its delay or date form, or returns five minutes when it is missing.
    /// </summary>
    public static TimeSpan GetRetryAfter(HttpResponseMessage response, TimeProvider timeProvider)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - timeProvider.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return DefaultRetryAfter;
    }
}
