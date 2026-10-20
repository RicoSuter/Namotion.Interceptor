using System.Text;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage;

/// <summary>
/// The <c>notes</c> of metadata: Markdown under the rules that the write operations of pages apply to new content, no
/// HTML, pictures, blocks, element attributes or expressions, and links only to http, https, mailto or relative
/// addresses.
/// </summary>
public static class MetadataNotes
{
    /// <summary>The largest notes, in bytes of UTF-8.</summary>
    public const int MaxSize = 4 * 1024;

    private const string RulesMessage =
        "notes hold Markdown text without HTML, pictures, blocks, element attributes or {{ }} expressions, and links only to " +
        "http, https or mailto addresses or to paths in this application.";

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
            return null;
        }
        catch (ArgumentException)
        {
            return RulesMessage;
        }
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
            return Markdig.Markdown.ToHtml(notes, MarkdownPageParser.Pipeline);
        }
        catch (ArgumentException)
        {
            // Thrown for Markdown nested beyond the depth limit of Markdig.
            return null;
        }
    }
}
