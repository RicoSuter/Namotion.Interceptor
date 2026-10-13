namespace Namotion.Devices.Sonos;

/// <summary>
/// A speaker answered an action with a UPnP fault: it is reachable, but refused the action.
/// </summary>
public sealed class SonosFaultException : Exception
{
    internal SonosFaultException(string action, int? upnpErrorCode, string? faultString)
        : base(CreateMessage(action, upnpErrorCode, faultString))
    {
        Action = action;
        UpnpErrorCode = upnpErrorCode;
    }

    /// <summary>
    /// The UPnP action the speaker refused, for example <c>Next</c>.
    /// </summary>
    public string Action { get; }

    /// <summary>
    /// The UPnP error code, for example 701 when the current source does not allow the action; null when the
    /// fault carried none.
    /// </summary>
    public int? UpnpErrorCode { get; }

    private static string CreateMessage(string action, int? upnpErrorCode, string? faultString)
    {
        if (upnpErrorCode is not { } code)
        {
            return $"The Sonos speaker refused {action}: {faultString ?? "unknown fault"}.";
        }

        return GetDescription(code) is { } description
            ? $"The Sonos speaker refused {action}: {description} (UPnP error {code})."
            : $"The Sonos speaker refused {action} (UPnP error {code}).";
    }

    // The codes of the UPnP device architecture and AVTransport specifications that these actions answer with.
    private static string? GetDescription(int code) => code switch
    {
        401 => "invalid action",
        402 => "invalid arguments",
        501 => "action failed",
        701 => "not available for the current source or state",
        711 => "illegal seek target",
        712 => "play mode not supported",
        714 => "content type not supported",
        _ => null
    };
}
