using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>What parsing a page produces: its children and the lookups the page keeps for write-back and names.</summary>
public sealed record MarkdownParseResult(
    IDictionary<string, IInterceptorSubject> Children,
    MarkdownPage? Page,
    IReadOnlyDictionary<int, IInterceptorSubject> BlockSubjects,
    IReadOnlyDictionary<string, IInterceptorSubject> NamedSubjects)
{
    /// <summary>
    /// Gets a new empty result on every call. The children dictionary is mutable and becomes
    /// the children of a page, so two pages must never get the same one.
    /// </summary>
    public static MarkdownParseResult Empty => new(
        new Dictionary<string, IInterceptorSubject>(),
        null,
        new Dictionary<int, IInterceptorSubject>(),
        new Dictionary<string, IInterceptorSubject>());
}
