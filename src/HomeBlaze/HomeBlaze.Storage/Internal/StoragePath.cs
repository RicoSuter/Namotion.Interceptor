namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Paths within a storage as the index holds them: forward slashes, no leading or trailing slash, exact casing.
/// </summary>
internal static class StoragePath
{
    /// <summary>The path of the storage root, which is the parent of every top-level entry.</summary>
    public const string Root = "";

    public static string Normalize(string path)
        => path.Replace('\\', '/').Trim('/');

    public static string GetParent(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? Root : path[..separatorIndex];
    }

    public static string GetName(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }

    public static int GetDepth(string path)
        => path.AsSpan().Count('/');
}
