using HomeBlaze.Storage.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The names of metadata files. <c>.metadata.json</c> in a folder describes the folder, and <c>X.metadata.json</c>
/// beside <c>X.json</c> describes the subject of that file. A metadata file never becomes a subject.
/// </summary>
internal static class StorageMetadataPath
{
    public const string FolderFileName = ".metadata.json";

    private const string Suffix = ".metadata.json";

    public static bool IsMetadataFile(string path)
        => path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name of what a metadata file describes: empty for the folder that holds it, else the name of a subject in
    /// that folder.
    /// </summary>
    public static string GetDescribedName(string path)
    {
        var name = StoragePath.GetName(path);
        return name[..^Suffix.Length];
    }

    public static string GetFolderFilePath(string folder)
        => folder.Length == 0 ? FolderFileName : $"{folder}/{FolderFileName}";

    /// <summary>The metadata file of the subject of a JSON file, for example Devices/Motor.metadata.json for Devices/Motor.json.</summary>
    public static string GetSubjectFilePath(string subjectPath)
        => string.Concat(subjectPath.AsSpan(0, subjectPath.Length - FileExtensions.Json.Length), Suffix);
}
