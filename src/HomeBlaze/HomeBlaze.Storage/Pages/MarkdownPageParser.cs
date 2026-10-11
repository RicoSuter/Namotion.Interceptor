using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HomeBlaze.Storage.Pages;

public readonly record struct MarkdownSpan(int Start, int Length)
{
    public int End => Start + Length;
}

public enum MarkdownBlockKind
{
    Widget,
    Slot,
    Style,
    Invalid
}

/// <summary>
/// One block of a page with the source positions needed to write it back. A closing line that closes
/// no block is an invalid block of one line.
/// </summary>
public sealed class MarkdownBlock
{
    public required MarkdownBlockKind Kind { get; init; }

    /// <summary>Position among the widget, slot and invalid blocks in document order, or -1 for a style block.</summary>
    public int Index { get; internal set; } = -1;

    public MarkdownBlockHead? Head { get; init; }

    public string? Error { get; init; }

    /// <summary>Number of fence characters at the start of the head line.</summary>
    public int FenceLength { get; init; }

    /// <summary>1-based line of the head line.</summary>
    public int Line { get; init; }

    /// <summary>
    /// 1-based line of the closing line. A block without a closing line ends where the block around it
    /// ends, or after the last line of the page.
    /// </summary>
    internal int EndLine { get; init; }

    public MarkdownSpan HeadSpan { get; init; }

    public string? ConfigurationJson { get; init; }

    public MarkdownSpan? ConfigurationSpan { get; init; }

    public IReadOnlyList<MarkdownBlock> Children => ChildList;

    /// <summary>The list behind <see cref="Children"/>, which only the parser fills.</summary>
    internal List<MarkdownBlock> ChildList { get; } = [];
}

public sealed class MarkdownPage
{
    private readonly Dictionary<MarkdownBlockContainer, MarkdownBlock> _containers = new();
    private readonly List<MarkdownBlock> _allBlocks = [];

    // Ordered by start and never overlapping, which the search in IsInsideCode relies on.
    private readonly MarkdownSpan[] _codeSpans;

    internal MarkdownPage(string source, MarkdownDocument document, MarkdownSpan[] codeSpans)
    {
        Source = source;
        Document = document;
        _codeSpans = codeSpans;
    }

    public string Source { get; }

    /// <summary>Top-level blocks in document order.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks => BlockList;

    /// <summary>The list behind <see cref="Blocks"/>, which only the parser fills.</summary>
    internal List<MarkdownBlock> BlockList { get; } = [];

    /// <summary>All widget, slot and invalid blocks in document order. The position equals <see cref="MarkdownBlock.Index"/>.</summary>
    public IReadOnlyList<MarkdownBlock> AllBlocks => _allBlocks;

    internal MarkdownDocument Document { get; }

    /// <summary>
    /// Gets whether the offset into <see cref="Source"/> lies inside markdown code: a fenced code block
    /// with its fence lines, an indented code block, or an inline code span with its backticks.
    /// </summary>
    /// <remarks>Code in raw HTML, such as a <c>code</c> or <c>pre</c> element, is not markdown code.</remarks>
    public bool IsInsideCode(int offset)
    {
        var low = 0;
        var high = _codeSpans.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var span = _codeSpans[middle];
            if (offset < span.Start)
            {
                high = middle - 1;
            }
            else if (offset >= span.End)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the 1-based line before which the head line of a new block is inserted when the cursor is on
    /// <paramref name="line"/>: that line, or the line after the json block of the block around it when
    /// the new block would otherwise stand before or inside that json block.
    /// </summary>
    /// <remarks>The json block must stay the first thing in its block, or it is no configuration any more.</remarks>
    public int GetLineForNewBlock(int line)
    {
        if (FindBlockAround(line) is not { ConfigurationSpan: { } configurationSpan })
        {
            return line;
        }

        var lineAfterConfiguration = Source.AsSpan(0, configurationSpan.End).Count('\n') + 2;
        return Math.Max(line, lineAfterConfiguration);
    }

    /// <summary>
    /// Gets the fence length for a new block whose head line is inserted before the 1-based
    /// <paramref name="line"/>: one more than the fence of the innermost block that the new block is
    /// then inside of, and two directly on the page.
    /// </summary>
    public int GetFenceLengthForNewBlock(int line) =>
        FindBlockAround(line) is { } outer ? outer.FenceLength + 1 : MarkdownBlockFence.MinimumLength;

    /// <summary>Gets the innermost block that a line inserted before <paramref name="line"/> is inside of.</summary>
    private MarkdownBlock? FindBlockAround(int line)
    {
        MarkdownBlock? innermost = null;
        var blocks = Blocks;
        while (blocks.FirstOrDefault(block => block.Line < line && line <= block.EndLine) is { } outer)
        {
            innermost = outer;
            blocks = outer.Children;
        }

        return innermost;
    }

    internal MarkdownBlock GetBlock(MarkdownBlockContainer container) => _containers[container];

    internal void Register(MarkdownBlockContainer container, MarkdownBlock block)
    {
        _containers[container] = block;
        if (block.Kind != MarkdownBlockKind.Style)
        {
            block.Index = _allBlocks.Count;
            _allBlocks.Add(block);
        }
    }
}

/// <summary>Turns page text into a tree of blocks. Knows nothing about subjects.</summary>
public static class MarkdownPageParser
{
    internal static readonly MarkdownPipeline Pipeline = CreatePipeline();

