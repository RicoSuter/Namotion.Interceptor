using System.Net;

namespace Namotion.Devices.Tesla.Client;

/// <summary>A Tesla API call failed with an HTTP status. The message never contains a token or a response body.</summary>
public class TeslaApiException(HttpStatusCode statusCode, string? error, TimeSpan? retryAfter = null)
    : Exception(error is null ? $"Tesla API returned {(int)statusCode}." : $"Tesla API returned {(int)statusCode}: {error}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>The error code or message that Tesla returned, or null.</summary>
    public string? Error { get; } = error;

    /// <summary>How long Tesla asks to wait before the next request, or null.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Tesla rejected the refresh token; the user has to provide a new one.</summary>
public sealed class TeslaReauthenticationRequiredException(string? error)
    : Exception(error is null
        ? "Tesla rejected the refresh token. Provide a new refresh token."
        : $"Tesla rejected the refresh token ({error}). Provide a new refresh token.")
{
    /// <summary>The error code that Tesla returned, or null.</summary>
    public string? Error { get; } = error;
}
