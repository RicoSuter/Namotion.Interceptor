namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Reads the segments of a path one at a time without allocating: segments joined by
/// <see cref="PathProviderBase.PathSeparator"/>, each with an optional index between
/// <see cref="PathProviderBase.IndexOpen"/> and <see cref="PathProviderBase.IndexClose"/>. An index is the text up to
/// the first closing bracket, so a key containing one has no path. Empty segments are skipped.
/// </summary>
/// <remarks>
/// A mutable struct: a copy reads on independently from where the original was, and a reader stored in a
/// <see langword="readonly"/> field does not advance. A default reader has no segments.
/// </remarks>
internal struct PathSegmentReader
{
    private readonly string? _path;
    private readonly PathCharacters _characters;
    private int _position;
    private PathSyntaxError _error;

    /// <summary>
    /// Initializes a reader at the start of <paramref name="path"/>.
    /// </summary>
    /// <param name="pathProvider">The path provider defining the separator and index characters.</param>
    /// <param name="path">The path to read. Null reads as an empty path.</param>
    public PathSegmentReader(PathProviderBase pathProvider, string? path)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        _path = path;
        _characters = pathProvider.GetCharacters();
        _position = 0;
        _error = PathSyntaxError.None;
        SkipSeparators();
    }

    /// <summary>
    /// Gets whether another segment, or malformed text, remains to be read.
    /// </summary>
    public readonly bool HasNext => _error == PathSyntaxError.None && _path is not null && _position < _path.Length;

    /// <summary>
    /// Gets whether <see cref="TryRead"/> returned false because the path is malformed.
    /// </summary>
    public readonly bool IsMalformed => _error != PathSyntaxError.None;

    /// <summary>
    /// Gets the reason and position of the error when <see cref="IsMalformed"/>, otherwise null. Each call on a
    /// malformed path builds a new string.
    /// </summary>
    public readonly string? Error => IsMalformed ? PathSyntax.FormatError(_error, _position, _characters, _path!) : null;

    /// <summary>
    /// Reads the next segment.
    /// </summary>
    /// <param name="segment">The segment, or default when the method returns false.</param>
    /// <returns>
    /// True when a segment was read; false at the end of the path or when the rest of the path is malformed, see
    /// <see cref="IsMalformed"/>. Once false, every later call returns false.
    /// </returns>
    public bool TryRead(out PathSegment segment)
    {
        segment = default;
        if (!HasNext)
        {
            return false;
        }

        var path = _path!;
        var characters = _characters;

        // Outside an index the closing bracket is an ordinary name character.
        var nameStart = _position;
        var nameLength = path.AsSpan(nameStart).IndexOfAny(characters.Separator, characters.IndexOpen);
        var position = nameLength < 0 ? path.Length : nameStart + nameLength;
        if (position == nameStart)
        {
            return Fail(PathSyntaxError.MissingName, position);
        }

        if (position == path.Length || path[position] != characters.IndexOpen)
        {
            segment = new PathSegment(path, nameStart, 0, position);
            _position = position;
            SkipSeparators();
            return true;
        }

        var openPosition = position;
        var indexLength = path.AsSpan(openPosition + 1).IndexOf(characters.IndexClose);
        if (indexLength < 0)
        {
            return Fail(PathSyntaxError.UnclosedIndex, openPosition);
        }

        if (indexLength == 0)
        {
            return Fail(PathSyntaxError.EmptyIndex, openPosition);
        }

        position = openPosition + indexLength + 2;
        if (position < path.Length && path[position] != characters.Separator)
        {
            return Fail(PathSyntaxError.ExpectedSeparator, position);
        }

        segment = new PathSegment(path, nameStart, openPosition, position);
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
