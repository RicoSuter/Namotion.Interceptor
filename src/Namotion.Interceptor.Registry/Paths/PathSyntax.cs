using System.Diagnostics.CodeAnalysis;
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

    public static bool TryParse(
        PathProviderBase pathProvider,
        string path,
        [NotNullWhen(true)] out List<(string segment, string? index)>? segments,
        [NotNullWhen(false)] out string? error)
    {
        ValidateCharacters(pathProvider);

        if (string.IsNullOrEmpty(path))
        {
            segments = [];
            error = null;
            return true;
        }

        var separator = pathProvider.PathSeparator;
        var indexOpen = pathProvider.IndexOpen;
        var indexClose = pathProvider.IndexClose;

        var results = new List<(string segment, string? index)>();
        var position = 0;
        while (position < path.Length)
        {
            if (path[position] == separator)
            {
                // Empty segments (leading, trailing or doubled separators) are skipped.
                position++;
                continue;
            }

            // Outside an index the closing bracket is an ordinary name character.
            var nameStart = position;
            while (position < path.Length &&
                   path[position] != separator &&
                   path[position] != indexOpen)
            {
                position++;
            }

            if (position == nameStart)
            {
                return Fail("Missing segment name", position, path, out segments, out error);
            }

            var name = path.Substring(nameStart, position - nameStart);
            string? index = null;

            if (position < path.Length && path[position] == indexOpen)
            {
                var openPosition = position;
                if (!TryReadIndex(path, ref position, indexClose, out index))
                {
                    return Fail($"Unclosed '{indexOpen}'", openPosition, path, out segments, out error);
                }

                if (index.Length == 0)
                {
                    return Fail("Empty index", openPosition, path, out segments, out error);
                }

                if (position < path.Length && path[position] != separator)
                {
                    return Fail($"Expected '{separator}' or end of path after index", position, path, out segments, out error);
                }
            }

            results.Add((name, index));
        }

        segments = results;
        error = null;
        return true;
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
        if (index is int position)
        {
            // Collection positions are the common case; formatting them on the stack avoids a string per segment.
            Span<char> buffer = stackalloc char[11];
            position.TryFormat(buffer, out var written, provider: CultureInfo.InvariantCulture);
            AppendQuoted(builder, buffer[..written], indexOpen, indexClose);
            return true;
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
    /// Reads the index text after the opening bracket at <paramref name="position"/>. On success
    /// <paramref name="position"/> is just past the closing bracket.
    /// </summary>
    private static bool TryReadIndex(string path, ref int position, char indexClose, [NotNullWhen(true)] out string? index)
    {
        position++;
        var start = position;
        StringBuilder? builder = null;
        while (position < path.Length)
        {
            if (path[position] != indexClose)
            {
                position++;
                continue;
            }

            if (position + 1 < path.Length && path[position + 1] == indexClose)
            {
                // A doubled closing bracket is a literal one; keep the first and skip the second.
                builder ??= new StringBuilder();
                builder.Append(path, start, position + 1 - start);
                position += 2;
                start = position;
                continue;
            }

            index = builder is null
                ? path.Substring(start, position - start)
                : builder.Append(path, start, position - start).ToString();
            position++;
            return true;
        }

        index = null;
        return false;
    }

    private static bool Fail(
        string reason,
        int position,
        string path,
        out List<(string segment, string? index)>? segments,
        out string? error)
    {
        segments = null;
        error = string.Create(CultureInfo.InvariantCulture, $"{reason} at position {position} in path '{path}'");
        return false;
    }
}
