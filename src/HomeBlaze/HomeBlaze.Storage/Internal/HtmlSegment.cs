using System.Collections.Immutable;
using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Represents a raw HTML segment within a MarkdownFile. A viewed page renders it directly, without a component wrapper, to
/// allow split HTML tags; in edit mode a segment that starts a block of text renders that block in a wrapper of its own.
/// </summary>
[InterceptorSubject]
[ExcludeFrom(ExclusionTarget.All)]
[DisplayName("Text")]
public partial class HtmlSegment
{
    /// <summary>Gets the HTML of the segment, joined from <see cref="Blocks"/>.</summary>
    public string Html => Blocks.Length == 1 ? Blocks[0] : string.Concat((IEnumerable<string>)Blocks);

    /// <summary>What the HTML starts among the top-level content of its page or block.</summary>
    public HtmlSegmentStart Start { get; }

    /// <summary>
    /// The kind of the content that cannot be edited as text which the HTML starts, such as HTML, Style, Error or Footnotes, or
    /// null when the kind of its first element names it.
    /// </summary>
    public string? Kind { get; }

    /// <summary>
    /// Gets the HTML cut at its block starts (see <see cref="HtmlMarkdownSegment.BlockStarts"/>), without empty parts.
    /// The HTML between two block starts is a complete block. Without block starts, it is the HTML as one part.
    /// </summary>
    internal ImmutableArray<string> Blocks { get; }

    /// <summary>Gets whether a block starts with the first part of <see cref="Blocks"/>.</summary>
    internal bool StartsWithBlock { get; }

    /// <summary>Gets whether a block starts after the last part of <see cref="Blocks"/>.</summary>
    internal bool EndsWithBlock { get; }

    /// <summary>
    /// Gets the complete block that ends with the first part of <see cref="Blocks"/> and starts in a segment before it, joined
    /// from the parts of the segments in between, or null when the first part ends no such block. Set by the parser, which
    /// knows the segments before.
    /// </summary>
    internal string? JoinedBlock { get; set; }

    public HtmlSegment(
        string html, HtmlSegmentStart start = HtmlSegmentStart.None, string? kind = null, ImmutableArray<int> blockStarts = default)
    {
        Start = start;
        Kind = kind;
        if (blockStarts.IsDefaultOrEmpty)
        {
            Blocks = [html];
            return;
        }

        StartsWithBlock = blockStarts[0] == 0;
        EndsWithBlock = blockStarts[^1] == html.Length;

        var blocks = ImmutableArray.CreateBuilder<string>(blockStarts.Length + 1);
        var partStart = 0;
        for (var index = 0; index <= blockStarts.Length; index++)
        {
            var end = index < blockStarts.Length ? blockStarts[index] : html.Length;
            if (end > partStart)
            {
                // A part that is the whole HTML is the same string, not a copy.
                blocks.Add(html[partStart..end]);
            }

            partStart = end;
        }

        Blocks = blocks.ToImmutable();
    }
}

/// <summary>What an <see cref="HtmlSegment"/> starts among the top-level content of its page or block.</summary>
public enum HtmlSegmentStart
{
    /// <summary>Nothing: it goes on with the content before it, such as the text after an expression.</summary>
    None,

    /// <summary>A text block that can be edited, such as a paragraph, a heading, a list or a table.</summary>
    Text,

    /// <summary>Other content that cannot be edited as text and shows nothing, such as an HTML comment.</summary>
    Other,

    /// <summary>
    /// Content that cannot be edited as text but shows something, such as raw HTML, a style block, an error or the footnotes,
    /// which is edited in the source of the page.
    /// </summary>
    Source
}
