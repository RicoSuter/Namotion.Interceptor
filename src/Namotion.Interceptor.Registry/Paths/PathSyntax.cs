using System.Globalization;
using System.Text;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// The path grammar shared by the reader and the writer: segments joined by the separator, each with an
/// optional index in brackets. Inside an index every character is literal except the closing bracket,
/// which is written twice.
/// </summary>
internal static class PathSyntax
{
    public static void ValidateCharacters(PathProviderBase pathProvider)
    {
        var separator = pathProvider.PathSeparator;
        var indexOpen = pathProvider.IndexOpen;
        var indexClose = pathProvider.IndexClose;
        if (separator == indexOpen || separator == indexClose || indexOpen == indexClose)
        {
            throw new InvalidOperationException(
                $"Path provider '{pathProvider.GetType().Name}' must use distinct characters for the separator " +
                $"('{separator}'), index open ('{indexOpen}') and index close ('{indexClose}').");
        }
    }

    /// <summary>
    /// Moves <paramref name="position"/> past separators, which skips empty segments.
    /// </summary>
    /// <returns>True when a segment starts at <paramref name="position"/>, false at the end of the path.</returns>
    public static bool SkipToSegment(PathCharacters characters, string path, ref int position)
    {
        while (position < path.Length && path[position] == characters.Separator)
        {
            position++;
        }

        return position < path.Length;
    }

    /// <summary>
    /// Reads the segment starting at <paramref name="position"/>, where <see cref="SkipToSegment"/> left it. The
    /// segment points into <paramref name="path"/>, so reading one allocates nothing.
    /// </summary>
    /// <returns>
    /// True with <paramref name="position"/> just past the segment, or false with <paramref name="position"/> at
    /// the malformed text, see <see cref="FormatError"/>.
    /// </returns>
    public static bool TryReadSegment(
        PathCharacters characters,
        string path,
        ref int position,
        out PathSegment segment,
        out PathSyntaxError error)
    {
        // Outside an index the closing bracket is an ordinary name character.
        var nameStart = position;
        var nameLength = path.AsSpan(nameStart).IndexOfAny(characters.Separator, characters.IndexOpen);
        position = nameLength < 0 ? path.Length : nameStart + nameLength;
        if (position == nameStart)
        {
            return Fail(PathSyntaxError.MissingName, out segment, out error);
        }

        if (position == path.Length || path[position] != characters.IndexOpen)
        {
            segment = new PathSegment(nameStart, -1, position);
            error = PathSyntaxError.None;
            return true;
        }

        var openPosition = position;
        if (!TrySkipIndex(path, ref position, characters.IndexClose))
        {
            position = openPosition;
            return Fail(PathSyntaxError.UnclosedIndex, out segment, out error);
        }

        if (position == openPosition + 2)
        {
            position = openPosition;
            return Fail(PathSyntaxError.EmptyIndex, out segment, out error);
        }

        if (position < path.Length && path[position] != characters.Separator)
        {
            return Fail(PathSyntaxError.ExpectedSeparator, out segment, out error);
        }

        segment = new PathSegment(nameStart, openPosition + 1, position);
        error = PathSyntaxError.None;
        return true;
    }

    /// <summary>
    /// Checks that <paramref name="path"/> is well formed without allocating.
    /// </summary>
    public static bool IsWellFormed(PathCharacters characters, string path)
    {
        var position = 0;
        while (SkipToSegment(characters, path, ref position))
        {
            if (!TryReadSegment(characters, path, ref position, out _, out _))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Describes an error <see cref="TryReadSegment"/> reported at <paramref name="position"/>.
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
    /// Turns raw index text (as written in a path) into the key text it stands for.
    /// </summary>
    public static string UnescapeIndex(ReadOnlySpan<char> rawIndex, char indexClose)
    {
        // Raw index text holds the closing bracket only as a doubled pair, so the key text is never longer.
        if (rawIndex.IndexOf(indexClose) < 0)
        {
            return rawIndex.ToString();
        }

        var buffer = rawIndex.Length <= 256 ? stackalloc char[rawIndex.Length] : new char[rawIndex.Length];
        var written = 0;
        for (var i = 0; i < rawIndex.Length; i++)
        {
            buffer[written++] = rawIndex[i];
            if (rawIndex[i] == indexClose)
            {
                i++;
            }
        }

        return new string(buffer[..written]);
    }

    public static string? FormatIndex(object index)
    {
        var text = index switch
        {
            string value => value,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => index.ToString()
        };

        return string.IsNullOrEmpty(text) ? null : text;
    }

    public static bool TryAppendIndex(StringBuilder builder, object index, char indexOpen, char indexClose)
    {
        if (index is ISpanFormattable formattable)
        {
            // Formatted on the stack to avoid a string per segment; must produce the same text as FormatIndex,
            // which the reader matches keys against and which a longer key falls back to.
            Span<char> buffer = stackalloc char[64];
            if (formattable.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture))
            {
                if (written == 0)
                {
                    return false;
                }

                AppendQuoted(builder, buffer[..written], indexOpen, indexClose);
                return true;
            }
        }

        var text = FormatIndex(index);
        if (text is null)
        {
            return false;
        }

        AppendQuoted(builder, text, indexOpen, indexClose);
        return true;
    }

    private static void AppendQuoted(StringBuilder builder, ReadOnlySpan<char> text, char indexOpen, char indexClose)
    {
        builder.Append(indexOpen);
        if (text.IndexOf(indexClose) < 0)
        {
            builder.Append(text);
        }
        else
        {
            foreach (var character in text)
            {
                builder.Append(character);
                if (character == indexClose)
                {
                    builder.Append(indexClose);
                }
            }
        }

        builder.Append(indexClose);
    }

    /// <summary>
    /// Skips the index text after the opening bracket at <paramref name="position"/>. On success
    /// <paramref name="position"/> is just past the closing bracket.
    /// </summary>
    private static bool TrySkipIndex(string path, ref int position, char indexClose)
    {
        position++;
        while (position < path.Length)
        {
            if (path[position] != indexClose)
            {
                position++;
                continue;
            }

            if (position + 1 < path.Length && path[position + 1] == indexClose)
            {
                // A doubled closing bracket is a literal one.
                position += 2;
                continue;
            }

            position++;
            return true;
        }

        return false;
    }

    private static bool Fail(PathSyntaxError reason, out PathSegment segment, out PathSyntaxError error)
    {
        segment = default;
        error = reason;
        return false;
    }
}

/// <summary>
/// A path segment as ranges of the path it was read from, so neither its name nor its index needs a string until
/// a caller asks for one.
/// </summary>
/// <param name="NameStart">Start of the segment name in the path.</param>
/// <param name="IndexStart">Start of the raw index text in the path (after the opening bracket), or -1 without an index.</param>
/// <param name="End">The position just past the segment, after the closing bracket when there is an index.</param>
internal readonly record struct PathSegment(int NameStart, int IndexStart, int End)
{
    public bool HasIndex => IndexStart >= 0;

    public string GetName(string path)
        => path.Substring(NameStart, (HasIndex ? IndexStart - 1 : End) - NameStart);

    /// <summary>
    /// The index text as written, with each closing bracket doubled. Only valid when <see cref="HasIndex"/>.
    /// </summary>
    public ReadOnlySpan<char> GetRawIndex(string path)
        => path.AsSpan(IndexStart, End - 1 - IndexStart);
}

/// <summary>
/// Why <see cref="PathSyntax.TryReadSegment"/> rejected a segment.
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
