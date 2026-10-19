namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// Writes the decorations and folding ranges of the page editor as records of six numbers: kind, color, start line,
/// start column, end line and end column. The editor module (<c>Files/MarkdownFileEditComponent.razor.js</c>) turns
/// them into Monaco decorations, so a refresh sends numbers and not decoration objects.
/// </summary>
public static class BlockOutlineDecorations
{
    /// <summary>A block region that can be edited.</summary>
    public const int SubjectBlockKind = 0;

    /// <summary>An expression region.</summary>
    public const int ExpressionKind = 1;

    /// <summary>The head line of a block with its json block, tinted in the color of the depth.</summary>
    public const int HeadKind = 2;

    /// <summary>The fence of a head line, in the color of the depth.</summary>
    public const int FenceKind = 3;

    /// <summary>The fence of a closing line, in the color of the depth and followed by the next label.</summary>
    public const int ClosingFenceKind = 4;

    /// <summary>A folding range from the start line to the end line. The columns are 1.</summary>
    public const int FoldKind = 5;

    /// <summary>Number of values per record.</summary>
    public const int RecordLength = 6;

    /// <summary>Number of colors, which repeat for deeper blocks.</summary>
    public const int ColorCount = 5;

    /// <summary>
    /// Gets the records of the regions and the outlines, and the labels of the closing fences in their order. A
    /// block gets a fold when it has more than its head line, and a json block when it has more than one line.
    /// </summary>
    public static EditorDecorations Create(IReadOnlyList<DecorationRegion> regions, IReadOnlyList<BlockOutline> outlines)
    {
        var recordCount = regions.Count;
        var labelCount = 0;
        for (var i = 0; i < outlines.Count; i++)
        {
            var outline = outlines[i];
            var hasClosingFence = outline.ClosingFence is not null;
            recordCount += 2 + (hasClosingFence ? 1 : 0) + (HasBlockFold(outline) ? 1 : 0) + (HasConfigurationFold(outline) ? 1 : 0);
            labelCount += hasClosingFence ? 1 : 0;
        }

        var values = new int[recordCount * RecordLength];
        var labels = new string[labelCount];
        var index = 0;
        var labelIndex = 0;

        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var kind = region.Type == DecorationRegionType.SubjectBlock ? SubjectBlockKind : ExpressionKind;
            Write(values, ref index, kind, 0, region.StartLine, region.StartColumn, region.EndLine, region.EndColumn);
        }

        for (var i = 0; i < outlines.Count; i++)
        {
            var outline = outlines[i];
            var color = outline.Depth % ColorCount;

            Write(values, ref index, HeadKind, color, outline.StartLine, outline.HeadFence.Column, outline.HeadEndLine, outline.HeadEndColumn);
            WriteFence(values, ref index, FenceKind, color, outline.HeadFence);
            if (outline.ClosingFence is { } closingFence)
            {
                WriteFence(values, ref index, ClosingFenceKind, color, closingFence);
                labels[labelIndex++] = outline.Name;
            }

            if (HasBlockFold(outline))
            {
                Write(values, ref index, FoldKind, 0, outline.StartLine, 1, outline.EndLine, 1);
            }

            if (HasConfigurationFold(outline))
            {
                Write(values, ref index, FoldKind, 0, outline.ConfigurationStartLine!.Value, 1, outline.HeadEndLine, 1);
            }
        }

        return new EditorDecorations(values, labels);
    }

    private static bool HasBlockFold(BlockOutline outline) => outline.EndLine > outline.StartLine;

    private static bool HasConfigurationFold(BlockOutline outline) =>
        outline.ConfigurationStartLine is { } startLine && outline.HeadEndLine > startLine;

    private static void WriteFence(int[] values, ref int index, int kind, int color, FencePosition fence) =>
        Write(values, ref index, kind, color, fence.Line, fence.Column, fence.Line, fence.Column + fence.Length);

    private static void Write(
        int[] values, ref int index, int kind, int color, int startLine, int startColumn, int endLine, int endColumn)
    {
        values[index++] = kind;
        values[index++] = color;
        values[index++] = startLine;
        values[index++] = startColumn;
        values[index++] = endLine;
        values[index++] = endColumn;
    }
}

/// <summary>
/// The records of <see cref="BlockOutlineDecorations.Create"/> and the labels of the closing fences.
/// </summary>
/// <param name="Values">Records of <see cref="BlockOutlineDecorations.RecordLength"/> values.</param>
/// <param name="Labels">The label of every closing fence record, in their order.</param>
public readonly record struct EditorDecorations(int[] Values, string[] Labels);
