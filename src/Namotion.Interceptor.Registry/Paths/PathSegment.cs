namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// One segment of a path read by <see cref="PathSegmentReader"/>: a name with an optional index.
/// <see cref="Index"/> points into the path without allocating, unless the key contained doubled quotes, in which
/// case it points into an unescaped copy instead. <see cref="GetName"/> and <see cref="GetIndex"/> allocate strings.
/// A segment is only valid when <see cref="PathSegmentReader.TryRead"/> returned true; a default segment has no meaning.
/// </summary>
internal readonly struct PathSegment
{
    private readonly string _path;
    private readonly int _nameStart;
    private readonly int _nameLength;
    private readonly int _indexStart;
    private readonly int _indexLength;
    private readonly string? _unescapedIndex;

    public PathSegment(string path, int nameStart, int nameLength)
    {
        _path = path;
        _nameStart = nameStart;
        _nameLength = nameLength;
    }

    public PathSegment(string path, int nameStart, int nameLength, int indexStart, int indexLength, string? unescapedIndex)
        : this(path, nameStart, nameLength)
    {
        _indexStart = indexStart;
        _indexLength = indexLength;
        _unescapedIndex = unescapedIndex;
        HasIndex = true;
    }

    public bool HasIndex { get; }

    /// <summary>Gets the index text without quotes, or an empty span when <see cref="HasIndex"/> is false.</summary>
    public ReadOnlySpan<char> Index
        => _unescapedIndex is not null ? _unescapedIndex.AsSpan()
            : HasIndex ? _path.AsSpan(_indexStart, _indexLength)
            : default;

    public string GetName() => _path.Substring(_nameStart, _nameLength);

    /// <summary>Gets the index text without quotes, or null when <see cref="HasIndex"/> is false.</summary>
    public string? GetIndex() => HasIndex ? _unescapedIndex ?? _path.Substring(_indexStart, _indexLength) : null;
}
