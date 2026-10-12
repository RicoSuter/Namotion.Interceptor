namespace HomeBlaze.AI.Agents;

/// <summary>
/// Watch path matching of agents.
/// </summary>
public static class AgentWatchPaths
{
    /// <summary>
    /// Trims entries, drops blank ones, adds a leading slash and removes a trailing one.
    /// </summary>
    public static string[] Normalize(IEnumerable<string>? watchPaths)
    {
        return (watchPaths ?? [])
            .Select(path => path.Trim())
            .Where(path => path.Length > 0)
            .Select(path => "/" + path.Trim('/'))
            .ToArray();
    }

    /// <summary>
    /// Whether a canonical property path lies in one of the normalized watch paths.
    /// </summary>
    public static bool Matches(string propertyPath, IReadOnlyList<string> normalizedWatchPaths)
    {
        foreach (var watchPath in normalizedWatchPaths)
        {
            if (watchPath == "/" ||
                (propertyPath.StartsWith(watchPath, StringComparison.Ordinal) &&
                 (propertyPath.Length == watchPath.Length || IsSegmentBoundary(propertyPath[watchPath.Length]))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the property <paramref name="propertyName"/> of the subject at the canonical
    /// <paramref name="subjectPath"/> lies in one of the normalized watch paths, without joining the two.
    /// </summary>
    public static bool Matches(string subjectPath, string propertyName, IReadOnlyList<string> normalizedWatchPaths)
    {
        // The property path is subjectPart + "/" + propertyName; the root's subject part is empty.
        var subjectPart = subjectPath == "/" ? ReadOnlySpan<char>.Empty : subjectPath.AsSpan();
        foreach (var watchPath in normalizedWatchPaths)
        {
            if (watchPath == "/" || MatchesWatchPath(subjectPart, propertyName, watchPath.AsSpan()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesWatchPath(ReadOnlySpan<char> subjectPart, string propertyName, ReadOnlySpan<char> watch)
    {
        if (watch.Length <= subjectPart.Length)
        {
            // The subject part continues with "/" when it is used up exactly.
            return StartsWithSegment(subjectPart, watch);
        }

        return watch.StartsWith(subjectPart, StringComparison.Ordinal) &&
            watch[subjectPart.Length] == '/' &&
            StartsWithSegment(propertyName, watch[(subjectPart.Length + 1)..]);
    }

    private static bool StartsWithSegment(ReadOnlySpan<char> text, ReadOnlySpan<char> prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) &&
        (prefix.Length == text.Length || IsSegmentBoundary(text[prefix.Length]));

    private static bool IsSegmentBoundary(char character) => character is '/' or '[';
}
