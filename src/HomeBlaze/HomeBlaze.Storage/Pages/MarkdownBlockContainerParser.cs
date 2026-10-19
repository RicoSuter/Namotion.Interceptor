using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.GenericAttributes;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// Parses the fence lines of blocks. A fence is a run of at least <see cref="MarkdownBlockFence.MinimumLength"/>
/// fence characters at the start of a line. A fence followed by text that starts with a letter, an
/// underscore or an opening brace is a head line, and a fence followed by other text is no fence line.
/// A head line ends every open block that was opened with as many characters or more, together with
/// every block that is still open inside them, and opens its block in the container that remains.
/// A fence alone closes the innermost open block that was opened with the same number of characters,
/// together with every block that is still open inside it. A fence line ends no block from inside a
/// quote, a list item or another container that is not a block. The end of the page ends every open block.
/// </summary>
internal sealed class MarkdownBlockContainerParser : BlockParser
{
    public MarkdownBlockContainerParser()
    {
        OpeningCharacters = [MarkdownBlockFence.Character];
    }

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (!TryReadFence(processor, out var fenceStart, out var fenceLength, out var head))
        {
            return BlockState.None;
        }

        if (head.IsEmpty)
        {
            return CloseBlock(processor, fenceStart, fenceLength);
        }

        if (!MarkdownBlockFence.StartsHeadLine(head.CurrentChar))
        {
            return BlockState.None;
        }

        // The blocks that the line is directly inside of have more characters the further in they are,
        // because each of their head lines ended the blocks with as many characters or more.
        MarkdownBlockContainer? outermostEnded = null;
        var container = FindOpenContainer(TakeBackDefinitionMadeFromFence(processor, fenceStart));
        while (container is MarkdownBlockContainer open && open.FenceLength >= fenceLength)
        {
            outermostEnded = open;
            container = open.Parent;
        }

        if (outermostEnded is not null)
        {
            processor.Close(outermostEnded);
        }

        var attributes = TryReadAttributes(head);
        var block = new MarkdownBlockContainer(this)
        {
            FenceLength = fenceLength,
            IsStyle = attributes is not null,
            EndLine = processor.LineIndex,
            Column = processor.Column - (processor.Start - fenceStart),
            Span = new SourceSpan(fenceStart, processor.Line.End)
        };

        if (attributes is not null)
        {
            block.SetAttributes(attributes);
        }

        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        // A block never ends here. The blocks inside it are asked after it, so a code block keeps a fence
        // line as text, and TryOpen sees a fence line only when nothing inside took it. An HTML block
        // takes every line up to an empty one, so it is ended here to let the closing line through. A
        // head line gets through only where an empty line would end the HTML block as well: a script,
        // a style element, a comment and the like keep every line up to their own end.
        var container = (MarkdownBlockContainer)block;
        if (processor.CurrentBlock is HtmlBlock htmlBlock &&
            TryReadFence(processor, out _, out var fenceLength, out var head) &&
            (head.IsEmpty ? fenceLength == container.FenceLength : EndsAtEmptyLine(htmlBlock) && MarkdownBlockFence.StartsHeadLine(head.CurrentChar)) &&
            IsInsideBlocksOnly(htmlBlock, block))
        {
            processor.Close(htmlBlock);
        }

        // Whether the line ends the block is known only once the blocks inside it were asked. Close
        // takes the line off again when it does.
        if (!processor.IsBlankLine)
        {
            container.AddLine(processor.LineIndex);
        }

