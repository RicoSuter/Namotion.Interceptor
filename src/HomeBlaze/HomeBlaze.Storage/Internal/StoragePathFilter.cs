namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Decides which storage paths never become subjects, for the listing of a pass and the file watcher alike.
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
    /// Checks whether the path never becomes a subject: it is hidden or has a temporary segment.
    /// </summary>
    /// <remarks>Only for paths within the storage, see <see cref="HasTemporarySegment"/>.</remarks>
    public static bool IsIgnored(ReadOnlySpan<char> path)
        => IsHidden(path) || HasTemporarySegment(path);

    /// <summary>
    /// Checks whether the path or one of its parent folders has a temporary name.
    /// </summary>
    /// <remarks>Only for paths within the storage: an absolute path can have such a folder above the storage.</remarks>
    public static bool HasTemporarySegment(ReadOnlySpan<char> path)
    {
        while (!path.IsEmpty)
        {
            var separatorIndex = path.IndexOfAny('/', '\\');
            var segment = separatorIndex < 0 ? path : path[..separatorIndex];
            if (IsTemporaryName(segment))
            {
                return true;
            }

            path = separatorIndex < 0 ? [] : path[(separatorIndex + 1)..];
        }

        return false;
    }

    private static bool IsTemporaryName(ReadOnlySpan<char> name)
        => name.StartsWith('~') ||
           name.EndsWith('~') ||
           name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
           name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase);
}
