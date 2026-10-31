namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Finds files of the repository from the test output folder.
/// </summary>
internal static class RepositoryPaths
{
    /// <summary>
    /// Gets the full path of a file from its path relative to the repository root, searching upward from the test output.
    /// </summary>
    /// <exception cref="FileNotFoundException">No folder above the test output holds the file.</exception>
    public static string Get(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{relativePath} was not found above {AppContext.BaseDirectory}.");
    }
}
