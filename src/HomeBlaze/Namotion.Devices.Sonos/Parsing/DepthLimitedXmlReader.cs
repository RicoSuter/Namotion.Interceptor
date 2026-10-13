using System.Xml;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// Passes a reader through and throws <see cref="XmlException"/> at the first element that nests deeper than a
/// number of levels. Owns the reader and disposes it.
/// </summary>
/// <remarks>
/// A reader sets no limit of its own, and loading a document takes time quadratic in its depth, while reading the
/// text of an element recurses once per level. A limit on the depth bounds both.
/// </remarks>
internal sealed class DepthLimitedXmlReader : XmlReader
{
    private readonly XmlReader _reader;
    private readonly int _maximumLevels;

    /// <param name="reader">The reader to pass through.</param>
    /// <param name="maximumLevels">How many elements may nest, the root included.</param>
    internal DepthLimitedXmlReader(XmlReader reader, int maximumLevels)
    {
        _reader = reader;
        _maximumLevels = maximumLevels;
    }

    public override bool Read()
    {
        if (!_reader.Read())
        {
            return false;
        }

        // The depth of the root element is zero.
        if (_reader.NodeType == XmlNodeType.Element && _reader.Depth >= _maximumLevels)
        {
            throw new XmlException($"The document nests elements deeper than {_maximumLevels} levels.");
        }

        return true;
    }

    public override int AttributeCount => _reader.AttributeCount;

    public override string BaseURI => _reader.BaseURI;

    public override int Depth => _reader.Depth;

    public override bool EOF => _reader.EOF;

    public override bool IsEmptyElement => _reader.IsEmptyElement;

    public override string LocalName => _reader.LocalName;

    public override string NamespaceURI => _reader.NamespaceURI;

    public override XmlNameTable NameTable => _reader.NameTable;

    public override XmlNodeType NodeType => _reader.NodeType;

    public override string Prefix => _reader.Prefix;

    public override ReadState ReadState => _reader.ReadState;

    public override string Value => _reader.Value;

    public override string GetAttribute(int i) => _reader.GetAttribute(i);

    public override string? GetAttribute(string name) => _reader.GetAttribute(name);

    public override string? GetAttribute(string name, string? namespaceURI) => _reader.GetAttribute(name, namespaceURI);

    public override string? LookupNamespace(string prefix) => _reader.LookupNamespace(prefix);

    public override bool MoveToAttribute(string name) => _reader.MoveToAttribute(name);

    public override bool MoveToAttribute(string name, string? ns) => _reader.MoveToAttribute(name, ns);

    public override bool MoveToElement() => _reader.MoveToElement();

    public override bool MoveToFirstAttribute() => _reader.MoveToFirstAttribute();

    public override bool MoveToNextAttribute() => _reader.MoveToNextAttribute();

    public override bool ReadAttributeValue() => _reader.ReadAttributeValue();

    public override void ResolveEntity() => _reader.ResolveEntity();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reader.Dispose();
        }

        base.Dispose(disposing);
    }
}
