namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// A UPnP service of a Sonos unit: its name and the path its actions are posted to.
/// </summary>
internal sealed class SonosService
{
    internal static readonly SonosService AvTransport = new("AVTransport", "/MediaRenderer/AVTransport/Control");
    internal static readonly SonosService RenderingControl = new("RenderingControl", "/MediaRenderer/RenderingControl/Control");
    internal static readonly SonosService GroupRenderingControl = new("GroupRenderingControl", "/MediaRenderer/GroupRenderingControl/Control");
    internal static readonly SonosService DeviceProperties = new("DeviceProperties", "/DeviceProperties/Control");
    internal static readonly SonosService ZoneGroupTopology = new("ZoneGroupTopology", "/ZoneGroupTopology/Control");
    internal static readonly SonosService ContentDirectory = new("ContentDirectory", "/MediaServer/ContentDirectory/Control");

    private SonosService(string name, string controlPath)
    {
        Name = name;
        ControlPath = controlPath;
        Namespace = $"urn:schemas-upnp-org:service:{name}:1";
    }

    internal string Name { get; }

    internal string ControlPath { get; }

    /// <summary>
    /// The namespace of the action element, which is also the prefix of the SOAPACTION header.
    /// </summary>
    internal string Namespace { get; }
}
