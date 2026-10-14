namespace Namotion.Devices.Upnp;

/// <summary>
/// A device answered an action with a UPnP fault: it is reachable, but refused the action.
/// </summary>
public sealed class UpnpFaultException : Exception
{
    private static readonly IReadOnlyDictionary<int, string> StandardDescriptions = new Dictionary<int, string>
    {
        [401] = "invalid action",
        [402] = "invalid arguments",
        [501] = "action failed",
        [600] = "argument value invalid",
        [601] = "argument value out of range",
        [602] = "optional action not implemented",
        [603] = "out of memory",
        [604] = "human intervention required",
        [605] = "string argument too long",
        [606] = "action not authorized"
    };

    /// <summary>
    /// Creates the exception for a UPnP fault a device answered an action with.
    /// </summary>
    public UpnpFaultException(string serviceType, string action, int? errorCode, string? faultText)
        : base(CreateMessage(action, errorCode, faultText))
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(action);

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

    /// <summary>
    /// Describes a UPnP error code every service shares; a code from 700 on is defined by each service instead.
    /// </summary>
    /// <param name="errorCode">The UPnP error code to describe.</param>
    /// <returns>The description of the code, or null when it is not one every UPnP service shares.</returns>
    public static string? GetStandardDescription(int errorCode) => StandardDescriptions.GetValueOrDefault(errorCode);

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
