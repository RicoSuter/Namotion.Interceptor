using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// Posts UPnP actions to the control URLs of one Sonos unit and reads their answers.
/// </summary>
internal sealed class SonosSoapClient
{
    private const string EnvelopeStart =
        "<s:Envelope s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\" xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>";

    private const string EnvelopeEnd = "</s:Body></s:Envelope>";

    private static readonly XNamespace EnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace ControlNamespace = "urn:schemas-upnp-org:control-1-0";

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    /// <param name="httpClient">The client for all requests, borrowed and not disposed.</param>
    /// <param name="baseUri">The base URI of the unit.</param>
    internal SonosSoapClient(HttpClient httpClient, Uri baseUri)
    {
        _httpClient = httpClient;
        _baseUri = baseUri;
    }

    /// <summary>
    /// Invokes an action. Throws <see cref="SonosFaultException"/> when the speaker answers with a UPnP fault,
    /// <see cref="HttpRequestException"/> for any other failed request and <see cref="XmlException"/> for an answer
    /// that is not a SOAP envelope.
    /// </summary>
    internal Task<SonosSoapResponse> InvokeAsync(
        SonosService service, string action, ReadOnlySpan<(string Name, string Value)> arguments, CancellationToken cancellationToken) =>
        SendAsync(service, action, CreateEnvelope(service, action, arguments), cancellationToken);

    internal static string CreateEnvelope(SonosService service, string action, ReadOnlySpan<(string Name, string Value)> arguments)
    {
        var envelope = new StringBuilder(EnvelopeStart, 512);
        envelope.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(service.Namespace).Append('"');
        if (arguments.IsEmpty)
        {
            envelope.Append(" />");
        }
        else
        {
            envelope.Append('>');
            foreach (var (name, value) in arguments)
            {
                if (value.Length == 0)
                {
                    envelope.Append('<').Append(name).Append(" />");
                }
                else
                {
                    envelope.Append('<').Append(name).Append('>');
                    AppendEscaped(envelope, value);
                    envelope.Append("</").Append(name).Append('>');
                }
            }

            envelope.Append("</u:").Append(action).Append('>');
        }

        return envelope.Append(EnvelopeEnd).ToString();
    }

    // Quotes are escaped as well, although XML does not require it in text: metadata is a document inside a value,
    // and the speakers are known to accept it in this form.
    private static void AppendEscaped(StringBuilder envelope, string value)
    {
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    envelope.Append("&amp;");
                    break;
                case '<':
                    envelope.Append("&lt;");
                    break;
                case '>':
                    envelope.Append("&gt;");
                    break;
                case '"':
                    envelope.Append("&quot;");
                    break;
                default:
                    envelope.Append(character);
                    break;
            }
        }
    }

    private async Task<SonosSoapResponse> SendAsync(SonosService service, string action, string envelope, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, service.ControlPath));
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(envelope));

        // Set without validation to keep the charset quoted, the form the speakers are known to accept.
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
        request.Headers.TryAddWithoutValidation("soapaction", $"{service.Namespace}#{action}");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateFailure(action, response, content);
        }

        var result = SonosXml.Parse(content).Root?.Element(EnvelopeNamespace + "Body")?.Elements().FirstOrDefault()
            ?? throw new XmlException($"The answer to {action} is not a SOAP envelope with a body.");
        return new SonosSoapResponse(result);
    }

    // A speaker answers a refused action with HTTP 500 and a fault; anything else, such as the bare HTTP 500 of a
    // satellite, is a failed request.
    private static Exception CreateFailure(string action, HttpResponseMessage response, string content)
    {
        XElement? fault = null;
        try
        {
            fault = content.Length == 0
                ? null
                : SonosXml.Parse(content).Root?.Element(EnvelopeNamespace + "Body")?.Element(EnvelopeNamespace + "Fault");
        }
        catch (XmlException)
        {
            // Not XML, so not a fault.
        }

        if (fault is null)
        {
            return new HttpRequestException(
                $"The Sonos speaker answered {action} with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var errorCode = fault.Descendants(ControlNamespace + "errorCode").FirstOrDefault()?.Value;
        return new SonosFaultException(
            action,
            int.TryParse(errorCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null,
            fault.Element("faultstring")?.Value);
    }
}

/// <summary>
/// The answer of an action: the values the speaker returned, by name.
/// </summary>
internal sealed class SonosSoapResponse
{
    private readonly XElement _element;

    internal SonosSoapResponse(XElement element)
    {
        _element = element;
    }

    /// <returns>The value, empty for an empty element and null when the speaker did not return it.</returns>
    internal string? GetString(string name)
    {
        foreach (var element in _element.Elements())
        {
            if (element.Name.LocalName == name)
            {
                return element.Value;
            }
        }

        return null;
    }

    /// <returns>The value, or null when the speaker did not return it or it is no whole number.</returns>
    internal int? GetInt32(string name) =>
        int.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <returns>The value, or null when the speaker did not return it or it is neither form of a UPnP boolean.</returns>
    internal bool? GetBoolean(string name) => GetString(name)?.Trim() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null
    };
}
