using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>What parsing a page produces: its children and the lookups the page keeps for write-back and names.</summary>
public sealed record MarkdownParseResult(
    IDictionary<string, IInterceptorSubject> Children,
    MarkdownPage? Page,
    IReadOnlyDictionary<int, IInterceptorSubject> BlockSubjects,
    IReadOnlyDictionary<string, IInterceptorSubject> NamedSubjects)
{
    public static MarkdownParseResult Empty { get; } = new(
        new Dictionary<string, IInterceptorSubject>(),
        null,
        new Dictionary<int, IInterceptorSubject>(),
        new Dictionary<string, IInterceptorSubject>());
}
