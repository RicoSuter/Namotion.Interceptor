using System.Buffers.Text;
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
    internal static AttributePath Parse(ReadOnlySpan<char> text)
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
    internal static bool TryParse(ReadOnlySpan<char> text, out AttributePath path)
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

    /// <summary>Parses the <c>endpoint/cluster/attribute</c> form with decimal numbers from UTF-8 text.</summary>
    /// <param name="utf8Text">The UTF-8 text to parse.</param>
    /// <param name="path">The path, or the default value when parsing fails.</param>
    /// <returns>Whether the text is a valid path.</returns>
    internal static bool TryParse(ReadOnlySpan<byte> utf8Text, out AttributePath path)
    {
        path = default;
        var firstSlash = utf8Text.IndexOf((byte)'/');
        if (firstSlash < 0)
        {
            return false;
        }

        var rest = utf8Text[(firstSlash + 1)..];
        var secondSlash = rest.IndexOf((byte)'/');
        if (secondSlash < 0)
        {
            return false;
        }

        if (!TryParseDigits(utf8Text[..firstSlash], out ushort endpoint) ||
            !TryParseDigits(rest[..secondSlash], out uint cluster) ||
            !TryParseDigits(rest[(secondSlash + 1)..], out uint attribute))
        {
            return false;
        }

        path = new AttributePath(endpoint, cluster, attribute);
        return true;
    }

    /// <summary>Formats the path as <c>endpoint/cluster/attribute</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Endpoint}/{Cluster}/{Attribute}");

    // Digits only, as NumberStyles.None: Utf8Parser stops at the first other byte, which then remains unconsumed.
    private static bool TryParseDigits(ReadOnlySpan<byte> utf8Text, out ushort value) =>
        Utf8Parser.TryParse(utf8Text, out value, out var consumed) && consumed == utf8Text.Length && IsDigit(utf8Text[0]);

    private static bool TryParseDigits(ReadOnlySpan<byte> utf8Text, out uint value) =>
        Utf8Parser.TryParse(utf8Text, out value, out var consumed) && consumed == utf8Text.Length && IsDigit(utf8Text[0]);

    private static bool IsDigit(byte character) => character is >= (byte)'0' and <= (byte)'9';
}
