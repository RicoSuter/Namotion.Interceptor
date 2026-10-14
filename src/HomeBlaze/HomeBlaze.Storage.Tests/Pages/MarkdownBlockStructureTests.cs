using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownBlockStructureTests
{
    private const string Board =
        "intro\n\n:: Demo.Board#board Columns=2\n::: Tile Position=1\none\n:::\n::: Tile Position=2\n:::: Demo.Leaf Count=1\n::::\n:::\n::\n\n:: Demo.Other\n::\n";

    private const string ShortBoard =
        "intro\n\n:: Demo.Board#board Columns=2\n::: Tile Position=1\none\n::: Tile Position=2\n:::: Demo.Leaf Count=1\n\n:: Demo.Other\n";

    [Theory]
    [InlineData(Board)]
    [InlineData(ShortBoard)]
    public void WhenFirstSlotBlockIsRemoved_ThenTheOtherBlocksAreMappedToTheirNewIndex(string source)
    {
        // Arrange
        var before = MarkdownPageParser.Parse(source);
        var result = MarkdownBlockWriter.RemoveBlock(source, before.Blocks[0].Children[0], out var edit);

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 0, owner: null);

        // Assert: board, first tile, second tile, leaf, other
        Assert.Equal(new[] { 0, -1, 1, 2, 3 }, map);
    }

    [Theory]
    [InlineData(Board)]
    [InlineData(ShortBoard)]
    public void WhenLastSlotBlockIsRemoved_ThenItAndTheBlockInItAreMappedToNothing(string source)
    {
        // Arrange
        var before = MarkdownPageParser.Parse(source);
        var result = MarkdownBlockWriter.RemoveBlock(source, before.Blocks[0].Children[1], out var edit);

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 0, owner: null);

        // Assert
        Assert.Equal(new[] { 0, 1, -1, -1, 2 }, map);
    }

    [Theory]
    [InlineData(Board, true)]
    [InlineData(ShortBoard, false)]
    public void WhenSlotBlockIsInsertedAtItsPlacement_ThenEveryBlockIsMapped(string source, bool hasClosingLine)
    {
        // Arrange
        var before = MarkdownPageParser.Parse(source);
        var owner = before.Blocks[0];
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(before, owner, "Tile");
        var text = MarkdownBlockWriter.CreateBlock(
            "Tile", null, """{ "Position": 3 }""", 3, hasClosingLine: placement.HasClosingLine,
            content: MarkdownBlockWriter.CreateBlock("Demo.Leaf", null, "{}", 4, hasClosingLine: placement.HasClosingLine));
        var result = MarkdownBlockWriter.InsertLines(source, placement.Line, text, placement.LineEnding, out var edit);

        // Act
        var after = MarkdownPageParser.Parse(result);
        var map = MarkdownBlockAlignment.TryAlign(before, after, edit, 2, owner);

        // Assert: the two new blocks stand between the leaf and the other block.
        Assert.Equal(hasClosingLine, placement.HasClosingLine);
        Assert.Equal(new[] { 0, 1, 2, 3, 6 }, map);
        Assert.Equal(3, after.Blocks[0].Children.Count);
        Assert.Equal("Position", after.Blocks[0].Children[2].Head!.Arguments[0].Key);
    }

    [Fact]
    public void WhenOwnerWithoutClosingLineGetsItsFirstSlotBlock_ThenTheOwnerIsMappedAlthoughItGrew()
    {
        // Arrange
        const string source = "intro\n\n:: Demo.Board Columns=2\n\n:: Demo.Other\n";
        var before = MarkdownPageParser.Parse(source);
        var owner = before.Blocks[0];
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(before, owner, "Tile");
        var text = MarkdownBlockWriter.CreateBlock("Tile", null, "{}", 3, hasClosingLine: placement.HasClosingLine);
        var result = MarkdownBlockWriter.InsertLines(source, placement.Line, text, placement.LineEnding, out var edit);

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 1, owner);

        // Assert
        Assert.Equal("intro\n\n:: Demo.Board Columns=2\n::: Tile\n\n:: Demo.Other\n", result);
        Assert.Equal(new[] { 0, 2 }, map);
    }

    [Fact]
    public void WhenRemovalLetsTheBlockBeforeTakeTextAfterIt_ThenAlignmentFails()
    {
        // Arrange: the first tile has no closing line and is ended by the head line of the second one.
        const string source = ":: Demo.Panel\n::: Tile\none\n::: Tile\ntwo\n:::\ntext of the panel\n::\n";
        var before = MarkdownPageParser.Parse(source);
        var result = MarkdownBlockWriter.RemoveBlock(source, before.Blocks[0].Children[1], out var edit);

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 0, owner: null);

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenInsertedTextHoldsMoreBlocksThanExpected_ThenAlignmentFails()
    {
        // Arrange
        var before = MarkdownPageParser.Parse(Board);
        var owner = before.Blocks[0];
        var result = MarkdownBlockWriter.InsertLines(Board, 11, "::: Tile\n:::\n::: Tile\n:::", "\n", out var edit);

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 1, owner);

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenAnotherHeadLineChangedAsWell_ThenAlignmentFails()
    {
        // Arrange
        var before = MarkdownPageParser.Parse(Board);
        var result = MarkdownBlockWriter.RemoveBlock(Board, before.Blocks[0].Children[0], out var edit)
            .Replace(":: Demo.Other", ":: Demo.Another");

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 0, owner: null);

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenInsertedBlockEndsABlockThatWasOpen_ThenAlignmentFails()
    {
        // Arrange: a head line with two fence characters ends the board, so the other block is no longer beside it.
        var before = MarkdownPageParser.Parse(ShortBoard);
        var owner = before.Blocks[0];
        var result = MarkdownBlockWriter.InsertLines(ShortBoard, 6, ":: Demo.Stray", "\n", out var edit);

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 1, owner);

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenRandomBlocksAreInsertedAndRemoved_ThenEverythingElseStaysByteForByte()
    {
        // Arrange
        var random = new Random(20261014);

        for (var round = 0; round < 300; round++)
        {
            var hasClosingLines = random.Next(2) == 0;
            var lineEnding = random.Next(4) == 0 ? "\r\n" : "\n";
            var source = CreateRandomPage(random, hasClosingLines, lineEnding);

            for (var step = 0; step < 6; step++)
            {
                // Act
                var before = MarkdownPageParser.Parse(source);
                var owners = before.AllBlocks.Where(block => block.Head?.Name == "Demo.Board").ToList();
                var owner = owners[random.Next(owners.Count)];
                var tiles = owner.Children.Where(child => child.Kind == MarkdownBlockKind.Slot).ToList();

                string result;
                MarkdownLineEdit edit;
                int[]? map;
                if (tiles.Count > 0 && random.Next(2) == 0)
                {
                    result = MarkdownBlockWriter.RemoveBlock(source, tiles[random.Next(tiles.Count)], out edit);
                    map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 0, owner: null);
                }
                else
                {
                    var placement = MarkdownBlockWriter.GetSlotBlockPlacement(before, owner, "Tile");
                    var text = MarkdownBlockWriter.Indent(
                        MarkdownBlockWriter.CreateBlock(
                            "Tile", null, $$"""{ "Position": {{step}} }""", owner.FenceLength + 1, placement.LineEnding, placement.HasClosingLine),
                        placement.Indentation,
                        placement.LineEnding);
                    result = MarkdownBlockWriter.InsertLines(source, placement.Line, text, placement.LineEnding, out edit);
                    map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 1, owner);
                }

                // Assert
                Assert.True(map is not null, $"Round {round}, step {step}:\n{source}\n--- became ---\n{result}");
                AssertOnlyTheEditedLinesChanged(source, result, edit);
                source = result;
            }
        }
    }

    private static void AssertOnlyTheEditedLinesChanged(string before, string after, MarkdownLineEdit edit)
    {
        var beforeLines = before.Split('\n').ToList();
        var afterLines = after.Split('\n').ToList();

        // A change at the end of a text without a final line ending moves that line ending, so the last
        // lines are compared without it.
        beforeLines.RemoveRange(edit.Line - 1, edit.RemovedLineCount);
        afterLines.RemoveRange(edit.Line - 1, edit.InsertedLineCount);
        Assert.Equal(
            beforeLines.Select(line => line.TrimEnd('\r')).Where((_, index) => index < beforeLines.Count - 1 || beforeLines[^1].Length > 0),
            afterLines.Select(line => line.TrimEnd('\r')).Where((_, index) => index < afterLines.Count - 1 || afterLines[^1].Length > 0));
    }

    private static string CreateRandomPage(Random random, bool hasClosingLines, string lineEnding)
    {
        var lines = new List<string> { "intro", string.Empty };
        var boardCount = random.Next(1, 4);
        for (var board = 0; board < boardCount; board++)
        {
            lines.Add($":: Demo.Board#board{board} Columns=2");
            var tileCount = random.Next(0, 4);
            for (var tile = 0; tile < tileCount; tile++)
            {
                lines.Add($"::: Tile Position={tile}");
                if (random.Next(2) == 0)
                {
                    lines.Add($"text {tile} with {{{{ /a/b }}}}");
                }

                if (random.Next(3) == 0)
                {
                    lines.Add(":::: Demo.Leaf Count=1");
                    if (hasClosingLines)
                    {
                        lines.Add("::::");
                    }
                }

                if (hasClosingLines)
                {
                    lines.Add(":::");
                }
            }

            // The last block of a page in the short form has a closing line, like the samples of the documentation.
            if (hasClosingLines || board == boardCount - 1 || random.Next(2) == 0)
            {
                lines.Add("::");
            }

            if (random.Next(2) == 0)
            {
                lines.Add(string.Empty);
            }
        }

        if (random.Next(2) == 0)
        {
            lines.Add("outro");
        }

        var text = string.Join(lineEnding, lines);
        return random.Next(3) == 0 ? text : text + lineEnding;
    }
}
