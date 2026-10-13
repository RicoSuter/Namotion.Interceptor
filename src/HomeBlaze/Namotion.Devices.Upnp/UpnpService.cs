namespace Namotion.Devices.Upnp;

/// <summary>
/// A UPnP service of a device: the type its actions are named in and the path they are posted to.
/// </summary>
public sealed class UpnpService
{
    /// <param name="serviceType">The full service type, for example <c>urn:schemas-upnp-org:service:AVTransport:1</c>.</param>
    /// <param name="controlPath">The control path, resolved against the base URI of the client.</param>
    public UpnpService(string serviceType, string controlPath)
    {
        ServiceType = serviceType;
        ControlPath = controlPath;
    }

    /// <summary>
    /// The namespace of the action element, which is also the prefix of the SOAPACTION header.
    /// </summary>
    public string ServiceType { get; }

    /// <summary>
    /// The control path, resolved against the base URI of the client.
    /// </summary>
    public string ControlPath { get; }
}
