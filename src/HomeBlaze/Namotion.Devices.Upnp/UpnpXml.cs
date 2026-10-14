using System.Xml;
using System.Xml.Linq;

namespace Namotion.Devices.Upnp;

/// <summary>
/// Reads XML received from the network: a document type declaration is refused instead of expanded, and nothing is
/// resolved.
/// </summary>
public static class UpnpXml
{
    // Matches XDocument.Parse except that a document type declaration is refused instead of expanded.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreWhitespace = true,
        XmlResolver = null
    };

    // Like ReaderSettings, but keeps whitespace-only text: discarding it would turn a value that is only spaces
    // into an empty string instead of the value a device sent. XmlReader.MoveToContent still skips whitespace
    // between elements regardless of this setting, so navigation between elements is unaffected.
    private static readonly XmlReaderSettings ValueReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreWhitespace = false,
        XmlResolver = null
    };

    /// <summary>
    /// How many elements may nest in a document <see cref="Parse"/> loads, the root included.
    /// </summary>
    public const int MaximumLevels = 64;

    /// <summary>
    /// Parses a document. Throws <see cref="XmlException"/> when it is malformed, declares a document type or nests
    /// its elements deeper than <see cref="MaximumLevels"/>.
    /// </summary>
    public static XDocument Parse(string xml)
    {
        using var stringReader = new StringReader(xml);
        using var xmlReader = new DepthLimitedXmlReader(XmlReader.Create(stringReader, ReaderSettings), MaximumLevels);
        return XDocument.Load(xmlReader);
    }

    /// <summary>
    /// Creates a reader over the bytes of a document, which takes the encoding from the document and throws
    /// <see cref="XmlException"/> while reading when it is malformed, declares a document type, or nests elements
    /// deeper than <see cref="MaximumLevels"/>.
    /// </summary>
    public static XmlReader CreateReader(Stream stream) => new DepthLimitedXmlReader(XmlReader.Create(stream, ReaderSettings), MaximumLevels);

    /// <summary>
    /// Creates a reader over the bytes of a document like <see cref="CreateReader"/>, except that it reports a
    /// whitespace-only value instead of discarding it.
    /// </summary>
    internal static XmlReader CreateValueReader(Stream stream) => XmlReader.Create(stream, ValueReaderSettings);
}
