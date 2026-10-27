using System.Collections.Immutable;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Represents a raw HTML segment within a MarkdownFile.
/// Rendered directly without a component wrapper to allow split HTML tags.
/// </summary>
[InterceptorSubject]
[ExcludeFrom(ExclusionTarget.All)]
public partial class HtmlSegment
{
    /// <summary>Gets the HTML of the segment, joined from <see cref="Blocks"/>.</summary>
    internal string Html => Blocks.Length == 1 ? Blocks[0] : string.Concat((IEnumerable<string>)Blocks);

    /// <summary>
    /// Gets the HTML cut at its block starts (see <see cref="HtmlMarkdownSegment.BlockStarts"/>), without empty parts.
    /// The HTML between two block starts is a complete block. Without block starts, it is the HTML as one part.
    /// </summary>
    internal ImmutableArray<string> Blocks { get; }

    /// <summary>Gets whether a block starts with the first part of <see cref="Blocks"/>.</summary>
    internal bool StartsWithBlock { get; }

    /// <summary>Gets whether a block starts after the last part of <see cref="Blocks"/>.</summary>
    internal bool EndsWithBlock { get; }

    public HtmlSegment(string html, ImmutableArray<int> blockStarts = default)
    {
        if (blockStarts.IsDefaultOrEmpty)
        {
            Blocks = [html];
            return;
        }

        StartsWithBlock = blockStarts[0] == 0;
        EndsWithBlock = blockStarts[^1] == html.Length;

        var blocks = ImmutableArray.CreateBuilder<string>(blockStarts.Length + 1);
        var start = 0;
        for (var index = 0; index <= blockStarts.Length; index++)
        {
            var end = index < blockStarts.Length ? blockStarts[index] : html.Length;
            if (end > start)
            {
                // A part that is the whole HTML is the same string, not a copy.
                blocks.Add(html[start..end]);
            }

            start = end;
        }

        Blocks = blocks.ToImmutable();
    }
}
