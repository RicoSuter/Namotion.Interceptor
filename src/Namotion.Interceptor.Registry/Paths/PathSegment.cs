namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// One segment of a path read by <see cref="PathSegmentReader"/>: a name with an optional index in brackets. The
/// segment points into the path it was read from, so reading its name or index allocates nothing.
/// </summary>
internal readonly struct PathSegment
{
    private readonly string? _path;
    private readonly int _nameStart;

    // Position of the opening bracket, or 0 without an index: a name is never empty, so no bracket sits at 0.
    private readonly int _indexOpen;

    internal PathSegment(string path, int nameStart, int indexOpen, int end)
    {
        _path = path;
        _nameStart = nameStart;
        _indexOpen = indexOpen;
        End = end;
    }

    /// <summary>
    /// Gets the segment name, without the index.
    /// </summary>
    public ReadOnlySpan<char> Name => _path.AsSpan(_nameStart, NameEnd - _nameStart);

    /// <summary>
    /// Gets whether the segment has an index.
    /// </summary>
    public bool HasIndex => _indexOpen > 0;

    /// <summary>
    /// Gets the index text between the brackets, or an empty span when the segment has no index.
    /// </summary>
    public ReadOnlySpan<char> Index => HasIndex ? _path.AsSpan(_indexOpen + 1, End - _indexOpen - 2) : default;

    /// <summary>
    /// Gets the position in the path just past the segment, after the closing bracket when there is an index.
    /// </summary>
    public int End { get; }

    private int NameEnd => HasIndex ? _indexOpen : End;

    /// <summary>
    /// Gets the segment name as a string, which is the path itself when the segment is the whole path.
    /// </summary>
    public string GetName() => _path?.Substring(_nameStart, NameEnd - _nameStart) ?? string.Empty;
}
