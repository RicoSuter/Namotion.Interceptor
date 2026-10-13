using System.Globalization;
using System.Text;
using System.Xml;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos.Client;

/// <summary>
/// The first element in the body of a SOAP envelope: the answer of an action with its values, or a fault.
/// </summary>
/// <param name="Name">The local name of the element, null when the document is no envelope with an element in its body.</param>
/// <param name="Values">The values of an answer in document order, null for a fault or no element.</param>
/// <param name="Fault">The fault, null for an answer or no element.</param>
internal sealed record SoapBody(string? Name, List<(string Name, string? Value)>? Values, SoapFault? Fault)
{
    private const string EnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string ControlNamespace = "urn:schemas-upnp-org:control-1-0";

    private static readonly SoapBody None = new(null, null, null);

    /// <summary>
    /// Reads the body of an envelope. Throws <see cref="XmlException"/> when the document is empty, malformed or
    /// declares a document type.
    /// </summary>
    /// <remarks>
    /// Reads forward with a reader instead of building a document: the time stays linear and the stack flat however
    /// deeply a hostile document nests.
    /// </remarks>
    internal static SoapBody Read(Stream stream)
    {
        using var reader = SonosXml.CreateReader(stream);
        if (reader.MoveToContent() != XmlNodeType.Element || !IsEnvelopeElement(reader, "Envelope") || !MoveToFirstChild(reader))
        {
            return None;
        }

        while (!IsEnvelopeElement(reader, "Body"))
        {
            reader.Skip();
            if (reader.MoveToContent() != XmlNodeType.Element)
            {
                return None;
            }
        }

        if (!MoveToFirstChild(reader))
        {
            return None;
        }

        var name = reader.LocalName;
        return IsEnvelopeElement(reader, "Fault")
            ? new SoapBody(name, null, ReadFault(reader))
            : new SoapBody(name, ReadValues(reader), null);
    }

    private static bool IsEnvelopeElement(XmlReader reader, string localName) =>
        reader.LocalName == localName && reader.NamespaceURI == EnvelopeNamespace;

    /// <summary>
    /// Moves from a start element to its first child. Returns false, with the reader past the start element, when it
    /// has no content or its content does not start with an element.
    /// </summary>
    private static bool MoveToFirstChild(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return false;
        }

        reader.Read();
        return reader.MoveToContent() == XmlNodeType.Element;
    }

    private static List<(string Name, string? Value)> ReadValues(XmlReader reader)
    {
        var values = new List<(string Name, string? Value)>();
        if (MoveToFirstChild(reader))
        {
            while (reader.NodeType == XmlNodeType.Element)
            {
                var name = reader.LocalName;
                values.Add((name, ReadText(reader)));
                reader.MoveToContent();
            }
        }

        return values;
    }

    private static SoapFault ReadFault(XmlReader reader)
    {
        int? errorCode = null;
        string? text = null;
        if (reader.IsEmptyElement)
        {
            return new SoapFault(errorCode, text);
        }

        var faultDepth = reader.Depth;
        reader.Read();
        while (reader.Depth > faultDepth)
        {
            if (IsElement(reader, "faultstring") && reader.Depth == faultDepth + 1)
            {
                text = ReadText(reader);
            }
            else if (IsElement(reader, "errorCode") && reader.NamespaceURI == ControlNamespace)
            {
                // Normally at detail/UPnPError/errorCode, but taken at any depth.
                errorCode ??= int.TryParse(ReadText(reader), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
            }
            else
            {
                reader.Read();
            }
        }

        return new SoapFault(errorCode, text);
    }

    private static bool IsElement(XmlReader reader, string localName) =>
        reader.NodeType == XmlNodeType.Element && reader.LocalName == localName;

    /// <summary>
    /// Reads the text of the element the reader is on and moves past its end. Returns null when the element holds
    /// elements, which are skipped.
    /// </summary>
    private static string? ReadText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return string.Empty;
        }

        var depth = reader.Depth;
        var holdsElements = false;
        string? text = null;
        StringBuilder? longText = null;
        reader.Read();
        while (reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                holdsElements = true;
                reader.Skip();
                continue;
            }

            if (IsText(reader.NodeType))
            {
                Append(reader.Value, ref text, ref longText);
            }

            reader.Read();
        }

        reader.Read();
        return holdsElements ? null : longText?.ToString() ?? text ?? string.Empty;
    }

    private static bool IsText(XmlNodeType nodeType) =>
        nodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace;

    // One node is the rule, so its string is kept as is; a comment or a CDATA section splits a value into several.
    private static void Append(string part, ref string? text, ref StringBuilder? longText)
    {
        if (text is null)
        {
            text = part;
        }
        else
        {
            (longText ??= new StringBuilder(text)).Append(part);
        }
    }
}

/// <summary>
/// A UPnP fault: the error code, null when it carried none, and the fault text.
/// </summary>
internal sealed record SoapFault(int? ErrorCode, string? Text);
