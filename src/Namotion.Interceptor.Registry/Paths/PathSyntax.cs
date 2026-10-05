using System.Globalization;
using System.Text;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// The writer's side of the grammar <see cref="PathSegmentReader"/> reads, and the reader's error text.
/// </summary>
internal static class PathSyntax
{
    /// <summary>
    /// Describes an error <see cref="PathSegmentReader"/> reported at <paramref name="position"/>.
    /// </summary>
    public static string FormatError(PathSyntaxError error, int position, PathCharacters characters, string path)
    {
        var reason = error switch
        {
            PathSyntaxError.MissingName => "Missing segment name",
            PathSyntaxError.UnclosedIndex => $"Unclosed '{characters.IndexOpen}'",
            PathSyntaxError.EmptyIndex => "Empty index",
            PathSyntaxError.ExpectedSeparator => $"Expected '{characters.Separator}' or end of path after index",
            _ => throw new ArgumentOutOfRangeException(nameof(error), error, null)
        };

        return string.Create(CultureInfo.InvariantCulture, $"{reason} at position {position} in path '{path}'");
    }

    /// <summary>
    /// The key's text: a string as is, an <see cref="IFormattable"/> with the invariant culture, anything else
    /// with <see cref="object.ToString"/>.
    /// </summary>
    public static string FormatIndex(object index) => index switch
    {
        string value => value,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => index.ToString() ?? string.Empty
    };

    /// <summary>
    /// Whether <paramref name="key"/>'s text equals <paramref name="text"/>, formatting on the stack where possible.
    /// </summary>
    public static bool KeyTextEquals(object key, ReadOnlySpan<char> text)
    {
        if (key is string value)
        {
            return text.SequenceEqual(value);
        }

        if (key is ISpanFormattable formattable)
        {
            Span<char> buffer = stackalloc char[64];
            if (formattable.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture))
            {
                return text.SequenceEqual(buffer[..written]);
            }
        }

        return text.SequenceEqual(FormatIndex(key));
    }

    /// <summary>
    /// Appends <paramref name="index"/> in brackets. False when its text is empty or contains the closing
    /// bracket, so the key has no path.
    /// </summary>
    public static bool TryAppendIndex(StringBuilder builder, object index, char indexOpen, char indexClose)
    {
        if (index is ISpanFormattable formattable)
        {
            // Formatted on the stack to avoid a string per segment; must produce the same text as FormatIndex,
            // which a longer key falls back to.
            Span<char> buffer = stackalloc char[64];
            if (formattable.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture))
            {
                return TryAppendIndexText(builder, buffer[..written], indexOpen, indexClose);
            }
        }

        return TryAppendIndexText(builder, FormatIndex(index), indexOpen, indexClose);
    }

    private static bool TryAppendIndexText(StringBuilder builder, ReadOnlySpan<char> text, char indexOpen, char indexClose)
    {
        if (text.IsEmpty || text.Contains(indexClose))
        {
            return false;
        }

        builder.Append(indexOpen).Append(text).Append(indexClose);
        return true;
    }
}

/// <summary>
/// Why <see cref="PathSegmentReader"/> rejected a segment.
/// </summary>
internal enum PathSyntaxError
{
    None,
    MissingName,
    UnclosedIndex,
    EmptyIndex,
    ExpectedSeparator
}

/// <summary>
/// A provider's path characters, read once per path instead of once per segment.
/// </summary>
internal readonly record struct PathCharacters(char Separator, char IndexOpen, char IndexClose);
