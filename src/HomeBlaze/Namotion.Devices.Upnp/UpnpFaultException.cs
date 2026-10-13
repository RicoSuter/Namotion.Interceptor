namespace Namotion.Devices.Upnp;

/// <summary>
/// A device answered an action with a UPnP fault: it is reachable, but refused the action.
/// </summary>
public sealed class UpnpFaultException : Exception
{
    public UpnpFaultException(string serviceType, string action, int? errorCode, string? faultText)
        : base(CreateMessage(action, errorCode, faultText))
    {
        ServiceType = serviceType;
        Action = action;
        ErrorCode = errorCode;
        FaultText = faultText;
    }

    /// <summary>
    /// The service type of the refused action.
    /// </summary>
    public string ServiceType { get; }

    /// <summary>
    /// The refused action, for example <c>GetInfo</c>.
    /// </summary>
    public string Action { get; }

    /// <summary>
    /// The UPnP error code; null when the fault carried none. Codes from 700 on are defined by each service.
    /// </summary>
    public int? ErrorCode { get; }

    /// <summary>
    /// The fault string the device sent.
    /// </summary>
    public string? FaultText { get; }

    /// <returns>The description of a code every UPnP service shares, or null for any other code.</returns>
    public static string? GetStandardDescription(int errorCode) => errorCode switch
    {
        401 => "invalid action",
        402 => "invalid arguments",
        501 => "action failed",
        606 => "action not authorized",
        _ => null
    };

    private static string CreateMessage(string action, int? errorCode, string? faultText)
    {
        if (errorCode is not { } code)
        {
            return $"The device refused {action}: {faultText ?? "unknown fault"}.";
        }

        return GetStandardDescription(code) is { } description
            ? $"The device refused {action}: {description} (UPnP error {code})."
            : $"The device refused {action} (UPnP error {code}).";
    }
}
