namespace Namotion.Devices.MyStrom;

/// <summary>
/// The myStrom device types that <see cref="MyStromSwitch"/> supports, numbered as in the myStrom REST API
/// documentation (https://api.mystrom.ch).
/// </summary>
internal static class MyStromSwitchTypes
{
    /// <summary>
    /// Gets the literal type of a supported switch, as <see cref="MyStromSwitch.DeviceType"/> reports it.
    /// </summary>
    /// <param name="numericType">The numeric device type.</param>
    /// <returns>The literal type, such as <c>WS2</c>, or null when the type is not a supported switch.</returns>
    public static string? GetLiteralType(byte numericType) => numericType switch
    {
        // The documentation lists the report, relay and temperature requests that MyStromSwitch polls for the
        // Switch CH and Switch EU only, so the Switch CH v1 (101, WSW) and the Switch Zero (120, LCS) are left out.
        106 => "WS2", // Switch CH v2
        107 => "WSE", // Switch EU
        _ => null
    };
}
