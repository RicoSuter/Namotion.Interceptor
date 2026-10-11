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
/// underscore or an opening brace opens a block, and a fence followed by other text is no fence line.
/// A fence alone closes the innermost open block that was opened with the same number of characters,
/// together with every block that is still open inside it. It closes no block from inside a quote, a
/// list item or another container that is not a block.
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

        // Other text after a fence, such as a time or a smiley, is no attempt at a head line.
        var first = head.CurrentChar;
        if (!char.IsLetter(first) && first != '_' && first != '{')
        {
            return BlockState.None;
        }

        TakeBackDefinitionMadeFromFence(processor, fenceStart);

        var attributes = TryReadAttributes(head);
        var block = new MarkdownBlockContainer(this)
        {
            FenceLength = fenceLength,
            IsStyle = attributes is not null,
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
        // line as text, and TryOpen sees a closing line only when nothing inside took it. An HTML block
        // takes every line up to an empty one, so it is ended here to let the closing line through.
        if (processor.CurrentBlock is HtmlBlock htmlBlock &&
            TryReadFence(processor, out _, out var fenceLength, out var head) &&
            head.IsEmpty &&
            fenceLength == ((MarkdownBlockContainer)block).FenceLength &&
            IsInsideBlocksOnly(htmlBlock, block))
        {
            processor.Close(htmlBlock);
        }

        // Back before the indentation, which belongs to the content: it can make a line code.
        processor.GoToColumn(processor.ColumnBeforeIndent);
        return BlockState.Continue;
    }

    public override bool Close(BlockProcessor processor, Block block)
    {
        ((MarkdownBlockContainer)block).EndLine = processor.LineIndex;
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
        }

        return container;
    }

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

        // Four columns of indentation make the line code. The characters that were taken off count as
        // indentation for the processor, so they are subtracted.
        fenceLength = processor.Start - fenceStart;
        if (processor.Indent - fenceLength >= 4)
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

        // A container that this line did not continue is still open in the processor and is closed after
        // this call. It is open for this line only when a container inside it continued. A definition
        // list is never asked to continue, so its flag says nothing and its item decides.
        var isOpen = false;
        for (var container = TakeBackDefinitionMadeFromFence(processor, fenceStart); container is not null; container = container.Parent)
        {
            isOpen |= container.IsOpen && container is not DefinitionList;
            if (!isOpen)
            {
                continue;
            }

            // The line is inside a quote, a list item or the like. A head line there opens no valid
            // block, so a closing line there closes none of the blocks around that container either.
            if (container is not MarkdownBlockContainer block)
            {
                break;
            }

            if (block.FenceLength == fenceLength)
            {
                block.IsClosed = true;
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
            Column = processor.Column - (processor.Start - fenceStart),
            Span = new SourceSpan(fenceStart, processor.Line.End)
        });

        return BlockState.BreakDiscard;
    }
}
