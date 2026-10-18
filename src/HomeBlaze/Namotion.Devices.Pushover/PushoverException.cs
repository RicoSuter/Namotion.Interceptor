using System.Net;
using Namotion.Devices.Pushover.Client;

namespace Namotion.Devices.Pushover;

/// <summary>
/// The Pushover API rejected a request, or sending to some targets failed.
/// </summary>
public sealed class PushoverException : Exception
{
    public PushoverException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal PushoverException(HttpStatusCode statusCode, PushoverResponse? response)
        : base(BuildMessage(statusCode, response))
    {
        StatusCode = statusCode;
        IsApplicationTokenInvalid = response?.Token == "invalid";
    }

    /// <summary>
    /// The HTTP status of the rejected request, or null when no request was rejected.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Whether Pushover rejected the application token.
    /// </summary>
    public bool IsApplicationTokenInvalid { get; }

    private static string BuildMessage(HttpStatusCode statusCode, PushoverResponse? response)
    {
        if (response?.Errors is { Length: > 0 } errors)
        {
            return string.Join("; ", errors);
        }

        return (int)statusCode is >= 200 and <= 299
            ? $"Pushover rejected the request (HTTP {(int)statusCode})."
            : $"Pushover returned HTTP {(int)statusCode}.";
    }
}
