using HomeBlaze.Storage.Blazor.Models;

namespace HomeBlaze.Storage.Blazor.Tests;

public class BlockOutlineTests
{
    [Fact]
    public void WhenBlockHasClosingLine_ThenOutlineEndsThereWithItsFence()
    {
        // Arrange
        const string content = "Text\n\n:: Test.Alert#info\nHello\n::\n\nAfter";

        // Act
        var outline = Assert.Single(MarkdownDecorationParser.ParseDecorations(content).Outlines);

        // Assert
        Assert.Equal((3, 5, 0, "info"), (outline.StartLine, outline.EndLine, outline.Depth, outline.Name));
        Assert.Equal(new FencePosition(3, 1, 2), outline.HeadFence);
        Assert.Equal(new FencePosition(5, 1, 2), outline.ClosingFence);
    }

    [Fact]
    public void WhenSiblingsEndEachOther_ThenEachEndsAtItsLastContentLineWithoutClosingFence()
    {
        // Arrange
        var content = string.Join("\n",
            ":: Test.Grid",
            "::: Cell Row=0",
            "First",
            "",
            "::: Cell Row=1",
            "Second",
            "::");

        // Act
        var outlines = MarkdownDecorationParser.ParseDecorations(content).Outlines;

        // Assert
        Assert.Collection(outlines,
            grid => Assert.Equal((1, 7, 0, "Grid", true), (grid.StartLine, grid.EndLine, grid.Depth, grid.Name, grid.ClosingFence is not null)),
            first => Assert.Equal((2, 3, 1, "Cell", false), (first.StartLine, first.EndLine, first.Depth, first.Name, first.ClosingFence is not null)),
            second => Assert.Equal((5, 6, 1, "Cell", false), (second.StartLine, second.EndLine, second.Depth, second.Name, second.ClosingFence is not null)));
    }

    [Fact]
    public void WhenFencesSkipLengths_ThenDepthFollowsTheNestingAndNotTheFenceLength()
    {
        // Arrange
        var content = string.Join("\n",
            ":: Test.Outer",
            ":::: Test.Inner",
            "Text",
            "::::",
            "::");

        // Act
        var outlines = MarkdownDecorationParser.ParseDecorations(content).Outlines;

        // Assert
        Assert.Equal([0, 1], outlines.Select(outline => outline.Depth));
        Assert.Equal(new FencePosition(4, 1, 4), outlines[1].ClosingFence);
        Assert.Equal(new FencePosition(5, 1, 2), outlines[0].ClosingFence);
    }

    [Fact]
    public void WhenBlockHasJsonBlock_ThenHeadEndsAfterTheJsonBlock()
    {
        // Arrange
        var content = string.Join("\n",
            ":: Test.Chart",
            "```json",
            "{ \"Range\": \"1h\" }",
            "```",
            "::");

        // Act
        var outline = Assert.Single(MarkdownDecorationParser.ParseDecorations(content).Outlines);

        // Assert
        Assert.Equal((4, 4), (outline.HeadEndLine, outline.HeadEndColumn));
        Assert.Equal([(1, 5), (2, 4)], GetFolds([], [outline]));
    }

    [Fact]
    public void WhenFenceIsInsideCode_ThenItGetsNoOutline()
    {
        // Arrange
        const string content = "```\n:: Test.Alert\n::\n```";

        // Act
        var decorations = MarkdownDecorationParser.ParseDecorations(content);

        // Assert
        Assert.Empty(decorations.Outlines);
    }

    [Fact]
    public void WhenOneClosingLineEndsSeveralBlocks_ThenItBelongsToTheBlockWithAsManyFenceCharacters()
    {
        // Arrange
        var content = string.Join("\n",
            ":: Test.Grid#grid",
            "::: Cell#status",
            "Text",
            "::");

        // Act
        var outlines = MarkdownDecorationParser.ParseDecorations(content).Outlines;

        // Assert
        Assert.Equal(new FencePosition(4, 1, 2), outlines[0].ClosingFence);
        Assert.Equal(4, outlines[0].EndLine);
        Assert.Null(outlines[1].ClosingFence);
        Assert.Equal(3, outlines[1].EndLine);
    }

