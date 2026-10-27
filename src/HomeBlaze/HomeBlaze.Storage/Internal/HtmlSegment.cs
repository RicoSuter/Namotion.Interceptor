using System.Collections.Immutable;
using HomeBlaze.Abstractions.Attributes;
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
    public string Html { get; }

    /// <summary>
    /// Gets the offsets into <see cref="Html"/>, in ascending order, at which a top-level block of the page starts, so that
    /// the view can render the HTML from there on as markup of its own. Default when there is none.
    /// </summary>
    public ImmutableArray<int> BlockStarts { get; }

    public HtmlSegment(string html, ImmutableArray<int> blockStarts = default)
    {
        Html = html;
        BlockStarts = blockStarts.IsDefaultOrEmpty ? default : blockStarts;
    }

    private string?[]? _completeBlocks;

    /// <summary>
    /// Gets the HTML from the block start at <paramref name="index"/> up to the next block start, which is a complete
    /// block, as the same string on every call.
    /// </summary>
    public string GetCompleteBlock(int index)
    {
        // A race only creates a string twice.
        var blocks = _completeBlocks ??= new string?[BlockStarts.Length - 1];
        return blocks[index] ??= Html[BlockStarts[index]..BlockStarts[index + 1]];
    }
}
