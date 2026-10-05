namespace HomeBlaze.History.Sqlite;

/// <summary>
/// Resolves the directory that holds the SQLite partition database files. Relative paths resolve under the
/// history folder of the instance data directory, which is outside the scanned subject files. Without a data
/// directory they resolve under the per-user local application data folder. Absolute paths are used as-is.
/// </summary>
internal static class SqliteDatabaseLocation
{
    /// <summary>
    /// The folder name used when no database path is configured.
    /// </summary>
    public const string DefaultFolderName = "Sqlite";

    /// <summary>
    /// The base directory under which relative database paths resolve: <c>History</c> in the instance data
    /// directory, or the local application data folder with a "HomeBlaze" subfolder when there is none.
    /// </summary>
    public static string DefaultBaseDirectory(string? dataDirectory) =>
        dataDirectory is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeBlaze")
            : Path.Combine(dataDirectory, "History");

    /// <summary>
    /// Resolves the configured database path against the given base directory. Null, empty, or whitespace
    /// resolves to the default folder under the base directory. A rooted (absolute) path is returned
    /// unchanged; a relative path is combined with the base directory.
    /// </summary>
    public static string Resolve(string? configuredPath, string baseDirectory)
    {
        var value = string.IsNullOrWhiteSpace(configuredPath) ? DefaultFolderName : configuredPath.Trim();

        return Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value);
    }
}