    public static MarkdownPage Parse(string source)
    {
        var document = Markdig.Markdown.Parse(source, Pipeline);
        var page = new MarkdownPage(source, document, FindCodeSpans(source, document));
        Visit(document, page.BlockList, page, allowsBlocks: true, hasOwner: false);
        return page;
    }

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseYamlFrontMatter()
            .UsePreciseSourceLocation();

        // Blocks have a parser of their own, so that a run of fence characters has one meaning only.
        builder.Extensions.TryRemove<CustomContainerExtension>();
        builder.Extensions.Add(new MarkdownBlockContainerExtension());
        return builder.Build();
    }

    private static MarkdownSpan[] FindCodeSpans(string source, MarkdownDocument document)
    {
        var spans = new List<MarkdownSpan>();
        foreach (var descendant in document.Descendants())
        {
            var start = descendant.Span.Start;
            var end = descendant.Span.End + 1;
            switch (descendant)
            {
                // Markdig reports inline code on some tab-indented lines at a wrong place. Such code is
                // left out, because its span would make unrelated text code.
                case CodeInline inline when IsDelimitedBy(source, inline.Delimiter, start, end):
                    break;

                // Front matter and math blocks derive from the code block types, but they are not code:
                // the code block renderer does not render them.
                case CodeBlock block and not (YamlFrontMatterBlock or MathBlock):
                    // The span of a fence that is never closed ends inside its opening line, so the ends
                    // of that line and of the last code line count as well.
                    end = Math.Max(end, FindLineEnd(source, start));
                    if (block.Lines.Count > 0)
                    {
                        end = Math.Max(end, FindLineEnd(source, block.Lines.Lines[block.Lines.Count - 1].Position));
                    }

                    break;

                default:
                    continue;
            }

            spans.Add(new MarkdownSpan(start, end - start));
        }

        // Markdig moves some blocks, for example footnotes to the end of the document.
        spans.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return [.. spans];
    }

    private static bool IsDelimitedBy(string source, char delimiter, int start, int end) =>
        start >= 0 && end <= source.Length && end - start >= 2 && source[start] == delimiter && source[end - 1] == delimiter;

    private static int FindLineEnd(string source, int position)
    {
        var lineEnd = source.IndexOf('\n', position);
        return lineEnd < 0 ? source.Length : lineEnd;
    }

    private static void Visit(
        ContainerBlock container, List<MarkdownBlock> siblings, MarkdownPage page, bool allowsBlocks, bool hasOwner)
    {
        foreach (var child in container)
        {
            if (child is MarkdownBlockContainer blockContainer)
            {
                var block = CreateBlock(blockContainer, page.Source, allowsBlocks, hasOwner);
                page.Register(blockContainer, block);
                siblings.Add(block);

                var isOwner = block.Kind is MarkdownBlockKind.Widget or MarkdownBlockKind.Slot;
                if (block.Kind != MarkdownBlockKind.Invalid)
                {
                    Visit(blockContainer, block.ChildList, page, allowsBlocks: isOwner, hasOwner: isOwner);
                }
            }
            else if (child is ContainerBlock other)
            {
                Visit(other, siblings, page, allowsBlocks: false, hasOwner: false);
            }
        }
    }