    [Fact]
    public void WhenBlocksHaveNamesTypesOrAttributes_ThenTheLabelNameFollowsThem()
    {
        // Arrange
        var content = string.Join("\n",
            ":: Test.Components.Card#motors",
            "::: Test.Components.ValueList",
            ":::",
            "::",
            "",
            ":: {.note}",
            "Text",
            "::");

        // Act
        var outlines = MarkdownDecorationParser.ParseDecorations(content).Outlines;

        // Assert
        Assert.Equal(["motors", "ValueList", "{.note}"], outlines.Select(outline => outline.Name));
    }

    [Fact]
    public void WhenClosingLineIsInsideQuote_ThenFenceIsFoundAfterTheQuoteMarker()
    {
        // Arrange
        var content = ":: Test.Card\n> ::: {.note}\n> text\n> :::\n::";

        // Act
        var outlines = MarkdownDecorationParser.ParseDecorations(content).Outlines;

        // Assert
        var note = Assert.Single(outlines, outline => outline.Name == "{.note}");
        Assert.Equal(new FencePosition(4, 3, 3), note.ClosingFence);
    }

    [Fact]
    public void WhenContentHasWindowsLineEndings_ThenClosingFenceExcludesTheCarriageReturn()
    {
        // Arrange
        var content = ":: Test.Alert\r\nHello\r\n::\r\n";

        // Act
        var outline = Assert.Single(MarkdownDecorationParser.ParseDecorations(content).Outlines);

        // Assert
        Assert.Equal(new FencePosition(3, 1, 2), outline.ClosingFence);
    }

    [Fact]
    public void WhenJsonBlockFollowsAnEmptyLine_ThenItsFoldStartsAtTheJsonBlock()
    {
        // Arrange
        var content = string.Join("\n",
            ":: Test.Chart",
            "",
            "```json",
            "{ \"Range\": \"1h\" }",
            "```",
            "::");

        // Act
        var outlines = MarkdownDecorationParser.ParseDecorations(content).Outlines;
        var folds = GetFolds([], outlines);

        // Assert
        Assert.Equal([(1, 6), (3, 5)], folds);
    }

    [Fact]
    public void WhenPageHasBlocksAndExpressions_ThenRecordsAndLabelsDescribeEveryDecoration()
    {
        // Arrange
        var content = string.Join("\n",
            ":: Test.Card#motors",
            "::: Test.ValueList",
            "Speed {{ motor/Speed }}",
            ":::",
            "::");
        var parsed = MarkdownDecorationParser.ParseDecorations(content);

        // Act
        var decorations = BlockOutlineDecorations.Create(parsed.Regions, parsed.Outlines);

        // Assert
        Assert.Equal(0, decorations.Values.Length % BlockOutlineDecorations.RecordLength);
        Assert.Equal(["motors", "ValueList"], decorations.Labels);
        var records = decorations.Values.Chunk(BlockOutlineDecorations.RecordLength).ToList();
        Assert.Equal(11, records.Count);
        Assert.Equal(2, records.Count(record => record[0] == BlockOutlineDecorations.SubjectBlockKind));
        Assert.Contains([BlockOutlineDecorations.HeadKind, 0, 1, 1, 1, 20], records);
        Assert.Contains([BlockOutlineDecorations.HeadKind, 1, 2, 1, 2, 19], records);
        Assert.Contains([BlockOutlineDecorations.ExpressionKind, 0, 3, 7, 3, 24], records);
        Assert.Contains([BlockOutlineDecorations.FenceKind, 1, 2, 1, 2, 4], records);
        Assert.Contains([BlockOutlineDecorations.ClosingFenceKind, 1, 4, 1, 4, 4], records);
        Assert.Contains([BlockOutlineDecorations.ClosingFenceKind, 0, 5, 1, 5, 3], records);
        Assert.Equal([(1, 5), (2, 4)], GetFolds(parsed.Regions, parsed.Outlines));
    }

    private static List<(int Start, int End)> GetFolds(IReadOnlyList<DecorationRegion> regions, IReadOnlyList<BlockOutline> outlines) =>
        BlockOutlineDecorations.Create(regions, outlines).Values
            .Chunk(BlockOutlineDecorations.RecordLength)
            .Where(record => record[0] == BlockOutlineDecorations.FoldKind)
            .Select(record => (record[2], record[4]))
            .ToList();
}
