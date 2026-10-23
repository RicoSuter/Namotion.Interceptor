using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// A compiled path pattern. It matches a path when it matches the whole path or a prefix of it, which is how a
/// rule on a subject also applies to everything below it.
/// </summary>
internal sealed class PathPattern
{
    private const string AnySegments = "**";

    private readonly Segment[] _segments;

    private PathPattern(Segment[] segments)
    {
        _segments = segments;
    }

    /// <exception cref="AuthorizationException">The pattern does not start with the separator, is malformed or has a segment with leading or trailing whitespace.</exception>
    public static PathPattern Parse(string pattern, PathProviderBase pathProvider, string roleName)
    {
        var separator = pathProvider.PathSeparator;
        if (pattern.Length == 0 || pattern[0] != separator)
        {
            throw new AuthorizationException($"Path pattern '{pattern}' of role '{roleName}' must start with '{separator}'.");
        }

        if (!pathProvider.TryParsePath(pattern, out var parsed, out var error))
        {
            throw new AuthorizationException($"Path pattern '{pattern}' of role '{roleName}' is malformed: {error}");
        }

        var segments = new Segment[parsed.Count];
        for (var i = 0; i < parsed.Count; i++)
        {
            var (name, index) = parsed[i];
            if (HasOuterWhitespace(name) || (index is not null && HasOuterWhitespace(index)))
            {
                throw new AuthorizationException($"Path pattern '{pattern}' of role '{roleName}' has a segment with leading or trailing whitespace.");
            }

            segments[i] = new Segment(name, index, IsAnySegments: name == AnySegments && index is null);
        }

        return new PathPattern(segments);
    }

    private static bool HasOuterWhitespace(string text)
        => text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]));

    public bool Matches(List<(string segment, string? index)> path)
    {
        // Backtracking to the last ** suffices: ** matches any segments, every other pattern segment exactly one,
        // and a used-up pattern matches whatever path remains.
        var patternIndex = 0;
        var pathIndex = 0;
        var afterLastAnySegments = -1;
        var pathAtLastAnySegments = 0;

        while (patternIndex < _segments.Length)
        {
            if (_segments[patternIndex].IsAnySegments)
            {
                afterLastAnySegments = ++patternIndex;
                pathAtLastAnySegments = pathIndex;
            }
            else if (pathIndex < path.Count && _segments[patternIndex].Matches(path[pathIndex]))
            {
                patternIndex++;
                pathIndex++;
            }
            else if (afterLastAnySegments >= 0 && pathAtLastAnySegments < path.Count)
            {
                patternIndex = afterLastAnySegments;
                pathIndex = ++pathAtLastAnySegments;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct Segment(string Name, string? Index, bool IsAnySegments)
    {
        public bool Matches((string segment, string? index) pathSegment)
            => Glob.IsMatch(Name, pathSegment.segment) &&
               (Index is null || (pathSegment.index is not null && Glob.IsMatch(Index, pathSegment.index)));
    }
}
