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
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.Descendants(DeviceNamespace + "serviceId"))
        {
            // The value of an element that holds elements is a new string of all the text below it, so a document
            // that nests service ids around a long text would copy that text once per level.
            if (!element.HasElements)
            {
                var value = element.Value;
                serviceIds.Add(value[(value.LastIndexOf(':') + 1)..]);
            }
        }

        return new SonosDeviceDescription(
            SonosValues.NullIfEmpty((string?)device?.Element(DeviceNamespace + "modelName")),
            SonosValues.NullIfEmpty((string?)device?.Element(DeviceNamespace + "modelNumber")),
            serviceIds);
    }
}
