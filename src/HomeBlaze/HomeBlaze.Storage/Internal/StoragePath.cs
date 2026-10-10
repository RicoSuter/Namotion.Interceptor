using FluentStorage.Blobs;

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

    /// <summary>
    /// The path of a listed blob, equal to <see cref="Normalize"/> of its full path.
    /// </summary>
    /// <remarks>
    /// Put together from the folder and the name of the blob, because a blob builds its full path anew from its
    /// parts on every read, which costs about a kilobyte of allocations per blob.
    /// </remarks>
    public static string FromBlob(Blob blob)
    {
        var folder = blob.FolderPath.AsSpan().Trim('/');
        return Normalize(folder.IsEmpty ? blob.Name : string.Concat(folder, "/", blob.Name));
    }

    public static string GetParent(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? Root : path[..separatorIndex];
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
