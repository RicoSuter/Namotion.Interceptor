using System.Text;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.GenericAttributes;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HomeBlaze.Storage;

/// <summary>
/// The <c>notes</c> of metadata: Markdown under the rules that the write operations of pages apply to new content, no
/// HTML, pictures, blocks or expressions, and links only to http, https or mailto addresses or to absolute paths in this
/// application; and without any attributes, also no identifier or class, as notes are shown inside the page of the
/// application.
/// </summary>
public static class MetadataNotes
{
    /// <summary>The largest notes, in bytes of UTF-8.</summary>
    public const int MaxSize = 4 * 1024;

    // The pipeline of pages without attributes and without the identifiers that it gives headings, so that notes put no
    // identifier or class of the application on the page that shows them.
    private static readonly MarkdownPipeline Pipeline = CreatePipeline(withAttributes: false);

    // The same with attributes, to find the attributes that notes give.
    private static readonly MarkdownPipeline AttributesPipeline = CreatePipeline(withAttributes: true);

    private const string RulesMessage =
        "notes hold Markdown text without HTML, pictures, blocks, element attributes or {{ }} expressions, and links only to " +
        "http, https or mailto addresses or to absolute paths in this application, such as /pages/Docs/Setup.md.";

    /// <summary>Gets why the notes are refused, or null when they may be written.</summary>
    public static string? GetError(string notes)
    {
        if (Encoding.UTF8.GetByteCount(notes) > MaxSize)
        {
            return $"notes are larger than {MaxSize / 1024} KB.";
        }

        if (notes.Contains("{{", StringComparison.Ordinal))
        {
            return RulesMessage;
        }

        try
        {
            MarkdownWriteRules.EnsureActiveContentUnchanged(string.Empty, notes, nameof(notes));
            return HasLinkNotAllowed(notes) || HasAttributes(notes) ? RulesMessage : null;
        }
        catch (ArgumentException)
        {
            return RulesMessage;
        }
    }

    // A relative path or a fragment would resolve against the address of the application rather than that of the page
    // that shows the notes. Browsers read a path that starts with two slashes or backslashes, ignoring whitespace and
    // control characters, as one to another host.
    private static bool HasLinkNotAllowed(string notes)
        => Markdown.Parse(notes, Pipeline).Descendants().Any(static node => node switch
        {
            LinkInline { Url: var url } => !IsAllowedLink(url),
            LinkReferenceDefinition { Url: { } url } => !IsAllowedLink(url),
            AutolinkInline { IsEmail: false, Url: var url } => !IsAllowedLink(url),
            _ => false
        });

    private static bool IsAllowedLink(string? url)
        => url is not null && (url.StartsWith('/') ? !IsToOtherHost(url) : MetadataLink.IsAllowedAddress(url));

    private static bool IsToOtherHost(string url)
        => url.Where(static character => !char.IsWhiteSpace(character) && !char.IsControl(character))
              .Take(2)
              .Count(static character => character is '/' or '\\') == 2;

    // Extensions set attributes as well, such as the classes of a task list, so attributes are given by the notes where
    // they differ from those without the attributes extension.
    private static bool HasAttributes(string notes)
        => !GetAttributes(Markdown.Parse(notes, AttributesPipeline)).SequenceEqual(GetAttributes(Markdown.Parse(notes, Pipeline)), StringComparer.Ordinal);

    private static IEnumerable<string> GetAttributes(MarkdownDocument document)
        => document.Descendants()
            .Select(static node => node.TryGetAttributes())
            .OfType<HtmlAttributes>()
            .Select(static attributes => string.Join(" ",
                attributes.Id,
                string.Join(".", attributes.Classes ?? []),
                string.Join(",", attributes.Properties?.Select(static property => property.Key + "=" + property.Value) ?? [])));

    private static MarkdownPipeline CreatePipeline(bool withAttributes)
    {
        var builder = MarkdownPageParser.CreatePipelineBuilder();
        builder.Extensions.TryRemove<AutoIdentifierExtension>();
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
