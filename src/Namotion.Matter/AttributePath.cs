using System.Globalization;

namespace Namotion.Matter;

/// <summary>Identifies an attribute of a node by endpoint number, cluster ID and attribute ID.</summary>
/// <param name="Endpoint">The endpoint number.</param>
/// <param name="Cluster">The cluster ID.</param>
/// <param name="Attribute">The attribute ID.</param>
public readonly record struct AttributePath(ushort Endpoint, uint Cluster, uint Attribute)
{
    /// <summary>Parses the <c>endpoint/cluster/attribute</c> form with decimal numbers.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The path.</returns>
    /// <exception cref="FormatException">The text is not three decimal numbers separated by slashes.</exception>
    public static AttributePath Parse(ReadOnlySpan<char> text)
    {
        if (!TryParse(text, out var path))
        {
            throw new FormatException($"'{text}' is not an attribute path of the form endpoint/cluster/attribute.");
        }

        return path;
    }

    /// <summary>Parses the <c>endpoint/cluster/attribute</c> form with decimal numbers.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="path">The path, or the default value when parsing fails.</param>
    /// <returns>Whether the text is a valid path.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out AttributePath path)
    {
        path = default;
        var firstSlash = text.IndexOf('/');
        if (firstSlash < 0)
        {
            return false;
        }

        var rest = text[(firstSlash + 1)..];
        var secondSlash = rest.IndexOf('/');
        if (secondSlash < 0)
        {
            return false;
        }

        if (!ushort.TryParse(text[..firstSlash], NumberStyles.None, CultureInfo.InvariantCulture, out var endpoint) ||
            !uint.TryParse(rest[..secondSlash], NumberStyles.None, CultureInfo.InvariantCulture, out var cluster) ||
            !uint.TryParse(rest[(secondSlash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var attribute))
        {
            return false;
        }

        path = new AttributePath(endpoint, cluster, attribute);
        return true;
    }

    /// <summary>Formats the path as <c>endpoint/cluster/attribute</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Endpoint}/{Cluster}/{Attribute}");
}
