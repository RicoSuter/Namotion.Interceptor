using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// The partitions of <see cref="AuthenticationWebExtensions.UiStartRateLimitPolicy"/>: requests that are not signed in
/// are limited to 60 per minute per IPv4 address or IPv6 /64 network, as for <see cref="LoginRateLimit"/>, and requests
/// without a client address share one such limit; signed-in requests are not limited. Loading the app takes about four
/// such requests (the page, and the negotiation, connection and disconnection of the live connection), so a dozen loads
/// a minute stay below the limit.
/// </summary>
internal static class UiStartRateLimit
{
    /// <summary>The requests that are not signed in that one address may send per minute.</summary>
    public const int PermitLimit = 60;

    private const string NoAddressKey = "no address";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimitPartition<string> GetPartition(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        var key = context.Connection.RemoteIpAddress is { } address ? LoginRateLimit.GetKey(address) : NoAddressKey;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = Window });
    }
}
