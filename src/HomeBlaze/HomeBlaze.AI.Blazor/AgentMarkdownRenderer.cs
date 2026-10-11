using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HomeBlaze.AI.Blazor;

/// <summary>
/// Renders untrusted agent output from Markdown to HTML that is safe to insert into the page: raw HTML is escaped,
/// links with a target other than an absolute http, https or mailto URL are reduced to their text, and images are
/// rendered as links so nothing loads without a click.
/// </summary>
public static class AgentMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();

    /// <summary>Converts Markdown to sanitized HTML.</summary>
    public static string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            SanitizeLink(link);
        }

        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            SanitizeAutolink(autolink);
        }

        // The parser turns the info string of a fenced code block into a class name.
        foreach (var codeBlock in document.Descendants<FencedCodeBlock>())
        {
            codeBlock.TryGetAttributes()?.Classes?.Clear();
        }

        return document.ToHtml(Pipeline);
    }

    // An allow list: extensions that copy text into class, id or style attributes (custom containers, generic
    // attributes, footnotes, heading identifiers) or embed content (media links) must stay out, because a class
    // can apply page styles such as a full-viewport overlay and repeated ids collide across timeline entries. The
    // remaining extensions only emit fixed attributes, such as a table column's text alignment.
    private static MarkdownPipeline CreatePipeline() => new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .UseListExtras()
        .DisableHtml()
        .Build();

    private static void SanitizeLink(LinkInline link)
    {
        // An image would load its URL without a click, which output steered by graph values could use to send
        // data away, so it is shown as a link labelled with its alternative text instead.
        if (link.IsImage)
        {
            link.IsImage = false;
            if (link.FirstChild is null)
            {
                link.AppendChild(new LiteralInline(link.Url ?? string.Empty));
            }
        }

        link.GetDynamicUrl = null;
        if (TryGetSafeUrl(link.Url) is not { } url)
        {
            UnwrapLink(link);
            return;
        }

        link.Url = url;
        AddExternalLinkAttributes(link);
    }

    private static void UnwrapLink(LinkInline link)
    {
        var child = link.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            child.Remove();
            link.InsertBefore(child);
            child = next;
        }

        link.Remove();
    }

    private static void SanitizeAutolink(AutolinkInline autolink)
    {
        var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
        if (TryGetSafeUrl(url) is null)
        {
            autolink.ReplaceBy(new LiteralInline(autolink.Url));
            return;
        }

        AddExternalLinkAttributes(autolink);
    }

    private static string? TryGetSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto))
        {
            return null;
        }

        // The normalized form is rendered, so a browser cannot read the original text as a different target.
        return uri.AbsoluteUri;
    }

    private static void AddExternalLinkAttributes(Inline link)
    {
        var attributes = link.GetAttributes();
        attributes.AddPropertyIfNotExist("target", "_blank");
        attributes.AddPropertyIfNotExist("rel", "noopener noreferrer nofollow");
    }
}
