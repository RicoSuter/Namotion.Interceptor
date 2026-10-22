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

    public HtmlSegment(string html, HtmlSegmentStart start = HtmlSegmentStart.None)
    {
        Html = html;
        Start = start;
    }
}

/// <summary>What an <see cref="HtmlSegment"/> starts among the top-level content of its page or block.</summary>
public enum HtmlSegmentStart
{
    /// <summary>Nothing: it goes on with the content before it, such as the text after an expression.</summary>
    None,

    /// <summary>A text block that can be edited, such as a paragraph, a heading, a list or a table.</summary>
    Text,

    /// <summary>Other content that cannot be edited as text, such as raw HTML, a style block or an error.</summary>
    Other
}
