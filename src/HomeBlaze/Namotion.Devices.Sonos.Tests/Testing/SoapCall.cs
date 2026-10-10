using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Tests.Testing;

/// <summary>
/// One SOAP request: the control URL path it was posted to, the service and action from SOAPACTION, and the raw body.
/// </summary>
internal sealed record SoapCall(string Path, string Service, string Action, string Body)
{
    /// <summary>
    /// Returns the value of the named argument, unescaped once as the speaker reads it, or null when it is absent.
    /// </summary>
    internal string? GetArgument(string name) =>
        XDocument.Parse(Body).Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
}
