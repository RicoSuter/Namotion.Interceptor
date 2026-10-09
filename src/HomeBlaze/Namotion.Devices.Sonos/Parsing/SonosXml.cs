using System.Xml;
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// Parses XML received from the network.
/// </summary>
internal static class SonosXml
{
    // Matches XDocument.Parse except that a document type declaration is refused instead of expanded.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreWhitespace = true,
        XmlResolver = null
    };

    /// <summary>
    /// Parses a document. Throws <see cref="XmlException"/> when it is malformed or declares a document type.
    /// </summary>
    internal static XDocument Parse(string xml)
    {
        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, ReaderSettings);
        return XDocument.Load(xmlReader);
    }
}
