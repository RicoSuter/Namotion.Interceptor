using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Markdig.Extensions.Abbreviations;
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
    /// Whether the path, relative to the storage, is in the documentation folder of the data directory, which is
    /// replaced from the image on every start.
    /// </summary>
    public static bool IsInDocumentationFolder(IStorageContainer storage, string path) => storage switch
    {
        FluentStorageContainer container => container.IsPathInDocumentationFolder(path),
        VirtualFolder folder => IsInDocumentationFolder(folder.Storage, path),
        _ => false
    };

    /// <exception cref="ArgumentException">The path is in the documentation folder.</exception>
    public static void EnsureNotInDocumentationFolder(IStorageContainer storage, string path)
    {
        // An argument error, so that its message reaches the caller of the operation, like the other rules of a write.
        if (IsInDocumentationFolder(storage, path))
        {
            throw new ArgumentException(MarkdownFile.DocumentationFolderMessage);
        }
    }

    /// <summary>
    /// Refuses a write that comes from work on the worker of the storage, such as the load of the page: the write would
    /// wait for the write lock of a write that waits for that work, and neither would ever end.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from work on the worker of the storage.</exception>
    public static void EnsureNotWithinStorageWork(IStorageContainer storage)
    {
        var container = storage switch
        {
            FluentStorageContainer fluentContainer => fluentContainer,
            VirtualFolder { Storage: FluentStorageContainer fluentContainer } => fluentContainer,
            _ => null
        };

        if (container is { IsWithinRunningOperation: true })
        {
            throw new InvalidOperationException(StorageWorker.CallFromWithinItemMessage);
        }
    }

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
        "The change adds or removes an expression, the first text, or text between two blocks before a block without a " +
        "#name, which would create the subject of that block again. Change the text elsewhere or keep the expressions and " +
        "the text before such a block where they are, or ask an operator to give the block a #name in the page editor.";

    private const string LayoutMessage =
        "With the permission to edit page layout, only layout blocks can be added, changed, removed or moved: cards, tabs, " +
        "tab and flow layouts, style blocks and Widget references to existing subjects, with single values of their " +
        "configuration on the head line or in the json block. Every other block, and each old subject(name) code block, " +
        "must stay exactly as ReadRawContent shows it, with *** where it hides a secret value, inside the same blocks and " +
        $"at the same position among the content around it, and {MarkdownFile.ActiveContent} cannot be added or changed.";

    private const string SubjectFenceKind = "SubjectFence";

    // The attribute that the globalization extension sets on an element with right-to-left text.
    private const string DirectionAttribute = "dir";

    // The fenced code languages the diagrams extension renders as diagrams instead of code.
    private static readonly HashSet<string> DiagramLanguages = new(StringComparer.OrdinalIgnoreCase) { "mermaid", "nomnoml" };

    /// <summary>
    /// Ensures that the new source of a page keeps its blocks and old subject(name) code blocks exactly as they are, in the
    /// same order and each block without a #name at the same position among the content around it, and adds none of the
    /// <see cref="MarkdownFile.ActiveContent"/> that the old source does not already contain. A new page passes an empty
    /// old source. With <paramref name="layout"/>, layout blocks (<see cref="MarkdownPageLayout"/>) may change, every
    /// other block must stay as it is, inside the same blocks and at the same position, and each reference path of a
    /// layout block must lead to a subject.
    /// </summary>
    /// <exception cref="ArgumentException">The new source adds, changes, removes or moves a block, or adds such content.</exception>
    public static void EnsureActiveContentUnchanged(string oldSource, string newSource, string parameterName, PageLayoutRules? layout = null)
    {
        var oldContent = GetActiveContent(MarkdownPageParser.Parse(oldSource), layout);
        var newPage = MarkdownPageParser.Parse(newSource);
        var newContent = GetActiveContent(newPage, layout);

        if (layout is not null)
        {
            // The units are kept by their place among the blocks, not by their order on the page, so layout can move.
            if (!GetUnitKeys(oldContent.Units).SequenceEqual(GetUnitKeys(newContent.Units), StringComparer.Ordinal) ||
                !IsContainedIn(newContent.ActiveMarkup, oldContent.ActiveMarkup))
            {
                throw new ArgumentException(LayoutMessage, parameterName);
            }

            EnsureReferencesResolve(newPage, layout, parameterName);
            return;
        }

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

    private static IEnumerable<string> GetUnitKeys(List<UnitText> units) =>
        units.Select(unit => unit.Identity + "\n" + unit.Text).Order(StringComparer.Ordinal);

    /// <exception cref="ArgumentException">A layout block refers to a path that leads to no subject.</exception>
    private static void EnsureReferencesResolve(MarkdownPage page, PageLayoutRules layout, string parameterName)
    {
        foreach (var block in MarkdownPageLayout.FindLayoutBlocks(page, layout.FindType))
        {
            if (block is { Kind: MarkdownBlockKind.Widget, Head: { } head } &&
                layout.FindType(head.Name) is { } type &&
                MarkdownPageLayout.GetLayoutType(type) is { ReferencePathProperty: not null })
            {
                var path = MarkdownPageLayout.GetReferencePath(block, type);
                if (string.IsNullOrWhiteSpace(path) || !layout.ResolvesToSubject(path))
                {
                    throw new ArgumentException(
                        $"The {head.Name} block at line {block.Line} refers to '{path}', which leads to no existing subject.",
                        parameterName);
                }
            }
        }
    }

    /// <summary>
    /// Gets the source to write for a page whose current source is <paramref name="source"/> when a change was made to
    /// <paramref name="redactedSource"/>, the view of the page with secret values hidden by <paramref name="redactions"/>,
    /// and gave <paramref name="newContent"/>. Each block that must stay (every block, or with <paramref name="layout"/>
    /// every block that is no layout block) and each old subject(name) code block must be exactly as in the view and gets
    /// its text from the source back, so a hidden value is kept and a guessed one is not accepted.
    /// </summary>
    /// <exception cref="ArgumentException">The new content changes such a unit of the view or breaks another rule of
    /// <see cref="EnsureActiveContentUnchanged"/>.</exception>
    public static string RestoreBlocks(
        string source,
        string redactedSource,
        IReadOnlyList<EmbeddedSubjectRedaction.Redaction> redactions,
        string newContent,
        string parameterName,
        PageLayoutRules? layout = null)
    {
        var message = layout is null ? ActiveContentMessage : LayoutMessage;

        // Every redaction is inside a unit, so a unit of the source maps to the view by the redactions before its end.
        var units = GetUnits(MarkdownPageParser.Parse(source), layout);
        var newUnits = GetUnits(MarkdownPageParser.Parse(newContent), layout);
        if (units.Count != newUnits.Count)
        {
            throw new ArgumentException(message, parameterName);
        }

        // A unit of the new content takes the source text of the unit of the view that it repeats: in order without the
        // permission, and by its place among the blocks with it, because layout around the units can move them.
        var matchedSourceSpans = new MarkdownSpan[newUnits.Count];
        var unmatched = new Dictionary<string, Queue<MarkdownSpan>>(StringComparer.Ordinal);
        for (var index = 0; index < units.Count; index++)
        {
            var start = MapToView(units[index].Span.Start, redactions);
            var end = MapToView(units[index].Span.End, redactions);
            if (start < 0 || end < start || end > redactedSource.Length)
            {
                throw new ArgumentException(message, parameterName);
            }

            var viewKey = units[index].Identity + "\n" + units[index].Kind + ":" + NormalizeLineEndings(redactedSource[start..end]);
            if (layout is null)
            {
                if (!string.Equals(viewKey, newUnits[index].Identity + "\n" + newUnits[index].Text, StringComparison.Ordinal))
                {
                    throw new ArgumentException(message, parameterName);
                }

                matchedSourceSpans[index] = units[index].Span;
            }
            else
            {
                if (!unmatched.TryGetValue(viewKey, out var spans))
                {
                    unmatched[viewKey] = spans = new Queue<MarkdownSpan>();
                }

                spans.Enqueue(units[index].Span);
            }
        }

        if (layout is not null)
        {
            for (var index = 0; index < newUnits.Count; index++)
            {
                if (!unmatched.TryGetValue(newUnits[index].Identity + "\n" + newUnits[index].Text, out var spans) ||
                    !spans.TryDequeue(out var span))
                {
                    throw new ArgumentException(message, parameterName);
                }

                matchedSourceSpans[index] = span;
            }
        }

        var restored = new StringBuilder(newContent.Length);
        var copied = 0;
        for (var index = 0; index < newUnits.Count; index++)
        {
            var newSpan = newUnits[index].Span;
            if (newSpan.Start < copied)
            {
                // Units overlap only on a page that the parser does not read as usual.
                throw new ArgumentException(message, parameterName);
            }

            var span = matchedSourceSpans[index];
            restored.Append(newContent, copied, newSpan.Start - copied).Append(source, span.Start, span.Length);
            copied = newSpan.End;
        }

        var result = restored.Append(newContent, copied, newContent.Length - copied).ToString();
        EnsureActiveContentUnchanged(source, result, parameterName, layout);

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
    private static ActiveContentOfPage GetActiveContent(MarkdownPage page, PageLayoutRules? layout)
    {
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

        // An abbreviation definition changes the text everywhere on the page, also inside blocks and expressions.
        if (page.Document.GetAbbreviations() is { } abbreviations)
        {
            activeMarkup.AddRange(abbreviations.Values.Select(abbreviation => "abbreviation:" + abbreviation.Label + "=" + abbreviation.Text));
        }

        return new ActiveContentOfPage(GetUnits(page, layout), layout is null ? GetUnnamedBlockPositions(page) : [], activeMarkup);
    }

    /// <summary>
    /// Gets the position of each block without a #name among the content around it, with the positions of the blocks
    /// around it up to the nearest one with a #name, which is what its key holds.
    /// </summary>
    private static List<string> GetUnnamedBlockPositions(MarkdownPage page)
    {
        var positions = new List<string>();
        AddUnnamedBlockPositions(MarkdownSegmentBuilder.Build(page), string.Empty, positions);
        return positions;
    }

    private static void AddUnnamedBlockPositions(IReadOnlyList<MarkdownSegment> segments, string path, List<string> positions)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index] is BlockMarkdownSegment blockSegment)
            {
                // A block with a #name keeps its subject wherever it is, and the keys of the blocks inside it count
                // from its content, so their positions are taken relative to it.
                if (blockSegment.Block.Head?.Id is { } id)
                {
                    AddUnnamedBlockPositions(blockSegment.Content, "#" + id, positions);
                    continue;
                }

                var blockPath = path + "/" + index.ToString(CultureInfo.InvariantCulture);
                positions.Add(blockPath);
                AddUnnamedBlockPositions(blockSegment.Content, blockPath, positions);
            }
        }
    }

    /// <summary>
    /// Gets the units that must stay as they are, in order: each fenced code block of the old subject(name) form outside of
    /// them, up to its closing fence or the end of the page, and each block with its kind and its text from the head line
    /// to its last line, which includes its configuration, its content and the blocks inside it. Without
    /// <paramref name="layout"/> that is every block directly on the page, and a block in a list item or a quote is taken
    /// like one of them. With it, it is every block that is no layout block and stands on the page or in a layout block,
    /// each with its place among the blocks, which the keys of the subjects hold. The text is compared with line feeds as
    /// line endings.
    /// </summary>
    private static List<UnitText> GetUnits(MarkdownPage page, PageLayoutRules? layout)
    {
        var source = page.Source;
        var lineEnds = GetLineEnds(source);
        var units = new List<UnitText>(page.Blocks.Count);
        if (layout is null)
        {
            foreach (var block in page.Blocks)
            {
                units.Add(CreateBlockUnit(block, source, lineEnds, identity: string.Empty));
            }
        }
        else
        {
            var layoutBlocks = MarkdownPageLayout.FindLayoutBlocks(page, layout.FindType);
            var places = GetBlockPlaces(page);
            AddUnitsBelowLayout(page.Blocks, layoutBlocks, places, source, lineEnds, units);
        }

        var blockCount = units.Count;
        foreach (var codeBlock in page.Document.Descendants<FencedCodeBlock>())
        {
            var start = codeBlock.Span.Start;
            if (MarkdownSegmentBuilder.IsOldSubjectFence(codeBlock) &&
                !units.Take(blockCount).Any(unit => unit.Span.Start <= start && start < unit.Span.End))
            {
                var end = MarkdownPageParser.GetCodeBlockEnd(source, codeBlock);
                units.Add(new UnitText(SubjectFenceKind, SubjectFenceKind + ":" + NormalizeLineEndings(source[start..end]), new MarkdownSpan(start, end - start), SubjectFenceKind));
            }
        }

        units.Sort(static (left, right) => left.Span.Start.CompareTo(right.Span.Start));
        return units;
    }

    private static void AddUnitsBelowLayout(
        IReadOnlyList<MarkdownBlock> blocks,
        HashSet<MarkdownBlock> layoutBlocks,
        Dictionary<MarkdownBlock, string> places,
        string source,
        List<int> lineEnds,
        List<UnitText> units)
    {
        foreach (var block in blocks)
        {
            if (layoutBlocks.Contains(block))
            {
                AddUnitsBelowLayout(block.Children, layoutBlocks, places, source, lineEnds, units);
            }
            else
            {
                // A block that is no segment of the page, such as one in a list item, creates no subject and keeps its text only.
                units.Add(CreateBlockUnit(block, source, lineEnds, places.GetValueOrDefault(block) ?? "unplaced"));
            }
        }
    }

    private static UnitText CreateBlockUnit(MarkdownBlock block, string source, List<int> lineEnds, string identity)
    {
        var end = lineEnds[Math.Clamp(block.EndLine, 1, lineEnds.Count) - 1];
        var start = Math.Min(block.HeadSpan.Start, end);
        return new UnitText(block.Kind.ToString(), block.Kind + ":" + NormalizeLineEndings(source[start..end]), new MarkdownSpan(start, end - start), identity);
    }

    /// <summary>
    /// Gets the place of each block among the blocks of the page: the path of its key and the keys of the blocks around
    /// it, each key being the #name of a block or its name and its position among the content around it, with the names
    /// of its configuration values. A subject is kept by a reload only while its place is the same.
    /// </summary>
    private static Dictionary<MarkdownBlock, string> GetBlockPlaces(MarkdownPage page)
    {
        var places = new Dictionary<MarkdownBlock, string>(ReferenceEqualityComparer.Instance);
        AddBlockPlaces(MarkdownSegmentBuilder.Build(page), string.Empty, places);
        return places;
    }

    private static void AddBlockPlaces(IReadOnlyList<MarkdownSegment> segments, string path, Dictionary<MarkdownBlock, string> places)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index] is BlockMarkdownSegment { Block: var block } blockSegment)
            {
                var key = block.Head?.Id is { } id
                    ? "#" + id
                    : (block.Head?.Name ?? block.Kind.ToString()) + "_" + index.ToString(CultureInfo.InvariantCulture);

                // A removed configuration value creates the subject again, and with it the subjects inside it.
                var configurationNames = string.Join(",", GetConfigurationNames(block).Order(StringComparer.Ordinal));
                var place = path + "/" + key + "(" + configurationNames + ")";
                places[block] = place;
                AddBlockPlaces(blockSegment.Content, place, places);
            }
        }
    }

    private static IEnumerable<string> GetConfigurationNames(MarkdownBlock block)
    {
        var names = block.Head?.Arguments.Select(argument => argument.Key) ?? [];
        try
        {
            if (block.ConfigurationJson is { } json && JsonNode.Parse(json) is JsonObject configuration)
            {
                names = names.Concat(configuration.Select(pair => pair.Key));
            }
        }
        catch (JsonException)
        {
            names = names.Append("!");
        }

        return names;
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    // The position after the last character of each line, before its line ending, by 0-based line.
    private static List<int> GetLineEnds(string source)
    {
        // A carriage return alone ends a line as well, as for the parser.
        var lineEnds = new List<int>();
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\n')
            {
                lineEnds.Add(index > 0 && source[index - 1] == '\r' ? index - 1 : index);
            }
            else if (source[index] == '\r' && (index + 1 == source.Length || source[index + 1] != '\n'))
            {
                lineEnds.Add(index);
            }
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
            !StoragePathFilter.IsIgnored(name);

        if (!isValid)
        {
            throw new ArgumentException(
                $"'{name}' is not a valid Markdown file name: use a plain file name ending in .md, without folders, " +
                "a leading dot, invisible formatting characters or any of the characters / \\ : * ? \" < > |.", parameterName);
        }
    }

    /// <summary>The kind of a unit, its text for the comparison, its span in its source and its place among the blocks.</summary>
    private readonly record struct UnitText(string Kind, string Text, MarkdownSpan Span, string Identity);

    private sealed record ActiveContentOfPage(List<UnitText> Units, List<string> UnnamedBlockPositions, List<string> ActiveMarkup);
}

/// <summary>
/// What the write rules need to let layout blocks change under the permission to edit page layout.
/// </summary>
/// <param name="FindType">Gets the type that a widget block names with its full name, or null when there is none.</param>
/// <param name="ResolvesToSubject">Gets whether a reference path of a layout block leads to an existing subject.</param>
internal sealed record PageLayoutRules(Func<string, Type?> FindType, Func<string, bool> ResolvesToSubject);
