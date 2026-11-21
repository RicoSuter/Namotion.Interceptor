using System.Xml;

namespace Namotion.Devices.FritzBox.Parsing;

/// <summary>
/// Reads the filter profiles (access profiles) that <c>GetFilterProfiles</c> of the host filter service returns.
/// </summary>
internal static class FilterProfileListParser
{
    // Fragment: the profiles may arrive as sibling elements without a common root.
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        ConformanceLevel = ConformanceLevel.Fragment,
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreWhitespace = true,
        XmlResolver = null
    };

    /// <summary>
    /// Reads the name of every <c>FilterProfile</c> element by its <c>FilterProfileID</c>, at any depth; a profile
    /// without an ID or a name is skipped. Throws <see cref="XmlException"/> when the list is malformed or declares a
    /// document type.
    /// </summary>
    internal static Dictionary<string, string> Parse(string xml)
    {
        var profiles = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, ReaderSettings);
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "FilterProfile")
            {
                var (id, name) = ReadProfile(reader);
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    profiles[id.Trim()] = name.Trim();
                }
            }
            else
            {
                reader.Read();
            }
        }

        return profiles;
    }

    /// <summary>
    /// Reads the ID and name of the profile the reader is on and leaves the reader after its end. The time table and
    /// the other fields are skipped without allocating strings for their values.
    /// </summary>
    private static (string? Id, string? Name) ReadProfile(XmlReader reader)
    {
        string? id = null;
        string? name = null;
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return (id, name);
        }

        var profileDepth = reader.Depth;
        reader.Read();
        while (reader.Depth > profileDepth)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != profileDepth + 1)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "FilterProfileID":
                    id = reader.ReadElementContentAsString();
                    break;
                case "Name":
                    name = reader.ReadElementContentAsString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        // The end element of the profile.
        reader.Read();
        return (id, name);
    }
}
