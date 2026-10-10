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
                propertyPath == watchPath ||
                propertyPath.StartsWith(watchPath + "/", StringComparison.Ordinal) ||
                propertyPath.StartsWith(watchPath + "[", StringComparison.Ordinal))
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
}
