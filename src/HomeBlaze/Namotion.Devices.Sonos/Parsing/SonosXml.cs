using System.Xml;
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// Parses XML received from the network and holds the namespaces of the Sonos XML documents.
/// </summary>
internal static class SonosXml
{
    internal static readonly XNamespace DidlNamespace = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    internal static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
    internal static readonly XNamespace UpnpNamespace = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    internal static readonly XNamespace RinconNamespace = "urn:schemas-rinconnetworks-com:metadata-1-0/";

    // Matches XDocument.Parse except that a document type declaration is refused instead of expanded.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreWhitespace = true,
        XmlResolver = null
    };

    /// <summary>
    /// How many elements may nest in a document, the root included. The documents of a speaker nest fewer than ten.
    /// </summary>
    internal const int MaximumLevels = 64;

    /// <summary>
    /// Parses a document. Throws <see cref="XmlException"/> when it is malformed, declares a document type or nests
    /// its elements deeper than <see cref="MaximumLevels"/>.
    /// </summary>
    internal static XDocument Parse(string xml)
    {
        using var stringReader = new StringReader(xml);
        using var xmlReader = new DepthLimitedXmlReader(XmlReader.Create(stringReader, ReaderSettings), MaximumLevels);
        return XDocument.Load(xmlReader);
    }

    /// <summary>
    /// Creates a reader over the bytes of a document, which takes the encoding from the document and throws
    /// <see cref="XmlException"/> while reading when it is malformed or declares a document type.
    /// </summary>
    internal static XmlReader CreateReader(Stream stream) => XmlReader.Create(stream, ReaderSettings);
}
