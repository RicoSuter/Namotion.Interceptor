using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Thread-safe bidirectional mapping between subjects and storage paths.
/// Tracks content hashes and file sizes for change detection.
/// </summary>
internal sealed class StoragePathRegistry
{
    // Stores original path (preserves case) for actual file operations
    private readonly ConcurrentDictionary<IInterceptorSubject, string> _subjectPaths = new();
    // Uses normalized (lowercase) key for case-insensitive lookup
    private readonly ConcurrentDictionary<string, IInterceptorSubject> _pathToSubject = new();
    private readonly ConcurrentDictionary<string, long> _fileSizes = new();
    private readonly ConcurrentDictionary<string, string> _contentHashes = new();

    public int Count => _subjectPaths.Count;

    /// <summary>
    /// Gets the original path (with preserved case) for a subject.
    /// </summary>
    public bool TryGetPath(IInterceptorSubject subject, out string path)
        => _subjectPaths.TryGetValue(subject, out path!);

    /// <summary>
    /// Gets a subject by path (case-insensitive lookup on Windows).
    /// </summary>
    public bool TryGetSubject(string path, out IInterceptorSubject subject)
        => _pathToSubject.TryGetValue(NormalizeForLookup(path), out subject!);

    public void Register(IInterceptorSubject subject, string path)
    {
        var originalPath = NormalizePath(path);  // Preserves case
        var lookupKey = NormalizeForLookup(path);  // Lowercase for lookup
        _subjectPaths[subject] = originalPath;
        _pathToSubject[lookupKey] = subject;
    }

    /// <summary>
    /// Normalizes paths: forward slashes, remove leading slashes. Preserves case.
    /// </summary>
    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Normalizes paths for dictionary lookup: forward slashes, remove leading slashes, lowercase.
    /// The lowercase is for case-insensitive matching on Windows (harmless on Linux).
    /// </summary>
    private static string NormalizeForLookup(string path)
        => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    public void Unregister(string path)
    {
        var lookupKey = NormalizeForLookup(path);
        if (_pathToSubject.TryRemove(lookupKey, out var subject))
        {
            _subjectPaths.TryRemove(subject, out _);
        }

        _contentHashes.TryRemove(lookupKey, out _);
        _fileSizes.TryRemove(lookupKey, out _);
    }

    public void UpdateHash(string path, string hash)
        => _contentHashes[NormalizeForLookup(path)] = hash;

    public void UpdateSize(string path, long size)
        => _fileSizes[NormalizeForLookup(path)] = size;

    public bool TryGetHash(string path, out string hash)
        => _contentHashes.TryGetValue(NormalizeForLookup(path), out hash!);

    public bool TryGetSize(string path, out long size)
        => _fileSizes.TryGetValue(NormalizeForLookup(path), out size);

    public bool HasSizeChanged(string path, long newSize)
        => !_fileSizes.TryGetValue(NormalizeForLookup(path), out var oldSize) || oldSize != newSize;

    public bool HasHashChanged(string path, string newHash)
        => !_contentHashes.TryGetValue(NormalizeForLookup(path), out var oldHash) || oldHash != newHash;

    public void Clear()
    {
        _subjectPaths.Clear();
        _pathToSubject.Clear();
        _contentHashes.Clear();
        _fileSizes.Clear();
    }

    /// <summary>
    /// Computes a SHA256 hash of the content string.
    /// </summary>
    public static string ComputeHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Computes a SHA256 hash of the stream content.
    /// </summary>
    public static async Task<string> ComputeHashAsync(Stream stream)
    {
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }
}
