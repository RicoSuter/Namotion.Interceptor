using System.Text;
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
/// HTML, pictures, blocks or expressions, and links only to http, https or mailto addresses or to relative paths in this
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
        "http, https or mailto addresses or to relative paths in this application.";

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
            return HasLinkToOtherHost(notes) || HasAttributes(notes) ? RulesMessage : null;
        }
        catch (ArgumentException)
        {
            return RulesMessage;
        }
    }

    // Browsers read a link that starts with two slashes or backslashes, ignoring whitespace and control characters, as
    // one to another host.
    private static bool HasLinkToOtherHost(string notes)
        => MarkdownPageParser.Parse(notes).Document.Descendants().Any(static node => node switch
        {
            LinkInline { Url: { } url } => IsToOtherHost(url),
            LinkReferenceDefinition { Url: { } url } => IsToOtherHost(url),
            _ => false
        });

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
    /// Converts the notes to HTML; null for notes that break the rules, such as after an edit of the file by hand,
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
            return Markdown.ToHtml(notes, Pipeline);
        }
        catch (ArgumentException)
        {
            // Thrown for Markdown nested beyond the depth limit of Markdig.
            return null;
        }
    }
}
