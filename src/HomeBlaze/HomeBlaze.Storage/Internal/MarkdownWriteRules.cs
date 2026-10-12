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
        "line, and text directly after a block without a closing line belongs to that block. Keep each block and each old " +
        "subject(name) code block exactly as ReadRawContent shows it, with *** where it hides a secret value.";

    private const string BlockPositionMessage =
        "The change adds or removes an expression, or the first text, before a block without a #name, which would create " +
        "the subject of that block again. Change the text elsewhere or keep the expressions and paragraphs before such a " +
        "block, or ask an operator to give the block a #name in the page editor.";

    private const string SubjectFenceKind = "SubjectFence";

    // The attribute that the globalization extension sets on an element with right-to-left text.
    private const string DirectionAttribute = "dir";

    // The fenced code languages the diagrams extension renders as diagrams instead of code.
    private static readonly HashSet<string> DiagramLanguages = new(StringComparer.OrdinalIgnoreCase) { "mermaid", "nomnoml" };

    /// <summary>
    /// Ensures that the new source of a page keeps its blocks and old subject(name) code blocks exactly as they are, in the
    /// same order and each block without a #name directly on the page at the same position among the content of the
    /// page, and adds none of the
    /// <see cref="MarkdownFile.ActiveContent"/> that the old source does not already contain. A new page passes an empty
    /// old source.
    /// </summary>
    /// <exception cref="ArgumentException">The new source adds, changes, removes or moves a block, or adds such content.</exception>
    public static void EnsureActiveContentUnchanged(string oldSource, string newSource, string parameterName)
    {
        var oldContent = GetActiveContent(oldSource);
        var newContent = GetActiveContent(newSource);

        // Adding, changing, removing or moving a block would create, reconfigure or detach a running subject.
        if (!oldContent.Units.Select(unit => unit.Text).SequenceEqual(newContent.Units.Select(unit => unit.Text), StringComparer.Ordinal) ||
            !IsContainedIn(newContent.ActiveMarkup, oldContent.ActiveMarkup))
        {
            throw new ArgumentException(ActiveContentMessage, parameterName);
        }

        // The page knows such a block by its type and its position, so a new position would create its subject again.
        if (!oldContent.UnnamedBlockPositions.SequenceEqual(newContent.UnnamedBlockPositions))
        {
            throw new ArgumentException(BlockPositionMessage, parameterName);
        }
    }

    /// <summary>
    /// Gets the source to write for a page whose current source is <paramref name="source"/> when a change was made to
    /// <paramref name="redactedSource"/>, the view of the page with secret values hidden by <paramref name="redactions"/>,
    /// and gave <paramref name="newContent"/>. Each block and old subject(name) code block must be exactly as in the view
    /// and gets its text from the source back, so a hidden value is kept and a guessed one is not accepted.
    /// </summary>
    /// <exception cref="ArgumentException">The new content changes such a unit of the view or breaks another rule of
    /// <see cref="EnsureActiveContentUnchanged"/>.</exception>
    public static string RestoreBlocks(
        string source,
        string redactedSource,
        IReadOnlyList<EmbeddedSubjectRedaction.Redaction> redactions,
        string newContent,
        string parameterName)
    {
        // Every redaction is inside a unit, so a unit of the source maps to the view by the redactions before its end.
        var units = GetUnits(MarkdownPageParser.Parse(source));
        var newUnits = GetUnits(MarkdownPageParser.Parse(newContent));
        if (units.Count != newUnits.Count)
        {
            throw new ArgumentException(ActiveContentMessage, parameterName);
        }

        for (var index = 0; index < units.Count; index++)
        {
            var start = MapToView(units[index].Span.Start, redactions);
            var end = MapToView(units[index].Span.End, redactions);
            var viewText = units[index].Kind + ":" + NormalizeLineEndings(redactedSource[start..end]);
            if (!string.Equals(viewText, newUnits[index].Text, StringComparison.Ordinal))
            {
                throw new ArgumentException(ActiveContentMessage, parameterName);
            }
        }

        var restored = new StringBuilder(newContent.Length);
        var copied = 0;
        for (var index = 0; index < newUnits.Count; index++)
        {
            var newSpan = newUnits[index].Span;
            var span = units[index].Span;
            restored.Append(newContent, copied, newSpan.Start - copied).Append(source, span.Start, span.Length);
            copied = newSpan.End;
        }

        var result = restored.Append(newContent, copied, newContent.Length - copied).ToString();
        EnsureActiveContentUnchanged(source, result, parameterName);

        // The size of the content was checked before, so only the hidden values can make the result too large, and the
        // message must not tell by how much.
        if (Encoding.UTF8.GetByteCount(result) > MarkdownFile.MaxContentSize)
        {
            throw new ArgumentException(
                $"The Markdown source would be larger than the limit of {MarkdownFile.MaxContentSize} bytes.", parameterName);
        }

        return result;
    }

    private static int MapToView(int position, IReadOnlyList<EmbeddedSubjectRedaction.Redaction> redactions)
    {
        var shift = 0;
        foreach (var redaction in redactions)
        {
            if (redaction.Source.End > position)
            {
                break;
            }

            shift += redaction.Redacted.Length - redaction.Source.Length;
        }

        return position + shift;
    }

    /// <summary>
    /// Collects what in a Markdown source makes the page run code or subjects, or load content, rather than show text,
    /// the way the page parses it: the blocks, each with its kind and its whole text, the positions of the blocks without
    /// a #name directly on the page, and the <see cref="MarkdownFile.ActiveContent"/>.
    /// </summary>
    private static ActiveContentOfPage GetActiveContent(string source)
    {
        var page = MarkdownPageParser.Parse(source);
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

            // Identifiers and classes only style the page, and the globalization extension sets dir for right-to-left
            // text; other attributes can hold script, for example onclick.
            if (node.TryGetAttributes() is { Properties: { Count: > 0 } properties } &&
                properties.Any(property => property.Key != DirectionAttribute))
            {
                activeMarkup.Add("attributes:" + string.Join(" ", properties.Select(property => property.Key + "=" + property.Value)));
            }
        }

        return new ActiveContentOfPage(GetUnits(page), GetUnnamedBlockPositions(page), activeMarkup);
    }

    /// <summary>
    /// Gets the position among the content of the page of each widget block without a #name directly on the page, the
    /// position that its key holds.
    /// </summary>
    private static List<int> GetUnnamedBlockPositions(MarkdownPage page)
    {
        var positions = new List<int>();
        var segments = MarkdownSegmentBuilder.Build(page);
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index] is BlockMarkdownSegment { Block: { Kind: MarkdownBlockKind.Widget, Head.Id: null } })
            {
                positions.Add(index);
            }
        }

        return positions;
    }

    /// <summary>
    /// Gets the units that must stay as they are, in order: each block with its kind and its text from the head line to
    /// its last line, which includes its configuration, its content and the blocks inside it, and each fenced code block
    /// of the old subject(name) form outside of them, up to its closing fence or the end of the page. A block in a list
    /// item or a quote is taken like a block directly on the page. The text is compared with line feeds as line endings.
    /// </summary>
    private static List<UnitText> GetUnits(MarkdownPage page)
    {
        var source = page.Source;
        var lineEnds = GetLineEnds(source);
        var units = new List<UnitText>(page.Blocks.Count);
        foreach (var block in page.Blocks)
        {
            var end = lineEnds[Math.Clamp(block.EndLine, 1, lineEnds.Count) - 1];
            var start = Math.Min(block.HeadSpan.Start, end);
            units.Add(new UnitText(block.Kind.ToString(), block.Kind + ":" + NormalizeLineEndings(source[start..end]), new MarkdownSpan(start, end - start)));
        }

        var blockCount = units.Count;
        foreach (var codeBlock in page.Document.Descendants<FencedCodeBlock>())
        {
            var start = codeBlock.Span.Start;
            if (MarkdownSegmentBuilder.IsOldSubjectFence(codeBlock) &&
                !units.Take(blockCount).Any(unit => unit.Span.Start <= start && start < unit.Span.End))
            {
                var end = MarkdownPageParser.GetCodeBlockEnd(source, codeBlock);
                units.Add(new UnitText(SubjectFenceKind, SubjectFenceKind + ":" + NormalizeLineEndings(source[start..end]), new MarkdownSpan(start, end - start)));
            }
        }

        units.Sort(static (left, right) => left.Span.Start.CompareTo(right.Span.Start));
        return units;
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

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

    /// <summary>The kind of a unit, its text for the comparison and its span in its source.</summary>
    private readonly record struct UnitText(string Kind, string Text, MarkdownSpan Span);

    private sealed record ActiveContentOfPage(List<UnitText> Units, List<int> UnnamedBlockPositions, List<string> ActiveMarkup);
}
