using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

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

/// <summary>One ::: block of a page with the source positions needed to write it back.</summary>
public sealed class MarkdownBlock
{
    public required MarkdownBlockKind Kind { get; init; }

    /// <summary>Position among the widget, slot and invalid blocks in document order, or -1 for a style block.</summary>
    public int Index { get; internal set; } = -1;

    public MarkdownBlockHead? Head { get; init; }

    public string? Error { get; init; }

    /// <summary>1-based line of the head line.</summary>
    public int Line { get; init; }

    public MarkdownSpan HeadSpan { get; init; }

    public string? ConfigurationJson { get; init; }

    public MarkdownSpan? ConfigurationSpan { get; init; }

    public List<MarkdownBlock> Children { get; } = [];
}

public sealed class MarkdownPage
{
    private readonly Dictionary<CustomContainer, MarkdownBlock> _containers = new();
    private readonly List<MarkdownBlock> _allBlocks = [];

    internal MarkdownPage(string source, MarkdownDocument document)
    {
        Source = source;
        Document = document;
    }

    public string Source { get; }

    /// <summary>Top-level blocks in document order.</summary>
    public List<MarkdownBlock> Blocks { get; } = [];

    /// <summary>All widget, slot and invalid blocks in document order. The position equals <see cref="MarkdownBlock.Index"/>.</summary>
    public IReadOnlyList<MarkdownBlock> AllBlocks => _allBlocks;

    internal MarkdownDocument Document { get; }

    internal MarkdownBlock GetBlock(CustomContainer container) => _containers[container];

    internal void Register(CustomContainer container, MarkdownBlock block)
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
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .UsePreciseSourceLocation()
        .Build();

    public static MarkdownPage Parse(string source)
    {
        var document = Markdig.Markdown.Parse(source, Pipeline);
        var page = new MarkdownPage(source, document);
        Visit(document, page.Blocks, page, allowsBlocks: true, hasOwner: false);
        return page;
    }

    private static void Visit(
        ContainerBlock container, List<MarkdownBlock> siblings, MarkdownPage page, bool allowsBlocks, bool hasOwner)
    {
        foreach (var child in container)
        {
            if (child is CustomContainer customContainer)
            {
                var block = CreateBlock(customContainer, page.Source, allowsBlocks, hasOwner);
                page.Register(customContainer, block);
                siblings.Add(block);

                var isOwner = block.Kind is MarkdownBlockKind.Widget or MarkdownBlockKind.Slot;
                if (block.Kind != MarkdownBlockKind.Invalid)
                {
                    Visit(customContainer, block.Children, page, allowsBlocks: isOwner, hasOwner: isOwner);
                }
            }
            else if (child is ContainerBlock other)
            {
                Visit(other, siblings, page, allowsBlocks: false, hasOwner: false);
            }
        }
    }

    private static MarkdownBlock CreateBlock(CustomContainer container, string source, bool allowsBlocks, bool hasOwner)
    {
        var line = container.Line + 1;
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

        MarkdownBlock Invalid(string error) => new()
        {
            Kind = MarkdownBlockKind.Invalid, Error = error, Line = line, HeadSpan = headSpan
        };

        if (string.IsNullOrEmpty(container.Info))
        {
            var attributes = container.TryGetAttributes();
            var hasAttributes = attributes is not null &&
                (attributes.Id is not null || attributes.Classes is { Count: > 0 } || attributes.Properties is { Count: > 0 });

            return hasAttributes
                ? new MarkdownBlock { Kind = MarkdownBlockKind.Style, Line = line, HeadSpan = headSpan }
                : Invalid("A block needs a name. If this line was meant to close a block, the fence of the outer block must be longer than the fences inside it.");
        }

        if (!allowsBlocks)
        {
            return Invalid("A block is only allowed directly on the page or directly inside another block.");
        }

        if (!MarkdownBlockHead.TryParse(source.Substring(headSpan.Start, headSpan.Length), out var head, out var error))
        {
            return Invalid(error);
        }

        if (!head.IsWidget && !hasOwner)
        {
            return Invalid($"'{head.Name}' is not a type name. A slot block is only allowed directly inside a widget block.");
        }

        string? configurationJson = null;
        MarkdownSpan? configurationSpan = null;
        if (container.Count > 0 && container[0] is FencedCodeBlock { Info: "json" } configuration)
        {
            configurationJson = configuration.Lines.ToString();
            configurationSpan = new MarkdownSpan(configuration.Span.Start, configuration.Span.Length);
        }

        return new MarkdownBlock
        {
            Kind = head.IsWidget ? MarkdownBlockKind.Widget : MarkdownBlockKind.Slot,
            Head = head,
            Line = line,
            HeadSpan = headSpan,
            ConfigurationJson = configurationJson,
            ConfigurationSpan = configurationSpan
        };
    }
}
