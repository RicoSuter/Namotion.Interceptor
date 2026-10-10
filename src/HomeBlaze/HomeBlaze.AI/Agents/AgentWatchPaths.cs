namespace HomeBlaze.AI.Agents;

/// <summary>
/// Path helpers of agents: watch path matching and the file name of an agent's data.
/// </summary>
public static class AgentWatchPaths
{
    // The Windows set, so data directories move between platforms without renames.
    private static readonly char[] InvalidFileNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

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
            if (watchPath == "/")
            {
                return true;
            }

            var watch = watchPath.AsSpan();
            if (watch.Length <= subjectPart.Length)
            {
                // The subject part continues with "/" when it is used up exactly.
                if (subjectPart.StartsWith(watch, StringComparison.Ordinal) &&
                    (watch.Length == subjectPart.Length || IsSegmentBoundary(subjectPart[watch.Length])))
                {
                    return true;
                }

                continue;
            }

            if (!watch.StartsWith(subjectPart, StringComparison.Ordinal) || watch[subjectPart.Length] != '/')
            {
                continue;
            }

            var propertyPart = watch[(subjectPart.Length + 1)..];
            if (propertyName.AsSpan().StartsWith(propertyPart, StringComparison.Ordinal) &&
                (propertyName.Length == propertyPart.Length || IsSegmentBoundary(propertyName[propertyPart.Length])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A flat file name for an agent's data files, derived from its canonical path.
    /// </summary>
    public static string GetStorageName(string subjectPath)
    {
        var trimmed = subjectPath.Trim('/');
        if (trimmed.Length == 0)
        {
            return "root";
        }

        var characters = trimmed.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (InvalidFileNameCharacters.Contains(characters[index]) || char.IsControl(characters[index]))
            {
                characters[index] = '_';
            }
        }

        return new string(characters);
    }

    private static bool IsSegmentBoundary(char character) => character is '/' or '[';
}
