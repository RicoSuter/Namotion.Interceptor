using Microsoft.AspNetCore.Http;

namespace HomeBlaze.Authentication.Blazor;

/// <summary>Compares the <c>Origin</c> header of a request with the request itself.</summary>
internal static class RequestOrigins
{
    /// <summary>
    /// Whether <paramref name="request"/> carries an <c>Origin</c> header naming another scheme, host or port than the
    /// request; false when it carries none. An origin that is no absolute address, such as <c>null</c>, counts as another.
    /// </summary>
    public static bool IsCrossOrigin(HttpRequest request) =>
        request.Headers.Origin is { Count: > 0 } origin && !IsSameOrigin(origin.ToString(), request);

    private static bool IsSameOrigin(string origin, HttpRequest request)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !request.Host.HasValue)
        {
            return false;
        }

        var requestPort = request.Host.Port ?? GetDefaultPort(request.Scheme);
        return string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(originUri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
            originUri.Port == requestPort;
    }

    private static int GetDefaultPort(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
}
