using HomeBlaze.Storage.Internal;
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

    /// <summary>A snapshot without text blocks, such as one that a write published before the read of its text.</summary>
    internal static readonly IReadOnlyDictionary<HtmlSegment, MarkdownTextBlock> NoTextBlocks =
        new Dictionary<HtmlSegment, MarkdownTextBlock>();

    /// <summary>The text block that each HTML segment of the parse starts, by the segment.</summary>
    internal IReadOnlyDictionary<HtmlSegment, MarkdownTextBlock> TextBlocks { get; init; } = NoTextBlocks;
}
