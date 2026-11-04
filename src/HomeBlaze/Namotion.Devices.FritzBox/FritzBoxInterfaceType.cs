namespace Namotion.Devices.FritzBox;

/// <summary>
/// How a client is connected, as the host list reports it.
/// </summary>
public enum FritzBoxInterfaceType
{
    Unknown,
    Ethernet,
    Wlan,
    HomePlug
}

internal static class FritzBoxInterfaceTypes
{
    internal static FritzBoxInterfaceType Parse(string? value) => value?.Trim() switch
    {
        "Ethernet" => FritzBoxInterfaceType.Ethernet,
        "802.11" => FritzBoxInterfaceType.Wlan,
        "HomePlug" => FritzBoxInterfaceType.HomePlug,
        _ => FritzBoxInterfaceType.Unknown
    };
}
