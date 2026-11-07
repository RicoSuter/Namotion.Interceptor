using System.Collections.Concurrent;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The links that widgets of one page resolved, for one structure version of the registry and one version of the paths of
/// the graph. Replaced as a whole when either changes, so it holds about <paramref name="capacity"/> links and lives as long
/// as its page.
/// </summary>
/// <remarks>
/// Safe for concurrent use: the versions, base path and page name never change, and links are only added.
/// </remarks>
internal sealed class ResolvedLinks(long structureVersion, long pathsVersion, string basePath, string pageName, int capacity)
{
    // One lock and a small table: a page keeps few links, and a new instance is built after every change of the graph.
    private readonly ConcurrentDictionary<string, string> _links = new(concurrencyLevel: 1, capacity: 8, StringComparer.Ordinal);
    private int _count;

    public long StructureVersion { get; } = structureVersion;

    public long PathsVersion { get; } = pathsVersion;

    /// <summary>The base path of the page that relative links are resolved against, already escaped for a link.</summary>
    public string BasePath { get; } = basePath;

    /// <summary>The name of the page as a link writes it, already escaped for a link.</summary>
    public string PageName { get; } = pageName;

    public int Count => Volatile.Read(ref _count);

    public bool TryGet(string link, out string resolved) => _links.TryGetValue(link, out resolved!);

    /// <summary>
    /// Keeps a resolved link unless the capacity is reached, so that links that change on every render, for example one
    /// bound to a value, cannot grow it. Concurrent adds can pass the check together and exceed it by the number of
    /// concurrent renders.
    /// </summary>
    public void TryAdd(string link, string resolved)
    {
        if (Volatile.Read(ref _count) < capacity && _links.TryAdd(link, resolved))
        {
            Interlocked.Increment(ref _count);
        }
    }
}
