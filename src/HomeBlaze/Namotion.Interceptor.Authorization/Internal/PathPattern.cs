using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// A compiled path pattern. It matches a member path, the path of the member's subject followed by the member, when
/// it matches the whole of it: <c>*</c> alone is one segment, <c>**</c> zero or more. A pattern segment matches a link
/// in any spelling the link carries.
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

    /// <summary>Whether the pattern matches the member path made of the subject's <paramref name="path"/> and <paramref name="member"/>.</summary>
    public bool Matches(PathLink[] path, in PathLink member)
    {
        // The classic glob over segments: backtracking to the last ** suffices, because ** matches any segments and
        // every other pattern segment exactly one, which bounds the work by pattern length times path length.
        var length = path.Length + 1;
        var patternIndex = 0;
        var pathIndex = 0;
        var lastAnySegments = -1;
        var pathAtLastAnySegments = 0;

        while (pathIndex < length)
        {
            if (patternIndex < _segments.Length && _segments[patternIndex].IsAnySegments)
            {
                lastAnySegments = patternIndex++;
                pathAtLastAnySegments = pathIndex;
            }
            else if (patternIndex < _segments.Length && _segments[patternIndex].Matches(pathIndex < path.Length ? path[pathIndex] : member))
            {
                patternIndex++;
                pathIndex++;
            }
            else if (lastAnySegments >= 0)
            {
                patternIndex = lastAnySegments + 1;
                pathIndex = ++pathAtLastAnySegments;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < _segments.Length && _segments[patternIndex].IsAnySegments)
        {
            patternIndex++;
        }

        return patternIndex == _segments.Length;
    }

    private readonly record struct Segment(string Name, string? Index, bool IsAnySegments)
    {
        // The explicit spelling names the property and optionally its index; the bare spelling of an [InlinePaths]
        // key is a name only, so a segment with an index never matches it.
        public bool Matches(in PathLink link)
            => MatchesExplicit(link) || (link.BareKey is not null && Index is null && Glob.IsMatch(Name, link.BareKey));

        private bool MatchesExplicit(in PathLink link)
            => (MatchesName(link.Segment) || MatchesName(link.AttributeSegment)) && MatchesIndex(link.Index);

        private bool MatchesName(string? segment)
            => segment is not null && Glob.IsMatch(Name, segment);

        private bool MatchesIndex(string? index)
            => Index is null || (index is not null && Glob.IsMatch(Index, index));
    }
}
