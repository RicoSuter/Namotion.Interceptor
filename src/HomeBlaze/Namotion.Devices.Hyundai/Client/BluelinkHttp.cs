using System.Net;

namespace Namotion.Devices.Hyundai.Client;

internal static class BluelinkHttp
{
    /// <summary>
    /// Creates the client for all Bluelink hosts. Its timeout of 2 minutes is the upper bound of every call;
    /// <see cref="SendWithTimeoutAsync"/> applies shorter ones. Redirects are not followed, because the sign in reads the code from a
    /// 302, and cookies are handled per login (<see cref="BluelinkAuthClient"/>), so that two logins never share them.
    /// </summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Hyundai answers the forced refresh only once the car answered, which took about 25 seconds for a legacy vehicle.</summary>
    private static readonly TimeSpan StatusRequestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Sends the request and returns the response with its content already read, within 30 seconds, or 90 seconds when the
    /// request waits for the car. A caller cancellation is not a timeout.
    /// </summary>
    /// <exception cref="TimeoutException">The response did not arrive in time and the caller did not cancel.</exception>
    public static async Task<HttpResponseMessage> SendWithTimeoutAsync(
        this HttpClient httpClient, TimeProvider timeProvider, HttpRequestMessage message, bool waitsForCar, CancellationToken cancellationToken)
    {
        var timeout = waitsForCar ? StatusRequestTimeout : DefaultRequestTimeout;
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            return await httpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead, linkedSource.Token);
        }
        catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Bluelink request timed out after {(int)timeout.TotalSeconds} seconds.", exception);
        }
    }
}
