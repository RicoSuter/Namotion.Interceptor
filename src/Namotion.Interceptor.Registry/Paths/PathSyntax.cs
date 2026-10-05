using System.Globalization;
using System.Text;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Key text and index writing for the grammar <see cref="PathSegmentReader"/> reads, and the reader's error text.
/// </summary>
internal static class PathSyntax
{
    /// <summary>The quote that wraps a key needing it; not configurable.</summary>
    public const char Quote = '\'';

    public static string FormatError(string path, int position, PathSyntaxError error, PathCharacters characters)
    {
        var reason = error switch
        {
            PathSyntaxError.MissingName => "Missing segment name",
            PathSyntaxError.UnclosedIndex => $"Unclosed '{characters.IndexOpen}'",
            PathSyntaxError.EmptyIndex => "Empty index",
            PathSyntaxError.UnclosedQuote => "Unclosed quote",
            PathSyntaxError.ExpectedIndexClose => $"Expected '{characters.IndexClose}' after quoted key",
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

    /// <summary>Appends <paramref name="index"/> in brackets, quoted when its text needs it.</summary>
    public static void AppendIndex(StringBuilder builder, object index, PathCharacters characters)
    {
        if (index is ISpanFormattable formattable)
        {
            // Must produce the same text as FormatIndex, which a longer key falls back to.
            Span<char> buffer = stackalloc char[64];
            if (formattable.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture))
            {
                AppendIndexText(builder, buffer[..written], characters);
                return;
            }
        }

        AppendIndexText(builder, FormatIndex(index), characters);
    }

    /// <summary>Appends key <paramref name="text"/> in brackets, quoted when it is empty, starts with a quote or contains the closing bracket.</summary>
    public static void AppendIndexText(StringBuilder builder, ReadOnlySpan<char> text, PathCharacters characters)
    {
        builder.Append(characters.IndexOpen);
        if (text.IsEmpty || text[0] == Quote || text.Contains(characters.IndexClose))
        {
            builder.Append(Quote);
            foreach (var character in text)
            {
                builder.Append(character);
                if (character == Quote)
                {
                    builder.Append(Quote);
                }
            }

            builder.Append(Quote);
        }
        else
        {
            builder.Append(text);
        }

        builder.Append(characters.IndexClose);
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
    UnclosedQuote,
    ExpectedIndexClose,
    ExpectedSeparator
}

/// <summary>
/// A provider's path characters, read once per path instead of once per segment.
/// </summary>
internal readonly record struct PathCharacters(char Separator, char IndexOpen, char IndexClose);
