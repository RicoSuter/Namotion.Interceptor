using System.Text;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.GenericAttributes;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HomeBlaze.Storage;

/// <summary>
/// The <c>notes</c> of metadata: Markdown under the rules that the write operations of pages apply to new content, no
/// HTML, pictures, blocks or expressions, and links only to http, https or mailto addresses or to the routes of this
/// application, <c>/</c>, <c>/browser</c>, <c>/browser/...</c> and <c>/pages/...</c>, without <c>.</c> or <c>..</c>
/// segments; and without attributes, also no identifier or class, as notes are shown inside the page of the
/// application. Braces that give none, such as <c>{low}</c>, and footnotes are text.
/// </summary>
public static class MetadataNotes
{
    /// <summary>The largest notes, in bytes of UTF-8.</summary>
    public const int MaxSize = 4 * 1024;

    // The pipeline of pages without attributes, footnotes and the identifiers that it gives headings, so that notes put
    // no identifier, class or link to a fragment on the page that shows them.
    private static readonly MarkdownPipeline Pipeline = CreatePipeline(withAttributes: false);

    // The same with attributes, to find the attributes that notes give.
    private static readonly MarkdownPipeline AttributesPipeline = CreatePipeline(withAttributes: true);

    /// <summary>Gets why the notes are refused, naming the rule they break, or null when they may be written.</summary>
    public static string? GetError(string notes)
    {
        if (Encoding.UTF8.GetByteCount(notes) > MaxSize)
        {
            return $"notes are larger than {MaxSize / 1024} KB.";
        }

        if (notes.Contains("{{", StringComparison.Ordinal))
        {
            return "notes may not hold {{ }} expressions.";
        }

        try
        {
            var document = Markdown.Parse(notes, Pipeline);
            if (document.Descendants().Any(static node => node is HtmlBlock or HtmlInline))
            {
                return "notes may not hold HTML.";
            }

            if (document.Descendants<LinkInline>().Any(static link => link.IsImage))
            {
                return "notes may not hold pictures.";
            }

            if (GetLinkNotAllowed(document) is { } url)
            {
                return $"notes may not link to '{url}': links lead only to http, https or mailto addresses or to the routes " +
                    "of this application, /, /browser/... and /pages/..., such as /pages/Docs/Setup.md.";
            }

            if (HasAttributes(notes, document))
            {
                return "notes may not give attributes, such as {#id}, {.class} or {key=value}.";
            }

            MarkdownWriteRules.EnsureNoActiveContent(notes, Pipeline, nameof(notes));
            return null;
        }
        catch (ArgumentException)
        {
            // Thrown for a block or other active content of pages, and for Markdown nested beyond the depth limit of Markdig.
            return "notes may not hold blocks, math, diagrams, abbreviations or Markdown nested too deep.";
        }
    }

    // A relative path or a fragment would resolve against the address of the application rather than that of the page
    // that shows the notes, and a path that is no route of the application leads to no page.
    private static string? GetLinkNotAllowed(MarkdownDocument document)
    {
        foreach (var node in document.Descendants())
        {
            var url = node switch
            {
                LinkInline link => link.Url ?? string.Empty,
                LinkReferenceDefinition { Url: { } definitionUrl } => definitionUrl,
                AutolinkInline { IsEmail: false } link => link.Url,
                _ => null
            };
            if (url is not null && !IsAllowedLink(url))
            {
                return url;
            }
        }

        return null;
    }

    private static bool IsAllowedLink(string url)
        => url is "/" or "/browser" ||
           (url.StartsWith("/browser/", StringComparison.Ordinal) || url.StartsWith("/pages/", StringComparison.Ordinal)) && IsPlainRoute(url) ||
           MetadataLink.IsAllowedAddress(url);

    // A dot segment or a backslash, which a browser reads as a slash, also percent-encoded, would lead out of the route.
    private static bool IsPlainRoute(string url)
    {
        var end = url.AsSpan().IndexOfAny('?', '#');
        var path = Uri.UnescapeDataString(end < 0 ? url : url[..end]);
        return !url.Contains('\\') && !path.Contains('\\') && !path.Split('/').Any(static segment => segment is "." or "..");
    }

    // Extensions set attributes as well, such as the classes of a task list, so attributes are given by the notes where
    // they differ from those without the attributes extension. Braces with only names, such as {low}, give no identifier,
    // class or value and are text in the notes as shown.
    private static bool HasAttributes(string notes, MarkdownDocument document)
        => !GetAttributes(Markdown.Parse(notes, AttributesPipeline)).SequenceEqual(GetAttributes(document), StringComparer.Ordinal);

    private static IEnumerable<string> GetAttributes(MarkdownDocument document)
        => document.Descendants()
            .Select(static node => node.TryGetAttributes())
            .OfType<HtmlAttributes>()
            .Select(static attributes => string.Join(" ",
                attributes.Id,
                string.Join(".", attributes.Classes ?? []),
                string.Join(",", attributes.Properties?.Where(static property => property.Value is not null)
                    .Select(static property => property.Key + "=" + property.Value) ?? [])))
            .Where(static attributes => !string.IsNullOrWhiteSpace(attributes));

    private static MarkdownPipeline CreatePipeline(bool withAttributes)
    {
        var builder = MarkdownPageParser.CreatePipelineBuilder();
        builder.Extensions.TryRemove<AutoIdentifierExtension>();
        builder.Extensions.TryRemove<FootnoteExtension>();
        if (!withAttributes)
        {
            builder.Extensions.TryRemove<GenericAttributesExtension>();
        }

        return builder.Build();
    }

    /// <summary>
    /// Converts the notes to HTML, with a link to a web address that opens in a new tab without opener and referrer;
    /// null for notes that break the rules, such as after an edit of the file by hand,
    /// which are to be shown as text instead.
    /// </summary>
    public static string? ToHtml(string notes)
    {
        if (GetError(notes) is not null)
        {
            return null;
        }

        try
        {
            var document = Markdown.Parse(notes, Pipeline);
            foreach (var node in document.Descendants())
            {
                var url = node switch
                {
                    LinkInline link => link.Url,
                    AutolinkInline { IsEmail: false } autolink => autolink.Url,
                    _ => null
                };
                if (url is not null && MetadataLink.IsWebAddress(url))
                {
                    var attributes = node.GetAttributes();
                    attributes.AddPropertyIfNotExist("target", "_blank");
                    attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
                }
            }

            return document.ToHtml(Pipeline);
        }
        catch (ArgumentException)
        {
            // Thrown for Markdown nested beyond the depth limit of Markdig.
            return null;
        }
    }

    /// <summary>
    /// Gets the notes to show as text, as notes that <see cref="ToHtml"/> does not convert are: the first
    /// <see cref="MaxSize"/> bytes of UTF-8 of them, ending at a whole character.
    /// </summary>
    public static string ToText(string notes)
    {
        if (Encoding.UTF8.GetByteCount(notes) <= MaxSize)
        {
            return notes;
        }

        var length = 0;
        var size = 0;
        while (length < notes.Length)
        {
            var count = char.IsSurrogatePair(notes, length) ? 2 : 1;
            size += Encoding.UTF8.GetByteCount(notes.AsSpan(length, count));
            if (size > MaxSize)
            {
                break;
            }

            length += count;
        }

        return notes[..length];
    }
}
