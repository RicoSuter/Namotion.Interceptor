using System.Text;
using System.Web;
using HomeBlaze.Components.Abstractions.TimeZones;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>One markup string or one widget of a page or of a fragment.</summary>
public readonly record struct MarkdownRenderSegment(string? Html, IInterceptorSubject? Widget);

/// <summary>Joins the children of a page or of a fragment into the markup strings and widgets that render them.</summary>
public static class MarkdownMarkup
{
    /// <summary>
    /// Gets the markup strings and widgets of the children of a page or of a fragment, in order. The HTML and the
    /// values of expressions between two widgets are joined, except that a complete top-level block is markup of its own,
    /// which is the same string on every call while its segment is kept.
    /// </summary>
    /// <param name="children">The children of the page or of the fragment.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for the time zone of the server.</param>
    public static List<MarkdownRenderSegment> GetRenderSegments(IEnumerable<IInterceptorSubject> children, ITimeZoneDisplay? timeZone)
    {
        var result = new List<MarkdownRenderSegment>();
        StringBuilder? builder = null;

        foreach (var child in children)
        {
            if (child is HtmlSegment html)
            {
                AppendHtml(html, builder ??= new StringBuilder(), result);
            }
            else if (child is RenderExpression expression)
            {
                builder ??= new StringBuilder();
                if (expression.Error is not null)
                {
                    builder.Append("<span class=\"mud-error-text\" data-testid=\"expression-error\">");
                    builder.Append(HttpUtility.HtmlEncode(expression.Error));
                    builder.Append("</span>");
                }
                else
                {
                    builder.Append(HttpUtility.HtmlEncode(expression.GetText(timeZone)));
                }
            }
            else
            {
                // HTML and expressions are joined into one markup string so that an
                // expression can sit inside an HTML attribute.
                Flush(builder, result);
                result.Add(new MarkdownRenderSegment(null, child));
            }
        }

        Flush(builder, result);
        return result;
    }

    // Each top-level block is a markup string of its own, so a value change replaces only the block that shows it. Only
    // the parts at both ends can join an expression, so the parts between are added as they are.
    private static void AppendHtml(HtmlSegment html, StringBuilder builder, List<MarkdownRenderSegment> result)
    {
        var blocks = html.Blocks;
        for (var index = 0; index < blocks.Length; index++)
        {
            var joinsBefore = index == 0 && !html.StartsWithBlock;
            var joinsAfter = index == blocks.Length - 1 && !html.EndsWithBlock;
            if (!joinsBefore)
            {
                Flush(builder, result);
            }

            if (joinsBefore || joinsAfter)
            {
                builder.Append(blocks[index]);
            }
            else
            {
                result.Add(new MarkdownRenderSegment(blocks[index], null));
            }
        }

        if (html.EndsWithBlock)
        {
            Flush(builder, result);
        }
    }

    private static void Flush(StringBuilder? builder, List<MarkdownRenderSegment> result)
    {
        if (builder is { Length: > 0 })
        {
            result.Add(new MarkdownRenderSegment(builder.ToString(), null));
            builder.Clear();
        }
    }
}
