using System.Net;

namespace Namotion.Devices.Tesla.Client;

/// <summary>A Tesla API call failed with an HTTP status. The message never contains a token or a response body.</summary>
public class TeslaApiException : Exception
{
    internal TeslaApiException(HttpStatusCode statusCode, string? error, TimeSpan? retryAfter = null)
        : base(error is null ? $"Tesla API returned {(int)statusCode}." : $"Tesla API returned {(int)statusCode}: {error}")
    {
        StatusCode = statusCode;
        Error = error;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>The error code or message that Tesla returned, or null.</summary>
    public string? Error { get; }

    /// <summary>How long Tesla asks to wait before the next request, or null.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Tesla rejected the refresh token; the user has to provide a new one.</summary>
public sealed class TeslaReauthenticationRequiredException : Exception
{
    internal TeslaReauthenticationRequiredException(string? error)
        : base(error is null
            ? "Tesla rejected the refresh token. Provide a new refresh token."
            : $"Tesla rejected the refresh token ({error}). Provide a new refresh token.")
    {
        Error = error;
    }

    /// <summary>The error code that Tesla returned, or null.</summary>
    public string? Error { get; }
}
