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
    private readonly ConcurrentDictionary<IInterceptorSubject, string> _subjectPaths = new();
    private readonly ConcurrentDictionary<string, IInterceptorSubject> _pathToSubject = new();
    private readonly ConcurrentDictionary<string, long> _fileSizes = new();
    private readonly ConcurrentDictionary<string, string> _contentHashes = new();

    public int Count => _subjectPaths.Count;

    public bool TryGetPath(IInterceptorSubject subject, out string path)
        => _subjectPaths.TryGetValue(subject, out path!);

    public bool TryGetSubject(string path, out IInterceptorSubject subject)
        => _pathToSubject.TryGetValue(path, out subject!);

    public void Register(IInterceptorSubject subject, string path)
    {
        _subjectPaths[subject] = path;
        _pathToSubject[path] = subject;
    }

    public void Unregister(string path)
    {
        if (_pathToSubject.TryRemove(path, out var subject))
        {
            _subjectPaths.TryRemove(subject, out _);
        }

        _contentHashes.TryRemove(path, out _);
        _fileSizes.TryRemove(path, out _);
    }

    public void UpdateHash(string path, string hash)
        => _contentHashes[path] = hash;

    public void UpdateSize(string path, long size)
        => _fileSizes[path] = size;

    public bool TryGetHash(string path, out string hash)
        => _contentHashes.TryGetValue(path, out hash!);

    public bool TryGetSize(string path, out long size)
        => _fileSizes.TryGetValue(path, out size);

    public bool HasSizeChanged(string path, long newSize)
        => !_fileSizes.TryGetValue(path, out var oldSize) || oldSize != newSize;

    public bool HasHashChanged(string path, string newHash)
        => !_contentHashes.TryGetValue(path, out var oldHash) || oldHash != newHash;

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
