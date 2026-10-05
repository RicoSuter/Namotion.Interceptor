using System.Text;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Reads the segments of a path one at a time: segments joined by the separator, each with an optional index in
/// brackets. Inside an index the separator and the opening bracket are literal; an index starting with a quote runs to
/// the next lone quote, with a doubled quote standing for one, otherwise it runs to the first closing bracket. Empty
/// segments are skipped.
/// </summary>
/// <remarks>A mutable struct: a copy reads on independently from where the original was.</remarks>
internal struct PathSegmentReader
{
    private readonly string? _path;
    private readonly PathCharacters _characters;
    private int _position;
    private PathSyntaxError _error;

    public PathSegmentReader(PathProviderBase pathProvider, string? path)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        _path = path;
        _characters = pathProvider.GetCharacters();
        _position = 0;
        _error = PathSyntaxError.None;
        SkipSeparators();
    }

    /// <summary>Gets whether another segment, or malformed text, remains to be read.</summary>
    public readonly bool HasNext => _error == PathSyntaxError.None && _path is not null && _position < _path.Length;

    /// <summary>Gets whether <see cref="TryRead"/> returned false because the path is malformed.</summary>
    public readonly bool IsMalformed => _error != PathSyntaxError.None;

    /// <summary>Gets the reason and position of the error when <see cref="IsMalformed"/>, otherwise null.</summary>
    public readonly string? Error => IsMalformed ? PathSyntax.FormatError(_path!, _position, _error, _characters) : null;

    /// <summary>
    /// Reads the next segment. False at the end of the path or when the rest of the path is malformed, see
    /// <see cref="IsMalformed"/>; once false, every later call returns false.
    /// </summary>
    public bool TryRead(out PathSegment segment)
    {
        segment = default;
        if (!HasNext)
        {
            return false;
        }

        var path = _path!;
        var characters = _characters;

        var nameStart = _position;
        var nameLength = path.AsSpan(nameStart).IndexOfAny(characters.Separator, characters.IndexOpen);
        var nameEnd = nameLength < 0 ? path.Length : nameStart + nameLength;
        if (nameEnd == nameStart)
        {
            return Fail(PathSyntaxError.MissingName, nameStart);
        }

        if (nameEnd == path.Length || path[nameEnd] != characters.IndexOpen)
        {
            segment = new PathSegment(path, nameStart, nameEnd - nameStart);
            return Advance(nameEnd);
        }

        var openPosition = nameEnd;
        var keyStart = openPosition + 1;
        int position;
        int keyLength;
        string? unescapedKey = null;
        if (keyStart < path.Length && path[keyStart] == PathSyntax.Quote)
        {
            if (!TryReadQuotedKey(path, keyStart, out keyLength, out unescapedKey, out position))
            {
                return Fail(PathSyntaxError.UnclosedQuote, keyStart);
            }

            if (position == path.Length || path[position] != characters.IndexClose)
            {
                return Fail(PathSyntaxError.ExpectedIndexClose, position);
            }

            keyStart++;
            position++;
        }
        else
        {
            keyLength = path.AsSpan(keyStart).IndexOf(characters.IndexClose);
            if (keyLength < 0)
            {
                return Fail(PathSyntaxError.UnclosedIndex, openPosition);
            }

            if (keyLength == 0)
            {
                return Fail(PathSyntaxError.EmptyIndex, openPosition);
            }

            position = keyStart + keyLength + 1;
        }

        if (position < path.Length && path[position] != characters.Separator)
        {
            return Fail(PathSyntaxError.ExpectedSeparator, position);
        }

        segment = new PathSegment(path, nameStart, nameEnd - nameStart, keyStart, keyLength, unescapedKey);
        return Advance(position);
    }

    /// <summary>
    /// Reads a quoted key whose opening quote is at <paramref name="quotePosition"/>. <paramref name="end"/> is just
    /// past the closing quote; <paramref name="unescaped"/> is set only when the key contains a doubled quote.
    /// </summary>
    private static bool TryReadQuotedKey(string path, int quotePosition, out int contentLength, out string? unescaped, out int end)
    {
        var contentStart = quotePosition + 1;
        var runStart = contentStart;
        var position = contentStart;
        StringBuilder? builder = null;
        while (true)
        {
            var offset = path.AsSpan(position).IndexOf(PathSyntax.Quote);
            if (offset < 0)
            {
                contentLength = 0;
                unescaped = null;
                end = 0;
                return false;
            }

            var quote = position + offset;
            if (quote + 1 < path.Length && path[quote + 1] == PathSyntax.Quote)
            {
                builder ??= new StringBuilder();
                builder.Append(path, runStart, quote + 1 - runStart);
                position = quote + 2;
                runStart = position;
                continue;
            }

            contentLength = quote - contentStart;
            unescaped = builder?.Append(path, runStart, quote - runStart).ToString();
            end = quote + 1;
            return true;
        }
    }

    private bool Advance(int position)
    {
        _position = position;
        SkipSeparators();
        return true;
    }

    private void SkipSeparators()
    {
        var path = _path;
        if (path is null)
        {
            return;
        }

        while (_position < path.Length && path[_position] == _characters.Separator)
        {
            _position++;
        }
    }

    private bool Fail(PathSyntaxError error, int position)
    {
        _error = error;
        _position = position;
        return false;
    }
}
