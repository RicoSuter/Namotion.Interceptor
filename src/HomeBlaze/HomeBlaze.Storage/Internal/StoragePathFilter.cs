namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Decides which storage paths never become subjects, for the listing of a pass and the file watcher alike.
/// </summary>
internal static class StoragePathFilter
{
    /// <summary>
    /// Checks whether the path never becomes a subject: the path or one of its parent folders is hidden, which
    /// is a name that starts with a dot (.DS_Store, .idea/workspace.xml), or has a temporary name. The metadata
    /// file of a folder, <c>.metadata.json</c> in any case, is not hidden.
    /// </summary>
    /// <remarks>
    /// Only for paths within the storage and with forward slashes: an absolute path can have such a folder
    /// above the storage.
    /// </remarks>
    public static bool IsIgnored(ReadOnlySpan<char> path)
    {
        while (!path.IsEmpty)
        {
            var separatorIndex = path.IndexOf('/');
            var segment = separatorIndex < 0 ? path : path[..separatorIndex];
            // In any case, so that a pass warns about one that is named in another case.
            var isFolderMetadataFile = separatorIndex < 0 && segment.Equals(StorageMetadataPath.FolderFileName, StringComparison.OrdinalIgnoreCase);
            if ((segment.StartsWith('.') && !isFolderMetadataFile) || IsTemporaryName(segment))
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
