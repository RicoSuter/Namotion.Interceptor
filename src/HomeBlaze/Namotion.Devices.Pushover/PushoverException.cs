using System.Net;
using Namotion.Devices.Pushover.Client;

namespace Namotion.Devices.Pushover;

/// <summary>
/// The Pushover API rejected a request, or sending to some targets failed.
/// </summary>
public sealed class PushoverException : Exception
{
    public PushoverException()
    {
    }

    public PushoverException(string message)
        : base(message)
    {
    }

    public PushoverException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal PushoverException(HttpStatusCode statusCode, PushoverResponse? response)
        : base(response?.Errors is { Length: > 0 } errors
            ? string.Join("; ", errors)
            : $"Pushover returned HTTP {(int)statusCode}.")
    {
        StatusCode = statusCode;
        IsApplicationTokenInvalid = response?.Token == "invalid";
        IsUserKeyInvalid = response?.User == "invalid";
    }

    /// <summary>
    /// The HTTP status of the rejected request, or null when no request was rejected.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Whether Pushover rejected the application token.
    /// </summary>
    public bool IsApplicationTokenInvalid { get; }

    /// <summary>
    /// Whether Pushover rejected the user or group key.
    /// </summary>
    public bool IsUserKeyInvalid { get; }
}
