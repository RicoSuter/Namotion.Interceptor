using System.Globalization;
using System.Text;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Markdig.Extensions.Mathematics;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The rules shared by the operations that write Markdown files: local storage only, a size limit, plain file names,
/// unchanged blocks and no added active content.
/// </summary>
internal static class MarkdownWriteRules
{
    private static readonly char[] InvalidFileNameCharacters = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Whether the storage is a <see cref="FluentStorageContainer"/> on the local disk or in memory, or a folder of one.
    /// </summary>
    public static bool IsLocal(IStorageContainer storage) => storage switch
    {
        FluentStorageContainer container => container.IsLocal,
        VirtualFolder folder => IsLocal(folder.Storage),
        _ => false
    };

    /// <exception cref="InvalidOperationException">The storage is not local.</exception>
    public static void EnsureLocal(IStorageContainer storage)
    {
        if (!IsLocal(storage))
        {
            throw CreateNotLocalException();
        }
    }

    public static InvalidOperationException CreateNotLocalException() =>
        new("Only Markdown files in local storage (on disk or in memory) can be written; this storage is not local.");

    /// <summary>
    /// Encodes the content as UTF-8.
    /// </summary>
    /// <exception cref="ArgumentException">The content is null or larger than <see cref="MarkdownFile.MaxContentSize"/> bytes.</exception>
    public static byte[] GetContentBytes(string content, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(content, parameterName);

        var size = Encoding.UTF8.GetByteCount(content);
        if (size > MarkdownFile.MaxContentSize)
        {
            throw new ArgumentException(
                $"The Markdown source would be {size} bytes, more than the limit of {MarkdownFile.MaxContentSize} bytes.", parameterName);
        }

        return Encoding.UTF8.GetBytes(content);
    }

    private const string ActiveContentMessage =
        $"Blocks cannot be added, changed or removed, and {MarkdownFile.ActiveContent} cannot be added or changed through " +
        "these operations; use the page editor. A block starts with a line of two or more colons and ends with its closing " +
        "line, and text directly after a block without a closing line belongs to that block.";

    // The fenced code languages the diagrams extension renders as diagrams instead of code.
    private static readonly HashSet<string> DiagramLanguages = new(StringComparer.OrdinalIgnoreCase) { "mermaid", "nomnoml" };

    /// <summary>
    /// Ensures that the new source of a page keeps its blocks exactly as they are, in the same order, and adds none of the
    /// <see cref="MarkdownFile.ActiveContent"/> that the old source does not already contain. A new page passes an empty
    /// old source.
    /// </summary>
    /// <exception cref="ArgumentException">The new source adds, changes, removes or moves a block, or adds such content.</exception>
    public static void EnsureActiveContentUnchanged(string oldSource, string newSource, string parameterName)
    {
        var (oldBlocks, oldMarkup) = GetActiveContent(oldSource);
        var (newBlocks, newMarkup) = GetActiveContent(newSource);

        // Adding, changing, removing or moving a block would create, reconfigure or detach a running subject.
        if (!oldBlocks.SequenceEqual(newBlocks, StringComparer.Ordinal) || !IsContainedIn(newMarkup, oldMarkup))
        {
            throw new ArgumentException(ActiveContentMessage, parameterName);
        }
    }

