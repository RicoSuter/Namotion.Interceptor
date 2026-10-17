using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// What a listing reports about the content of a file. Compared for equality only.
/// </summary>
/// <remarks>
/// The default value is recorded where the version of a file is not known, so that the next pass looks at the
/// file again. It equals the version of an empty file that a storage lists without a modification time.
/// </remarks>
internal readonly record struct StorageVersion(long Size, DateTimeOffset? Modified);

/// <summary>
/// What the index holds for one path: the subject of the file or folder, the version and hash of the file that
/// the subject was last brought in line with, and the key under which the subject is in the tree.
/// </summary>
internal sealed class StorageEntry
{
    public required string Path { get; init; }

    /// <summary>The path of the folder that holds the entry.</summary>
    public string Parent => field ??= StoragePath.GetParent(Path);

    public required bool IsFolder { get; init; }

    public StorageVersion Version { get; set; }

    public IInterceptorSubject? Subject { get; set; }

    /// <summary>
    /// The hash of the content that the subject was last brought in line with. Null for a folder, for a file
    /// that is never read, and from the start of a load or a write until the subject has taken the content,
    /// so also after a load that failed.
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>
    /// The key in the parent folder that the entry holds or asked for. Null until the entry was first applied
    /// to the tree.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>True for a metadata file, which never has a subject and holds <see cref="Metadata"/> instead.</summary>
    public bool IsMetadata { get; init; }

    /// <summary>The content of a metadata file, null when the file could not be read as one.</summary>
    public StorageMetadataDocument? Metadata { get; set; }

    /// <summary>True when the subject is in the tree under <see cref="Key"/>.</summary>
    public bool IsPlaced => Key != null && Subject != null;

    /// <summary>
    /// True when the entry was applied and is not in the tree, because its key is held by another entry or its
    /// folder is not placed.
    /// </summary>
    public bool IsKeyTaken => Key != null && Subject == null;
}
