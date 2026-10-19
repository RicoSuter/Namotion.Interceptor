namespace Namotion.Devices.FritzBox.Client;

/// <summary>
/// The TR-064 service types this library uses.
/// </summary>
internal static class Tr64Services
{
    internal const string DeviceInfo = "urn:dslforum-org:service:DeviceInfo:1";
    internal const string UserInterface = "urn:dslforum-org:service:UserInterface:1";
    internal const string Layer3Forwarding = "urn:dslforum-org:service:Layer3Forwarding:1";
    internal const string WanCommonInterfaceConfig = "urn:dslforum-org:service:WANCommonInterfaceConfig:1";
    internal const string WanPppConnection = "urn:dslforum-org:service:WANPPPConnection:1";
    internal const string WanIpConnection = "urn:dslforum-org:service:WANIPConnection:1";
    internal const string Hosts = "urn:dslforum-org:service:Hosts:1";
    internal const string AppSetup = "urn:dslforum-org:service:X_AVM-DE_AppSetup:1";

    /// <summary>
    /// Every WLAN of the router is its own version of this service: <c>:1</c>, <c>:2</c> and so on.
    /// </summary>
    internal const string WlanConfigurationPrefix = "urn:dslforum-org:service:WLANConfiguration:";
}
