using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.GenericAttributes;
using Markdig.Extensions.MediaLinks;
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

        return document.ToHtml(Pipeline);
    }

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml();

        // Generic attributes let the text set any HTML attribute, including event handlers; media links embed
        // iframes; heading identifiers would repeat across timeline entries.
        builder.Extensions.TryRemove<GenericAttributesExtension>();
        builder.Extensions.TryRemove<MediaLinkExtension>();
        builder.Extensions.TryRemove<AutoIdentifierExtension>();
        return builder.Build();
    }

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
