using System.Text;
using System.Xml;

namespace Namotion.Devices.Upnp;

/// <summary>
/// Posts UPnP actions to the control URLs of one device and reads their answers.
/// </summary>
/// <remarks>
/// The size of an answer is bounded by <see cref="HttpClient.MaxResponseContentBufferSize"/> of the borrowed client,
/// which the caller must set to what its device can reasonably answer.
/// </remarks>
public sealed class UpnpSoapClient
{
    private const string EnvelopeStart =
        "<s:Envelope s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\" xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>";

    private const string EnvelopeEnd = "</s:Body></s:Envelope>";

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    /// <param name="httpClient">The client for all requests, borrowed and not disposed.</param>
    /// <param name="baseUri">The base URI the control paths are resolved against.</param>
    public UpnpSoapClient(HttpClient httpClient, Uri baseUri)
    {
        _httpClient = httpClient;
        _baseUri = baseUri;
    }

    /// <summary>
    /// Invokes an action without parsing its answer. Throws <see cref="UpnpFaultException"/> when the device refuses
    /// it with a UPnP fault and <see cref="HttpRequestException"/> for any other failed request.
    /// </summary>
    public Task InvokeAsync(
        UpnpService service, string action, ReadOnlySpan<(string Name, string Value)> arguments, CancellationToken cancellationToken) =>
        InvokeAsync(service, action, CreateEnvelope(service, action, arguments), cancellationToken);

    /// <summary>
    /// Invokes an action and returns the values of its answer. Throws <see cref="UpnpFaultException"/> and
    /// <see cref="HttpRequestException"/> as a command does, and <see cref="XmlException"/> for an answer that is not
    /// the SOAP answer to the action.
    /// </summary>
    public Task<UpnpSoapResponse> QueryAsync(
        UpnpService service, string action, ReadOnlySpan<(string Name, string Value)> arguments, CancellationToken cancellationToken) =>
        QueryAsync(service, action, CreateEnvelope(service, action, arguments), cancellationToken);

    internal static string CreateEnvelope(UpnpService service, string action, ReadOnlySpan<(string Name, string Value)> arguments)
    {
        var envelope = new StringBuilder(EnvelopeStart, 512);
        envelope.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(service.ServiceType).Append('"');
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

    // Quotes are escaped as well, although XML does not require it in text: a value can be a document itself.
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

    private async Task InvokeAsync(UpnpService service, string action, string envelope, CancellationToken cancellationToken)
    {
        // The answer of a command carries nothing to read, so only its status counts.
        using var response = await PostAsync(service, action, envelope, cancellationToken);
    }

    private async Task<UpnpSoapResponse> QueryAsync(UpnpService service, string action, string envelope, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(service, action, envelope, cancellationToken);

        // Read from the bytes, so the encoding comes from the document and not from the charset of the header.
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var body = SoapBody.Read(content);
        if (body.Fault is { } fault)
        {
            throw new UpnpFaultException(service.ServiceType, action, fault.ErrorCode, fault.Text);
        }

        return body.Values is { } values && IsAnswerTo(body.Name, action)
            ? new UpnpSoapResponse(values)
            : throw new XmlException($"The answer is not the SOAP answer to {action}.");
    }

    private static bool IsAnswerTo(string? name, string action)
    {
        const string suffix = "Response";
        return name is not null &&
               name.Length == action.Length + suffix.Length &&
               name.StartsWith(action, StringComparison.Ordinal) &&
               name.EndsWith(suffix, StringComparison.Ordinal);
    }

    /// <returns>The successful response, which the caller disposes.</returns>
    private async Task<HttpResponseMessage> PostAsync(UpnpService service, string action, string envelope, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, service.ControlPath));
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(envelope));

        // Set without validation to keep the charset quoted, the form devices are known to accept.
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service.ServiceType}#{action}\"");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await CreateFailureAsync(service, action, response, cancellationToken);
        }
    }

    // A device answers a refused action with HTTP 500 and a fault; anything else is a failed request.
    private static async Task<Exception> CreateFailureAsync(
        UpnpService service, string action, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        SoapFault? fault = null;
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            fault = SoapBody.Read(content).Fault;
        }
        catch (XmlException)
        {
            // Empty or not XML, so not a fault.
        }

        return fault is null
            ? new HttpRequestException($"The device answered {action} with HTTP {(int)response.StatusCode}.", null, response.StatusCode)
            : new UpnpFaultException(service.ServiceType, action, fault.ErrorCode, fault.Text);
    }
}
