using System.Globalization;
using System.Text;
using System.Xml;

namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// Posts UPnP actions to the control URLs of one Sonos unit and reads their answers.
/// </summary>
internal sealed class SonosSoapClient
{
    private const string EnvelopeStart =
        "<s:Envelope s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\" xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>";

    private const string EnvelopeEnd = "</s:Body></s:Envelope>";

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
    /// Invokes an action without parsing its answer. Throws <see cref="SonosFaultException"/> when the speaker
    /// refuses it with a UPnP fault and <see cref="HttpRequestException"/> for any other failed request.
    /// </summary>
    internal Task InvokeAsync(
        SonosService service, string action, ReadOnlySpan<(string Name, string Value)> arguments, CancellationToken cancellationToken) =>
        InvokeAsync(service, action, CreateEnvelope(service, action, arguments), cancellationToken);

    /// <summary>
    /// Invokes an action and returns the values of its answer. Throws <see cref="SonosFaultException"/> and
    /// <see cref="HttpRequestException"/> as a command does, and <see cref="XmlException"/> for an answer that is not
    /// the SOAP answer to the action.
    /// </summary>
    internal Task<SonosSoapResponse> QueryAsync(
        SonosService service, string action, ReadOnlySpan<(string Name, string Value)> arguments, CancellationToken cancellationToken) =>
        QueryAsync(service, action, CreateEnvelope(service, action, arguments), cancellationToken);

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

    private async Task InvokeAsync(SonosService service, string action, string envelope, CancellationToken cancellationToken)
    {
        // The answer of a command carries nothing to read, so only its status counts.
        using var response = await PostAsync(service, action, envelope, cancellationToken);
    }

    private async Task<SonosSoapResponse> QueryAsync(SonosService service, string action, string envelope, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(service, action, envelope, cancellationToken);

        // Read from the bytes, so the encoding comes from the document and not from the charset of the header.
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var body = SoapBody.Read(content);
        if (body.Fault is { } fault)
        {
            throw new SonosFaultException(service, action, fault.ErrorCode, fault.Text);
        }

        return body.Values is { } values && IsAnswerTo(body.Name, action)
            ? new SonosSoapResponse(values)
            : throw new XmlException($"The answer of the Sonos speaker is not the SOAP answer to {action}.");
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
    private async Task<HttpResponseMessage> PostAsync(SonosService service, string action, string envelope, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, service.ControlPath));
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(envelope));

        // Set without validation to keep the charset quoted, the form the speakers are known to accept.
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
        request.Headers.TryAddWithoutValidation("soapaction", $"{service.Namespace}#{action}");

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

    // A speaker answers a refused action with HTTP 500 and a fault; anything else, such as the bare HTTP 500 of a
    // satellite, is a failed request.
    private static async Task<Exception> CreateFailureAsync(
        SonosService service, string action, HttpResponseMessage response, CancellationToken cancellationToken)
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
            ? new HttpRequestException($"The Sonos speaker answered {action} with HTTP {(int)response.StatusCode}.", null, response.StatusCode)
            : new SonosFaultException(service, action, fault.ErrorCode, fault.Text);
    }
}

/// <summary>
/// The answer of an action: the values the speaker returned, by name.
/// </summary>
internal sealed class SonosSoapResponse
{
    private readonly List<(string Name, string? Value)> _values;

    internal SonosSoapResponse(List<(string Name, string? Value)> values)
    {
        _values = values;
    }

    /// <returns>The value, empty for an empty element and null when the speaker did not return it as text.</returns>
    internal string? GetString(string name)
    {
        foreach (var (valueName, value) in _values)
        {
            if (valueName == name)
            {
                return value;
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
