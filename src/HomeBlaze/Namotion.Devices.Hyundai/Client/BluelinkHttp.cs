using System.Net;

namespace Namotion.Devices.Hyundai.Client;

internal static class BluelinkHttp
{
    /// <summary>
    /// Creates the client for all Bluelink hosts. Redirects are not followed, because the sign in reads the code from a
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
        Timeout = TimeSpan.FromSeconds(30)
    };
}