    /// <summary>
    /// Collects what in a Markdown source makes the page run code or subjects, or load content, rather than show text,
    /// the way the page parses it: the blocks, each with its kind and its whole text, and the
    /// <see cref="MarkdownFile.ActiveContent"/>.
    /// </summary>
    internal static (List<string> Blocks, List<string> ActiveMarkup) GetActiveContent(string source)
    {
        var page = MarkdownPageParser.Parse(source);

        var blocks = new List<string>();
        CollectBlocks(page.Blocks, page.Source, GetLineEnds(page.Source), blocks);

        var activeMarkup = new List<string>();
        foreach (var node in page.Document.Descendants())
        {
            switch (node)
            {
                case HtmlBlock htmlBlock:
                    activeMarkup.Add("html:" + htmlBlock.Lines);
                    break;
                case HtmlInline htmlInline:
                    activeMarkup.Add("html:" + htmlInline.Tag);
                    break;
                // An image loads from its host when the page is shown, and media links render images and video or
                // audio URLs as players and frames of other sites.
                case LinkInline { IsImage: true } image:
                    activeMarkup.Add("image:" + image.Url);
                    break;
                case LinkInline { Url: { } url } when !IsSafeUrl(url):
                    activeMarkup.Add("url:" + url);
                    break;
                case MathBlock math:
                    activeMarkup.Add("math:" + math.Lines);
                    break;
                case FencedCodeBlock { Info: { } info } diagram when DiagramLanguages.Contains(info):
                    activeMarkup.Add("diagram:" + info + ":" + diagram.Lines);
                    break;
                case AutolinkInline { IsEmail: false } autolink when !IsSafeUrl(autolink.Url):
                    activeMarkup.Add("url:" + autolink.Url);
                    break;
                case LinkReferenceDefinition { Url: { } url } when !IsSafeUrl(url):
                    activeMarkup.Add("url:" + url);
                    break;
            }

            // Identifiers and classes only style the page; other attributes can hold script, for example onclick.
            if (node.TryGetAttributes() is { Properties: { Count: > 0 } properties })
            {
                activeMarkup.Add("attributes:" + string.Join(" ", properties.Select(property => property.Key + "=" + property.Value)));
            }
        }

        return (blocks, activeMarkup);
    }

    /// <summary>
    /// Adds each block with its kind and its text from the head line to its last line, which includes its configuration,
    /// its content and the blocks inside it. A block in a list item or a quote is added like a block directly on the page.
    /// </summary>
    private static void CollectBlocks(IReadOnlyList<MarkdownBlock> blocks, string source, List<int> lineEnds, List<string> collected)
    {
        foreach (var block in blocks)
        {
            var end = lineEnds[Math.Clamp(block.EndLine, 1, lineEnds.Count) - 1];
            var start = Math.Min(block.HeadSpan.Start, end);
            collected.Add(block.Kind + ":" + source[start..end]);
        }
    }

    // The position after the last character of each line, before its line ending, by 0-based line.
    private static List<int> GetLineEnds(string source)
    {
        var lineEnds = new List<int>();
        for (var lineFeed = source.IndexOf('\n'); lineFeed >= 0; lineFeed = source.IndexOf('\n', lineFeed + 1))
        {
            lineEnds.Add(lineFeed > 0 && source[lineFeed - 1] == '\r' ? lineFeed - 1 : lineFeed);
        }

        lineEnds.Add(source.Length);
        return lineEnds;
    }

    /// <summary>
    /// Whether a link target is relative or uses http, https or mailto. Browsers ignore whitespace and control
    /// characters in a scheme, so they are removed before it is compared.
    /// </summary>
    private static bool IsSafeUrl(string url)
    {
        var colon = url.IndexOf(':');
        if (colon < 0 || url.AsSpan(0, colon).IndexOfAny('/', '?', '#') >= 0)
        {
            return true;
        }

        var scheme = new string(url.AsSpan(0, colon).ToArray().Where(character => !char.IsWhiteSpace(character) && !char.IsControl(character)).ToArray());
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);
    }

    // Whether every item of the new list has its own occurrence in the old list.
    private static bool IsContainedIn(List<string> newItems, List<string> oldItems)
    {
        var available = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in oldItems)
        {
            available[item] = available.GetValueOrDefault(item) + 1;
        }

        foreach (var item in newItems)
        {
            if (available.GetValueOrDefault(item) == 0)
            {
                return false;
            }

            available[item]--;
        }

        return true;
    }

    /// <exception cref="ArgumentException">The name is not a plain file name ending in .md that becomes a visible page.</exception>
    public static void ValidateFileName(string name, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(name, parameterName);

        var isValid =
            name.Length > FileExtensions.Markdown.Length &&
            name.Length <= 255 &&
            name.EndsWith(FileExtensions.Markdown, StringComparison.OrdinalIgnoreCase) &&
            name == name.Trim() &&
            !name.StartsWith('.') &&
            name.IndexOfAny(InvalidFileNameCharacters) < 0 &&
            !name.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format) &&
            !StoragePathFilter.IsTemporaryFile(name);

        if (!isValid)
        {
            throw new ArgumentException(
                $"'{name}' is not a valid Markdown file name: use a plain file name ending in .md, without folders, " +
                "a leading dot, invisible formatting characters or any of the characters / \\ : * ? \" < > |.", parameterName);
        }
    }
}
