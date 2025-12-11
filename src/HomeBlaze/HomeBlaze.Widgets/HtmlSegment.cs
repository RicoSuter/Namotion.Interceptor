using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Widgets;

/// <summary>
/// Represents a raw HTML segment within a MarkdownFile.
/// Rendered directly without a component wrapper to allow split HTML tags.
/// </summary>
[InterceptorSubject]
public partial class HtmlSegment
{
    public string Html { get; }

    public HtmlSegment(string html)
    {
        Html = html;
    }
}
