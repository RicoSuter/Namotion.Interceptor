using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// What a listing reports about the content of a file. Only compared for equality: a backend with a stronger
/// token than size and time can supply that instead.
/// </summary>
internal readonly record struct StorageVersion(long Size, DateTimeOffset? Modified);

internal enum StorageEntryState
{
    /// <summary>Loaded in this pass and not applied to the tree yet.</summary>
    New,

    /// <summary>The subject is in the tree under <see cref="StorageEntry.Key"/>.</summary>
    Placed,

    /// <summary>Not in the tree: its key is held by another entry, or its folder is not placed.</summary>
    KeyTaken,

    /// <summary>The file could not be loaded.</summary>
    Failed
}

/// <summary>
/// What was applied last for one path.
/// </summary>
internal sealed class StorageEntry
{
    public required string Path { get; init; }

    /// <summary>The path of the folder that holds the entry.</summary>
    public string Parent => field ??= StoragePath.GetParent(Path);

    public required bool IsFolder { get; init; }

    public StorageVersion Version { get; set; }

    public IInterceptorSubject? Subject { get; set; }

    /// <summary>The content hash, for subjects that hold content. Null for folders and plain files.</summary>
    public string? Hash { get; set; }

    /// <summary>The key in the parent folder that the entry holds or asked for.</summary>
    public string? Key { get; set; }

    public StorageEntryState State { get; set; }
}
