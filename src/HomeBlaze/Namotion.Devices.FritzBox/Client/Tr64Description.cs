using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Namotion.Devices.Upnp;

namespace Namotion.Devices.FritzBox.Client;

/// <summary>
/// The TR-064 description of a FRITZ!Box: its identity and the control path of each service it offers.
/// </summary>
internal sealed class Tr64Description
{
    internal const int DescriptionPort = 49000;
    internal const string DescriptionPath = "/tr64desc.xml";
    internal const int MaximumBytes = 1024 * 1024;

    private readonly Dictionary<string, UpnpService> _services;

    private Tr64Description(string? friendlyName, string? modelName, string? udn, Dictionary<string, UpnpService> services)
    {
        FriendlyName = friendlyName;
        ModelName = modelName;
        Udn = udn;
        _services = services;
        WlanServiceTypes = services.Keys
            .Where(serviceType => serviceType.StartsWith(Tr64Services.WlanConfigurationPrefix, StringComparison.Ordinal))
            .OrderBy(GetVersion)
            .ToArray();
    }

    internal string? FriendlyName { get; }

    internal string? ModelName { get; }

    internal string? Udn { get; }

    /// <summary>
    /// The WLAN services in the order of their version, which is the order of the router's WLANs.
    /// </summary>
    internal IReadOnlyList<string> WlanServiceTypes { get; }

    internal bool HasService(string serviceType) => _services.ContainsKey(serviceType);

    internal UpnpService? TryGetService(string serviceType) =>
        _services.TryGetValue(serviceType, out var service) ? service : null;

    /// <summary>
    /// Reads the description from <c>http://host:port/tr64desc.xml</c> without authentication. Throws what
    /// <see cref="HttpClient.GetStringAsync(Uri, CancellationToken)"/> and <see cref="Parse"/> throw.
    /// </summary>
    internal static async Task<Tr64Description> LoadAsync(HttpClient httpClient, string host, int port, CancellationToken cancellationToken)
    {
        var uri = new UriBuilder(Uri.UriSchemeHttp, host, port, DescriptionPath).Uri;
        return Parse(await httpClient.GetStringAsync(uri, cancellationToken));
    }

    /// <summary>
    /// Parses a description. Throws <see cref="XmlException"/> when it is malformed, declares a document type or
    /// nests too deeply. A service whose control URL is not a path on the same host is left out.
    /// </summary>
    internal static Tr64Description Parse(string xml)
    {
        var root = UpnpXml.Parse(xml).Root ?? throw new XmlException("The description has no root element.");
        var ns = root.Name.Namespace;
        var device = root.Element(ns + "device");

        return new Tr64Description(
            GetElementValue(device, ns, "friendlyName"),
            GetElementValue(device, ns, "modelName"),
            GetElementValue(device, ns, "UDN"),
            ReadServices(root, ns));
    }

    private static Dictionary<string, UpnpService> ReadServices(XElement root, XNamespace ns)
    {
        var services = new Dictionary<string, UpnpService>(StringComparer.Ordinal);
        foreach (var service in root.Descendants(ns + "service"))
        {
            var serviceType = GetElementValue(service, ns, "serviceType");
            var controlUrl = service.Element(ns + "controlURL")?.Value.Trim();
            if (serviceType is not null && IsLocalPath(controlUrl))
            {
                services.TryAdd(serviceType, new UpnpService(serviceType, controlUrl));
            }
        }

        return services;
    }

    private static string? GetElementValue(XElement? parent, XNamespace ns, string name) =>
        StringValues.NullIfBlank(parent?.Element(ns + name)?.Value);

    /// <returns>Whether the value is a rooted path on the host it came from: not a URI and not protocol-relative.</returns>
    internal static bool IsLocalPath([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '/')
        {
            return false;
        }

        if (value.Length > 1 && (value[1] == '/' || value[1] == '\\'))
        {
            return false;
        }

        return !value.Contains("://", StringComparison.Ordinal);
    }

    private static int GetVersion(string serviceType) =>
        int.TryParse(serviceType.AsSpan(serviceType.LastIndexOf(':') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            ? version
            : int.MaxValue;
}