        // Back before the indentation, which belongs to the content: it can make a line code.
        processor.GoToColumn(processor.ColumnBeforeIndent);
        return BlockState.Continue;
    }

    public override bool Close(BlockProcessor processor, Block block)
    {
        // A block that ends on a line after its head line without a closing line of its own has
        // continued on that line before, and took the line as content then. A block that ends on its
        // first line took none, which is a closing line that closes no block.
        var container = (MarkdownBlockContainer)block;
        if (!container.HasClosingLine && container.EndLine == processor.LineIndex && container.Line != processor.LineIndex)
        {
            container.RemoveLastLine();
        }

        return true;
    }

    /// <summary>
    /// Gets the container that the fence line is in. After a definition, Markdig reads a fence with three
    /// spaces before it as the marker of another definition with the rest of the fence as its text. That
    /// definition is removed again, and the fence line is in the container of the definition list.
    /// </summary>
    private static ContainerBlock? TakeBackDefinitionMadeFromFence(BlockProcessor processor, int fenceStart)
    {
        var container = processor.CurrentContainer;
        if (container is DefinitionItem { Count: 0 } definition && definition.Span.Start == fenceStart)
        {
            container = definition.Parent;
            processor.Discard(definition);

            // What Markdig does itself when a definition declines a line: the empty line before the
            // line is no part of the definition, and the list ends with its last definition.
            if (container is { LastChild: DefinitionItem previous })
            {
                if (previous.LastChild is BlankLineBlock)
                {
                    previous.RemoveAt(previous.Count - 1);
                }

                container.Span.End = previous.Span.End;
            }
        }

        return container;
    }

    private static bool HasBlockAround(ContainerBlock container)
    {
        for (var outer = container.Parent; outer is not null; outer = outer.Parent)
        {
            if (outer is MarkdownBlockContainer)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the innermost container that is open for the current line, from <paramref name="container"/>
    /// outwards. Every container around it is open for the line as well.
    /// </summary>
    private static ContainerBlock? FindOpenContainer(ContainerBlock? container)
    {
        // A container that this line did not continue is still open in the processor and is closed after
        // the parser returns. It is open for this line only when a container inside it continued. A
        // definition list is never asked to continue, so its flag says nothing and its item decides.
        while (container is not null && (!container.IsOpen || container is DefinitionList))
        {
            container = container.Parent;
        }

        return container;
    }

    /// <summary>Gets whether the HTML block ends at the next empty line and not at an end of its own.</summary>
    private static bool EndsAtEmptyLine(HtmlBlock htmlBlock) =>
        htmlBlock.Type is HtmlBlockType.InterruptingBlock or HtmlBlockType.NonInterruptingBlock;

    /// <summary>Gets whether every container between <paramref name="inner"/> and <paramref name="outer"/> is a block.</summary>
    private static bool IsInsideBlocksOnly(Block inner, Block outer)
    {
        for (var container = inner.Parent; container is MarkdownBlockContainer; container = container.Parent)
        {
            if (container == outer)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the fence at the start of the current line without moving the processor.
    /// <paramref name="head"/> is the text after the fence without the spaces around it.
    /// </summary>
    private static bool TryReadFence(BlockProcessor processor, out int fenceStart, out int fenceLength, out StringSlice head)
    {
        head = processor.Line;
        fenceStart = processor.Start;

        // An open definition item takes the first fence character off the line before it declines the
        // line, so the fence can start before the position of the processor.
        while (fenceStart > processor.CurrentLineStartPosition && head.Text[fenceStart - 1] == MarkdownBlockFence.Character)
        {
            fenceStart--;
        }

        // The characters that were taken off count as indentation for the processor, so they are subtracted.
        fenceLength = processor.Start - fenceStart;
        if (MarkdownBlockFence.IsIndentedAsCode(processor.Indent - fenceLength))
        {
            return false;
        }

        while (head.CurrentChar == MarkdownBlockFence.Character)
        {
            head.NextChar();
            fenceLength++;
        }

        head.Trim();
        return fenceLength >= MarkdownBlockFence.MinimumLength;
    }

    private static HtmlAttributes? TryReadAttributes(StringSlice head)
    {
        if (head.CurrentChar != '{' || !GenericAttributesParser.TryParse(ref head, out var attributes))
        {
            return null;
        }

        var hasAttributes = attributes.Id is not null ||
            attributes.Classes is { Count: > 0 } ||
            attributes.Properties is { Count: > 0 };

        return hasAttributes && head.IsEmptyOrWhitespace() ? attributes : null;
    }

    private BlockState CloseBlock(BlockProcessor processor, int fenceStart, int fenceLength)
    {
        List<MarkdownBlockContainer>? openBlocks = null;
        var container = FindOpenContainer(TakeBackDefinitionMadeFromFence(processor, fenceStart));
        for (; container is MarkdownBlockContainer block; container = block.Parent)
        {
            if (block.FenceLength == fenceLength)
            {
                block.HasClosingLine = true;
                block.EndLine = processor.LineIndex;
                block.UpdateSpanEnd(processor.Line.End);
                processor.Close(block);
                return BlockState.BreakDiscard;
            }

            openBlocks ??= [];
            openBlocks.Insert(0, block);
        }

        processor.NewBlocks.Push(new MarkdownBlockContainer(this)
        {
            FenceLength = fenceLength,
            IsClosingLine = true,
            OpenBlocks = openBlocks ?? [],

            // The line is inside a quote, a list item or the like. A head line there opens no valid
            // block, so a closing line there closes none of the blocks around that container either.
            HasOpenBlockOutOfReach = container is not null && HasBlockAround(container),
            EndLine = processor.LineIndex,
            Column = processor.Column - (processor.Start - fenceStart),
            Span = new SourceSpan(fenceStart, processor.Line.End)
        });

        return BlockState.BreakDiscard;
    }
}
