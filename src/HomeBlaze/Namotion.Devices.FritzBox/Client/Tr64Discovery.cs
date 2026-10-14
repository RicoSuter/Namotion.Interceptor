using System.Xml;
using Namotion.Devices.Upnp;

namespace Namotion.Devices.FritzBox.Client;

internal static class Tr64Discovery
{
    /// <summary>
    /// Reads the description and the HTTPS port over HTTP without authentication.
    /// </summary>
    /// <returns>The description and the base URI of the authenticated HTTPS interface.</returns>
    internal static async Task<(Tr64Description Description, Uri BaseUri)> DiscoverAsync(
        FritzBoxConnectionOptions options, CancellationToken cancellationToken)
    {
        var descriptionUri = new UriBuilder(Uri.UriSchemeHttp, options.HostAddress, options.DescriptionPort, Tr64Description.DescriptionPath).Uri;
        using var plainClient = new HttpClient
        {
            Timeout = options.RequestTimeout,
            MaxResponseContentBufferSize = Tr64Description.MaximumBytes
        };

        var description = Tr64Description.Parse(await plainClient.GetStringAsync(descriptionUri, cancellationToken));
        var deviceInfo = description.TryGetService(Tr64Services.DeviceInfo)
            ?? throw new FritzBoxFirmwareException("The FRITZ!Box description lists no DeviceInfo service.");
        var securityPort = (await new UpnpSoapClient(plainClient, descriptionUri).QueryAsync(deviceInfo, "GetSecurityPort", [], cancellationToken))
            .GetInt32("NewSecurityPort")
            ?? throw new XmlException("The FRITZ!Box did not report its HTTPS port.");

        return (description, new UriBuilder(Uri.UriSchemeHttps, options.HostAddress, securityPort).Uri);
    }
}
