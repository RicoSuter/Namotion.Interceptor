using FluentStorage.Blobs;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Paths within a storage as the index holds them: forward slashes, no leading or trailing slash, exact casing.
/// </summary>
internal static class StoragePath
{
    public static string Normalize(string path)
        => path.Replace('\\', '/').Trim('/');

    /// <summary>
    /// The path of a listed blob, equal to <see cref="Normalize"/> of its full path.
    /// </summary>
    public static string FromBlob(Blob blob)
    {
        // Not from the full path: a blob builds that anew from its parts on every read, about a kilobyte per blob.
        var folder = blob.FolderPath.AsSpan().Trim('/');
        return Normalize(folder.IsEmpty ? blob.Name : string.Concat(folder, "/", blob.Name));
    }

    /// <summary>
    /// The path as a file subject receives it: with the leading slash that the full path of a blob has.
    /// </summary>
    public static string ToFullPath(string path)
        => "/" + path;

    /// <summary>The path of the folder that holds the path, empty for a path in the root of the storage.</summary>
    public static string GetParent(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? string.Empty : path[..separatorIndex];
    }

    /// <inheritdoc cref="GetParent(string)"/>
    public static ReadOnlySpan<char> GetParent(ReadOnlySpan<char> path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? [] : path[..separatorIndex];
    }

    public static string GetName(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }

    public static int GetDepth(string path)
        => path.AsSpan().Count('/');
}