    private static MarkdownBlock CreateBlock(
        MarkdownBlockContainer container, string source, bool allowsBlocks, bool hasOwner)
    {
        var headStart = container.Span.Start;
        var headEnd = source.IndexOf('\n', headStart);
        if (headEnd < 0)
        {
            headEnd = source.Length;
        }

        if (headEnd > headStart && source[headEnd - 1] == '\r')
        {
            headEnd--;
        }

        var headSpan = new MarkdownSpan(headStart, headEnd - headStart);
        var kind = MarkdownBlockKind.Invalid;
        MarkdownBlockHead? head = null;
        string? error;

        if (container.IsClosingLine)
        {
            error = FormatClosesNothing(container);
        }
        else
        {
            if (container.IsStyle)
            {
                error = FindNestingError(container);
                if (error is null)
                {
                    kind = MarkdownBlockKind.Style;
                }
            }
            else if (!allowsBlocks)
            {
                error = "A block is only allowed directly on the page or directly inside another block.";
            }
            else if (MarkdownBlockHead.TryParse(source.Substring(headSpan.Start, headSpan.Length), out head, out error))
            {
                // After the head grammar, so that prose which starts with a fence is not told to add characters.
                error = FindNestingError(container);
                if (error is null)
                {
                    if (head.IsWidget || hasOwner)
                    {
                        kind = head.IsWidget ? MarkdownBlockKind.Widget : MarkdownBlockKind.Slot;
                    }
                    else
                    {
                        error = $"'{head.Name}' is not a type name. A slot block is only allowed directly inside a widget block.";
                    }
                }
            }

            // The box replaces the block with everything in it, so it also says why that is more than expected.
            if (!container.IsClosed)
            {
                var fence = MarkdownBlockFence.Create(container.FenceLength);
                var notClosed = $"This block is not closed. The line '{fence}' that ends it is missing.";
                if (FindFirstBlockClosedByLineOfLength(container, container.FenceLength) is { } inner)
                {
                    notClosed =
                        $"{notClosed} The '{fence}' at line {inner.EndLine + 1} closed the block at line {inner.Line + 1}, " +
                        $"which is inside this block and has {container.FenceLength} characters as well.";
                }

                error = error is null ? notClosed : $"{error} {notClosed}";
                kind = MarkdownBlockKind.Invalid;
            }
        }

        string? configurationJson = null;
        MarkdownSpan? configurationSpan = null;
        var isOwner = kind is MarkdownBlockKind.Widget or MarkdownBlockKind.Slot;
        if (isOwner && container.Count > 0 && container[0] is FencedCodeBlock { Info: "json" } configuration)
        {
            configurationJson = configuration.Lines.ToString();
            configurationSpan = new MarkdownSpan(configuration.Span.Start, configuration.Span.Length);
        }

        return new MarkdownBlock
        {
            Kind = kind,
            Head = isOwner ? head : null,
            Error = error,
            FenceLength = container.FenceLength,
            Line = container.Line + 1,
            EndLine = container.EndLine + 1,
            HeadSpan = headSpan,
            ConfigurationJson = configurationJson,
            ConfigurationSpan = configurationSpan
        };
    }

    /// <summary>
    /// Gets the block inside <paramref name="container"/> that was the first to be closed by a line of
    /// <paramref name="fenceLength"/> characters. Only blocks that such a line reaches from the container
    /// count, so none inside a quote or a list item.
    /// </summary>
    private static MarkdownBlockContainer? FindFirstBlockClosedByLineOfLength(MarkdownBlockContainer container, int fenceLength)
    {
        // In document order with the blocks inside a block before that block, which is the order of the closing lines.
        foreach (var child in container)
        {
            if (child is not MarkdownBlockContainer { IsClosingLine: false } inner)
            {
                continue;
            }

            if (FindFirstBlockClosedByLineOfLength(inner, fenceLength) is { } nested)
            {
                return nested;
            }

            if (inner is { IsClosed: true, IsClosedByLongerLine: false } && inner.FenceLength == fenceLength)
            {
                return inner;
            }
        }

        return null;
    }

    private static string? FindNestingError(MarkdownBlockContainer container)
    {
        var outer = container.Parent;
        while (outer is not null and not MarkdownBlockContainer)
        {
            outer = outer.Parent;
        }

        if (outer is not MarkdownBlockContainer outerBlock || container.FenceLength > outerBlock.FenceLength)
        {
            return null;
        }

        return $"A block inside another block needs more '{MarkdownBlockFence.Character}' characters than the block around it. " +
               $"This block has {container.FenceLength} and the block around it, at line {outerBlock.Line + 1}, has {outerBlock.FenceLength}. " +
               $"Write '{MarkdownBlockFence.Create(outerBlock.FenceLength + 1)}' here and on its closing line.";
    }

    private static string FormatClosesNothing(MarkdownBlockContainer closingLine)
    {
        var message = $"This line of {closingLine.FenceLength} '{MarkdownBlockFence.Character}' characters closes no block.";
        if (closingLine.OpenBlocks.Count > 0)
        {
            var openBlocks = closingLine.OpenBlocks.Select(block => $"line {block.Line + 1} ({block.FenceLength} characters)");
            message = $"{message} A closing line needs as many characters as the head line of its block. " +
                      $"Open here: {string.Join(", ", openBlocks)}.";
        }

        if (closingLine.HasOpenBlockOutOfReach)
        {
            return $"{message} A closing line inside a quote or a list item closes no block outside of it.";
        }

        return closingLine.OpenBlocks.Count > 0 ? message : $"{message} No block is open here.";
    }
}
