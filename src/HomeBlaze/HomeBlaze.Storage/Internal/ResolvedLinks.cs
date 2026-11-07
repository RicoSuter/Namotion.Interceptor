using System.Collections.Concurrent;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The links that widgets of one page resolved, for one version of the paths of the graph (see
/// <see cref="HomeBlaze.Services.SubjectPathResolver.PathsVersion"/>). Replaced as a whole when the version changes, so it
/// holds at most <paramref name="capacity"/> links and lives as long as its page.
/// </summary>
/// <remarks>
/// Safe for concurrent use: the version, base path and page name never change, and links are only added.
/// </remarks>
internal sealed class ResolvedLinks(long pathsVersion, string basePath, string pageName, int capacity)
{
    private readonly ConcurrentDictionary<string, string> _links = new(StringComparer.Ordinal);

    public long PathsVersion { get; } = pathsVersion;

    /// <summary>The base path of the page that relative links are resolved against, already escaped for a link.</summary>
    public string BasePath { get; } = basePath;

    /// <summary>The name of the page as a link writes it, already escaped for a link.</summary>
    public string PageName { get; } = pageName;

    public int Count => _links.Count;

    public bool TryGet(string link, out string resolved) => _links.TryGetValue(link, out resolved!);

    /// <summary>
    /// Keeps a resolved link unless the capacity is reached, so that links that change on every render, for example
    /// one bound to a value, cannot grow it. Concurrent adds can pass the check together and exceed it by a few.
    /// </summary>
    public void TryAdd(string link, string resolved)
    {
        if (_links.Count < capacity)
        {
            _links.TryAdd(link, resolved);
        }
    }
}
