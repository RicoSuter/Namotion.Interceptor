using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// The parts of <c>/xml/device_description.xml</c> the subjects use.
/// </summary>
internal sealed record SonosDeviceDescription(string? ModelName, string? ModelNumber, IReadOnlySet<string> ServiceIds);

internal static class DeviceDescriptionParser
{
    private static readonly XNamespace DeviceNamespace = "urn:schemas-upnp-org:device-1-0";

    internal static SonosDeviceDescription Parse(string xml)
    {
        var document = SonosXml.Parse(xml);
        var device = document.Root?.Element(DeviceNamespace + "device");

        // Service ids look like "urn:upnp-org:serviceId:AVTransport"; nested devices hold the media services.
        var serviceIds = document.Descendants(DeviceNamespace + "serviceId")
            .Select(element => element.Value[(element.Value.LastIndexOf(':') + 1)..])
            .ToHashSet(StringComparer.Ordinal);

        return new SonosDeviceDescription(
            SonosValues.NullIfEmpty((string?)device?.Element(DeviceNamespace + "modelName")),
            SonosValues.NullIfEmpty((string?)device?.Element(DeviceNamespace + "modelNumber")),
            serviceIds);
    }
}
