using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>
/// The partitions of <see cref="AuthenticationWebExtensions.LoginRateLimitPolicy"/>: posts are limited to 10 per minute
/// per IPv4 address or IPv6 /64 network, as one host usually holds a whole /64; posts without a client address share a
/// stricter limit of 3 per minute. Other requests than posts are not limited.
/// </summary>
internal static class LoginRateLimit
{
    private const string NoAddressKey = "no address";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimitPartition<string> GetPartition(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        if (context.Connection.RemoteIpAddress is not { } address)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LoginRateLimit));
            // The options are created once per partition, so the warning is logged once while the partition lives.
            return RateLimitPartition.GetFixedWindowLimiter(NoAddressKey, _ =>
            {
                logger.LogWarning("Sign-in attempts arrive without a client address, so they share one limit of 3 per minute.");
                return new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = Window };
            });
        }

        return RateLimitPartition.GetFixedWindowLimiter(GetKey(address), _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = Window });
    }

    /// <summary>Gets the partition key of <paramref name="address"/>: the IPv4 address, or the IPv6 /64 network.</summary>
    public static string GetKey(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes) + "/64";
    }
}
