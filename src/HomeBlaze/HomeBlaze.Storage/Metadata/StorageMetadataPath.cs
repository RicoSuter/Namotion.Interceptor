using HomeBlaze.Storage.Abstractions;

namespace HomeBlaze.Storage.Metadata;

/// <summary>
/// The names of metadata files. <c>.metadata.json</c> in a folder describes the folder, and <c>X.metadata.json</c>
/// beside <c>X.json</c> describes the subject of that file. A metadata file never becomes a subject.
/// </summary>
internal static class StorageMetadataPath
{
    public const string FolderFileName = ".metadata.json";

    private const string Suffix = ".metadata.json";

    /// <summary>Whether the path names a metadata file, in exactly this case, as the files are found by that name.</summary>
    public static bool IsMetadataFile(string path)
        => path.EndsWith(Suffix, StringComparison.Ordinal);

    /// <summary>
    /// Whether a subject at the path would share its file with a metadata file on a file system that ignores case, or
    /// its metadata file with its folder, as <c>.json</c> has no name before the extension.
    /// </summary>
    public static bool IsReservedSubjectPath(string path)
        => path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ||
           Path.GetFileName(path.AsSpan()).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase);

    public static string GetFolderFilePath(string folder)
        => folder.Length == 0 ? FolderFileName : $"{folder}/{FolderFileName}";

    /// <summary>The metadata file of the subject of a JSON file, for example Devices/Motor.metadata.json for Devices/Motor.json.</summary>
    public static string GetSubjectFilePath(string subjectPath)
        => string.Concat(subjectPath.AsSpan(0, subjectPath.Length - FileExtensions.Json.Length), Suffix);
}
