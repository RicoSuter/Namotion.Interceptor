using System.Net;
using System.Xml;
using Namotion.Devices.Upnp;

namespace Namotion.Devices.FritzBox.Client;

internal static class Tr64Discovery
{
    /// <summary>
    /// Reads the description and the HTTPS port over HTTP without authentication. Throws
    /// <see cref="TimeoutException"/> when the router does not answer within the request timeout and
    /// <see cref="InvalidDataException"/> when it reports an HTTPS port out of range.
    /// </summary>
    /// <returns>The description and the base URI of the authenticated HTTPS interface.</returns>
    internal static async Task<(Tr64Description Description, Uri BaseUri)> DiscoverAsync(
        FritzBoxConnectionOptions options, CancellationToken cancellationToken)
    {
        using var plainClient = FritzBoxHttpClient.CreatePlain(Timeout.InfiniteTimeSpan, Tr64Description.MaximumBytes);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            var description = await Tr64Description.LoadAsync(plainClient, options.HostAddress, options.DescriptionPort, timeout.Token);
            var deviceInfo = description.TryGetService(Tr64Services.DeviceInfo)
                ?? throw new FritzBoxFirmwareException("The FRITZ!Box description lists no DeviceInfo service.");
            var plainUri = new UriBuilder(Uri.UriSchemeHttp, options.HostAddress, options.DescriptionPort).Uri;
            var securityPort = (await new UpnpSoapClient(plainClient, plainUri).QueryAsync(deviceInfo, "GetSecurityPort", [], timeout.Token))
                .GetInt32("NewSecurityPort")
                ?? throw new XmlException("The FRITZ!Box did not report its HTTPS port.");
            if (securityPort is < 1 or > IPEndPoint.MaxPort)
            {
                throw new InvalidDataException($"The FRITZ!Box reported the HTTPS port {securityPort}, which is out of range.");
            }

            return (description, new UriBuilder(Uri.UriSchemeHttps, options.HostAddress, securityPort).Uri);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The FRITZ!Box did not answer the TR-064 discovery in time.", exception);
        }
    }
}
