namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Decides which storage paths never become subjects, for the startup scan and the file watcher alike.
/// </summary>
internal static class StoragePathFilter
{
    /// <summary>
    /// Checks whether the path or one of its parent folders is hidden, i.e. starts with a dot (e.g. .DS_Store, .idea/workspace.xml).
    /// </summary>
    public static bool IsHidden(ReadOnlySpan<char> path)
    {
        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] == '.' && (index == 0 || path[index - 1] is '/' or '\\'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the path is a temporary file that editors write before renaming it to the real name.
    /// </summary>
    public static bool IsTemporaryFile(ReadOnlySpan<char> path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith('~') ||
               fileName.EndsWith('~') ||
               fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase);
    }
}
