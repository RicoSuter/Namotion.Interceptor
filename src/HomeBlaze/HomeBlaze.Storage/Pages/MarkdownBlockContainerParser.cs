using Markdig.Extensions.GenericAttributes;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// Parses the fence lines of blocks. A fence is a run of at least <see cref="MarkdownBlockFence.MinimumLength"/>
/// fence characters at the start of a line. A fence with text after it opens a block, and a fence alone
/// closes the innermost open block that was opened with the same number of characters, together with
/// every block that is still open inside it.
/// </summary>
internal sealed class MarkdownBlockContainerParser : BlockParser
{
    public MarkdownBlockContainerParser()
    {
        OpeningCharacters = [MarkdownBlockFence.Character];
    }

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (!TryReadFence(processor, out var fenceLength, out var head))
        {
            return BlockState.None;
        }

        if (head.IsEmpty)
        {
            return CloseBlock(processor, fenceLength);
        }

        var attributes = TryReadAttributes(head);
        var block = new MarkdownBlockContainer(this)
        {
            FenceLength = fenceLength,
            IsStyle = attributes is not null,
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, processor.Line.End)
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
            TryReadFence(processor, out var fenceLength, out var head) &&
            head.IsEmpty &&
            fenceLength == ((MarkdownBlockContainer)block).FenceLength)
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

    private static bool TryReadFence(BlockProcessor processor, out int fenceLength, out StringSlice head)
    {
        head = processor.Line;
        fenceLength = 0;
        if (processor.IsCodeIndent)
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

    private BlockState CloseBlock(BlockProcessor processor, int fenceLength)
    {
        List<MarkdownBlockContainer>? openBlocks = null;

        // A container that this line did not continue is still open in the processor and is closed after
        // this call. It is open for this line only when a container inside it continued.
        var isOpen = false;
        for (var container = processor.CurrentContainer; container is not null; container = container.Parent)
        {
            isOpen |= container.IsOpen;
            if (!isOpen || container is not MarkdownBlockContainer block)
            {
                continue;
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
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, processor.Line.End)
        });

        return BlockState.BreakDiscard;
    }
}
