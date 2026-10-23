using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Represents a raw HTML segment within a MarkdownFile.
/// Rendered directly without a component wrapper to allow split HTML tags.
/// </summary>
[InterceptorSubject]
[ExcludeFrom(ExclusionTarget.All)]
[DisplayName("Text")]
public partial class HtmlSegment
{
    public string Html { get; }

    /// <summary>What the HTML starts among the top-level content of its page or block.</summary>
    public HtmlSegmentStart Start { get; }

    /// <summary>
    /// The kind of the content that cannot be edited as text which the HTML starts, such as HTML, Style, Error or Footnotes, or
    /// null when the kind of its first element names it.
    /// </summary>
    public string? Kind { get; }

    public HtmlSegment(string html, HtmlSegmentStart start = HtmlSegmentStart.None, string? kind = null)
    {
        Html = html;
        Start = start;
        Kind = kind;
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
