using BlazorMonaco.Editor;

namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// Creates the editor decorations that show the nesting of blocks: the fences in the color of the depth, a tint
/// behind the head line and a label after the closing line. The classes are styled by the page editor.
/// </summary>
public static class BlockOutlineDecorations
{
    /// <summary>Number of colors, which repeat for deeper blocks.</summary>
    public const int ColorCount = 5;

    private static readonly string[] HeadClassNames = CreateClassNames("block-head");
    private static readonly string[] FenceClassNames = CreateClassNames("block-fence");
    private static readonly string[] ClosingLabelClassNames = CreateClassNames("block-closing-label");

    /// <summary>Adds the decorations of the outlines to <paramref name="decorations"/>.</summary>
    public static void Add(IReadOnlyList<BlockOutline> outlines, List<ModelDeltaDecoration> decorations)
    {
        for (var i = 0; i < outlines.Count; i++)
        {
            var outline = outlines[i];
            var color = outline.Depth % ColorCount;

            decorations.Add(new ModelDeltaDecoration
            {
                Range = new BlazorMonaco.Range(outline.StartLine, outline.HeadFence.Column, outline.HeadEndLine, outline.HeadEndColumn),
                Options = new ModelDecorationOptions { ClassName = HeadClassNames[color] }
            });

            decorations.Add(CreateFence(outline.HeadFence, color, label: null));
            if (outline.ClosingFence is { } closingFence)
            {
                decorations.Add(CreateFence(closingFence, color, outline.Name));
            }
        }
    }

    /// <summary>
    /// Gets the folding ranges of the outlines as pairs of start and end lines: one per block that has more
    /// than its head line, and one per json block of more than one line.
    /// </summary>
    public static int[] GetFoldingLines(IReadOnlyList<BlockOutline> outlines)
    {
        var count = 0;
        for (var i = 0; i < outlines.Count; i++)
        {
            count += (HasBlockFold(outlines[i]) ? 2 : 0) + (HasConfigurationFold(outlines[i]) ? 2 : 0);
        }

        var lines = new int[count];
        var index = 0;
        for (var i = 0; i < outlines.Count; i++)
        {
            var outline = outlines[i];
            if (HasBlockFold(outline))
            {
                lines[index++] = outline.StartLine;
                lines[index++] = outline.EndLine;
            }

            if (HasConfigurationFold(outline))
            {
                lines[index++] = outline.ConfigurationStartLine!.Value;
                lines[index++] = outline.HeadEndLine;
            }
        }

        return lines;
    }

    /// <summary>Gets whether <see cref="GetFoldingLines"/> would return <paramref name="lines"/>, without creating the array.</summary>
    public static bool HasSameFoldingLines(IReadOnlyList<BlockOutline> outlines, ReadOnlySpan<int> lines)
    {
        var index = 0;
        for (var i = 0; i < outlines.Count; i++)
        {
            var outline = outlines[i];
            if (HasBlockFold(outline) && !Matches(lines, ref index, outline.StartLine, outline.EndLine))
            {
                return false;
            }

            if (HasConfigurationFold(outline) && !Matches(lines, ref index, outline.ConfigurationStartLine!.Value, outline.HeadEndLine))
            {
                return false;
            }
        }

        return index == lines.Length;
    }

    private static bool HasBlockFold(BlockOutline outline) => outline.EndLine > outline.StartLine;

    private static bool HasConfigurationFold(BlockOutline outline) =>
        outline.ConfigurationStartLine is { } startLine && outline.HeadEndLine > startLine;

    private static bool Matches(ReadOnlySpan<int> lines, ref int index, int startLine, int endLine)
    {
        if (index + 1 >= lines.Length || lines[index] != startLine || lines[index + 1] != endLine)
        {
            return false;
        }

        index += 2;
        return true;
    }

    // The label is injected after the fence and so needs the fence range: Monaco does not show injected text on an empty range.
    private static ModelDeltaDecoration CreateFence(FencePosition fence, int color, string? label) => new()
    {
        Range = new BlazorMonaco.Range(fence.Line, fence.Column, fence.Line, fence.Column + fence.Length),
        Options = new ModelDecorationOptions
        {
            InlineClassName = FenceClassNames[color],
            After = label is null ? null : new InjectedTextOptions
            {
                Content = "  " + label,
                InlineClassName = ClosingLabelClassNames[color]
            }
        }
    };

    private static string[] CreateClassNames(string className)
    {
        var classNames = new string[ColorCount];
        for (var color = 0; color < ColorCount; color++)
        {
            classNames[color] = $"{className} block-color-{color}";
        }

        return classNames;
    }
}
