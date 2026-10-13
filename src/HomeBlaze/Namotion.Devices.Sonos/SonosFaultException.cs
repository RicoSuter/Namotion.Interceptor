using Namotion.Devices.Sonos.Client;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A speaker answered an action with a UPnP fault: it is reachable, but refused the action.
/// </summary>
public sealed class SonosFaultException : Exception
{
    internal SonosFaultException(SonosService service, string action, int? upnpErrorCode, string? faultString)
        : base(CreateMessage(service, action, upnpErrorCode, faultString))
    {
        Action = action;
        UpnpErrorCode = upnpErrorCode;
    }

    /// <summary>
    /// The UPnP action the speaker refused, for example <c>Next</c>.
    /// </summary>
    public string Action { get; }

    /// <summary>
    /// The UPnP error code, for example 701 when the current source does not allow a transport action; null when
    /// the fault carried none.
    /// </summary>
    public int? UpnpErrorCode { get; }

    private static string CreateMessage(SonosService service, string action, int? upnpErrorCode, string? faultString)
    {
        if (upnpErrorCode is not { } code)
        {
            return $"The Sonos speaker refused {action}: {faultString ?? "unknown fault"}.";
        }

        return GetDescription(service, code) is { } description
            ? $"The Sonos speaker refused {action}: {description} (UPnP error {code})."
            : $"The Sonos speaker refused {action} (UPnP error {code}).";
    }

    // Codes below 700 mean the same for every service; from 700 on each service defines its own, and only those of
    // the transport service are described.
    private static string? GetDescription(SonosService service, int code) => code switch
    {
        401 => "invalid action",
        402 => "invalid arguments",
        501 => "action failed",
        701 when service == SonosService.AvTransport => "not available for the current source or state",
        711 when service == SonosService.AvTransport => "illegal seek target",
        712 when service == SonosService.AvTransport => "play mode not supported",
        714 when service == SonosService.AvTransport => "content type not supported",
        _ => null
    };
}
