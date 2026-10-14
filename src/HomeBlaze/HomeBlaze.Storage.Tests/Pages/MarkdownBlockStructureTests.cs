using HomeBlaze.Storage.Pages;
using Markdig.Syntax;

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
            "Tile", null, """{ "Position": 3 }""", placement.FenceLength, hasClosingLine: placement.HasClosingLine,
            content: MarkdownBlockWriter.CreateBlock("Demo.Leaf", null, "{}", placement.FenceLength + 1, hasClosingLine: placement.HasClosingLine));
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
        var text = MarkdownBlockWriter.CreateBlock("Tile", null, "{}", placement.FenceLength, hasClosingLine: placement.HasClosingLine);
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
    public void WhenInsertedBlockTakesOverTheBlockAfterIt_ThenAlignmentFails()
    {
        // Arrange: the leaf has four fence characters, so a new block with three does not end before it.
        const string source = ":: Demo.Board\n:::: Tile\none\n:::: Demo.Leaf\n::\n";
        var before = MarkdownPageParser.Parse(source);
        var owner = before.Blocks[0];
        var result = MarkdownBlockWriter.InsertLines(source, 4, "::: Tile", "\n", out var edit);

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 1, owner);

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenSiblingHasMoreFenceCharactersThanNeeded_ThenTheNewBlockGetsAsMany()
    {
        // Arrange
        const string source = ":: Demo.Board\n:::: Tile\none\n:::: Demo.Leaf\n::\n";
        var before = MarkdownPageParser.Parse(source);
        var owner = before.Blocks[0];
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(before, owner, "Tile");
        var text = MarkdownBlockWriter.CreateBlock("Tile", null, "{}", placement.FenceLength, hasClosingLine: placement.HasClosingLine);
        var result = MarkdownBlockWriter.InsertLines(source, placement.Line, text, placement.LineEnding, out var edit);

        // Act
        var after = MarkdownPageParser.Parse(result);
        var map = MarkdownBlockAlignment.TryAlign(before, after, edit, 1, owner);

        // Assert: the leaf is still directly inside the board.
        Assert.Equal(":: Demo.Board\n:::: Tile\none\n:::: Tile\n:::: Demo.Leaf\n::\n", result);
        Assert.Equal(new[] { 0, 1, 3 }, map);
        Assert.Equal(3, after.Blocks[0].Children.Count);
    }

    [Fact]
    public void WhenRemovalLetsAStyleBlockTakeTextAfterIt_ThenAlignmentFails()
    {
        // Arrange: the style block has no closing line and is ended by the head line of the tile.
        const string source = ":: Demo.Panel\n::: {.note}\nstyled\n::: Tile\ntwo\n:::\ntext of the panel\n::\n";
        var before = MarkdownPageParser.Parse(source);
        var tile = before.Blocks[0].Children.Single(child => child.Kind == MarkdownBlockKind.Slot);
        var result = MarkdownBlockWriter.RemoveBlock(source, tile, out var edit);

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 0, owner: null);

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenOwnerIsEndedByTheEndOfThePage_ThenItsFirstSlotBlockIsAligned()
    {
        // Arrange
        const string source = "intro\n\n:: Demo.Board Columns=2";
        var before = MarkdownPageParser.Parse(source);
        var owner = before.Blocks[0];
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(before, owner, "Tile");
        var text = MarkdownBlockWriter.CreateBlock("Tile", null, "{}", placement.FenceLength, hasClosingLine: placement.HasClosingLine);
        var result = MarkdownBlockWriter.InsertLines(source, placement.Line, text, placement.LineEnding, out var edit);

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 1, owner);

        // Assert
        Assert.Equal("intro\n\n:: Demo.Board Columns=2\n::: Tile", result);
        Assert.Equal(new[] { 0 }, map);
    }

    [Theory]
    [InlineData("> ::: {.note}\n> styled")]
    [InlineData("- first\n- second")]
    [InlineData("a paragraph")]
    public void WhenBlockBetweenTwoTextsIsRemoved_ThenTheTextAfterItStaysAParagraphOfItsOwn(string textBefore)
    {
        // Arrange
        var source = $":: Demo.Panel\n{textBefore}\n::: Tile\ntwo\n:::\ntext of the panel\n::\n";
        var before = MarkdownPageParser.Parse(source);
        var tile = before.Blocks[0].Children.Single(child => child.Kind == MarkdownBlockKind.Slot);

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(source, tile, out var edit);
        var after = MarkdownPageParser.Parse(result);
        var map = MarkdownBlockAlignment.TryAlign(before, after, edit, 0, owner: null);

        // Assert: the paragraph of the text starts on the line of the text, so it continues nothing before it.
        Assert.NotNull(map);
        var textLine = result.Split('\n').ToList().IndexOf("text of the panel");
        Assert.Contains(
            after.Document.Descendants<ParagraphBlock>(),
            paragraph => paragraph.Line == textLine && paragraph.Parent is MarkdownBlockContainer);
    }

    [Fact]
    public void WhenBlockBetweenATextAndAnIndentedFenceIsRemoved_ThenTheIndentedLineStaysCode()
    {
        // Arrange: four columns of indentation make the line code, and code continues a paragraph before it.
        const string source = ":: Demo.Panel\na paragraph\n::: Tile\n:::\n    :: Demo.Leaf\n::\n";
        var before = MarkdownPageParser.Parse(source);
        var tile = before.Blocks[0].Children.Single(child => child.Kind == MarkdownBlockKind.Slot);

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(source, tile, out var edit);
        var after = MarkdownPageParser.Parse(result);
        var map = MarkdownBlockAlignment.TryAlign(before, after, edit, 0, owner: null);

        // Assert
        Assert.NotNull(map);
        var codeLine = result.Split('\n').ToList().IndexOf("    :: Demo.Leaf");
        Assert.Contains(
            after.Document.Descendants<CodeBlock>(),
            code => code.Line == codeLine && code.Parent is MarkdownBlockContainer);
    }

    [Fact]
    public void WhenLinesAreInsertedBeforeTheJsonBlockOfTheOwner_ThenAlignmentFails()
    {
        // Arrange: the json block is the configuration of the board only while it is the first thing in it.
        const string source = ":: Demo.Board\n```json\n{ \"Tags\": [1] }\n```\n::\n";
        var before = MarkdownPageParser.Parse(source);
        var result = MarkdownBlockWriter.InsertLines(source, 2, "::: Tile\n:::", "\n", out var edit);

        // Act
        var map = MarkdownBlockAlignment.TryAlign(before, MarkdownPageParser.Parse(result), edit, 1, before.Blocks[0]);

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenRandomBlocksAreInsertedAndRemoved_ThenNothingElseChangesAndTheCheckAgreesWithTheBlocks()
    {
        // Arrange
        var random = new Random(20261014);
        var cleanRemovals = 0;
        var refusedRemovals = 0;
        var inserts = 0;

        for (var round = 0; round < 400; round++)
        {
            var lineEnding = random.Next(4) == 0 ? "\r\n" : "\n";
            var source = CreateRandomPage(random, closingLineChance: random.Next(3) * 50, lineEnding);

            for (var step = 0; step < 6; step++)
            {
                var before = MarkdownPageParser.Parse(source);
                var owners = before.AllBlocks.Where(block => block.Head?.Name == "Demo.Board").ToList();
                var owner = owners[random.Next(owners.Count)];
                var tiles = owner.Children.Where(child => child.Kind == MarkdownBlockKind.Slot).ToList();

                if (tiles.Count > 0 && random.Next(2) == 0)
                {
                    // Act
                    var result = MarkdownBlockWriter.RemoveBlock(source, tiles[random.Next(tiles.Count)], out var edit);
                    var after = MarkdownPageParser.Parse(result);
                    var map = MarkdownBlockAlignment.TryAlign(before, after, edit, 0, owner: null);
                    var context = $"Round {round}, step {step}, removal at line {edit.Line}:\n{source}\n--- became ---\n{result}";

                    // Assert: the check says yes exactly when every other block has its text and its place.
                    AssertOnlyTheEditedLinesChanged(source, result, edit, context);
                    AssertRemovalLeavesNothingButOneEmptyLine(result, edit, lineEnding, context);
                    var isClean = Describe(before, edit.Line, edit.RemovedLineCount).SequenceEqual(Describe(after, edit.Line, edit.InsertedLineCount));
                    Assert.True(isClean == (map is not null), context);
                    if (map is null)
                    {
                        refusedRemovals++;
                        continue;
                    }

                    cleanRemovals++;
                    AssertMapKeepsHeadLines(before, after, map, context);
                    Assert.Equal(tiles.Count - 1, after.AllBlocks[map[owner.Index]].Children.Count(child => child.Kind == MarkdownBlockKind.Slot));
                    source = result;
                }
                else
                {
                    // Act
                    var placement = MarkdownBlockWriter.GetSlotBlockPlacement(before, owner, "Tile");
                    var text = MarkdownBlockWriter.Indent(
                        MarkdownBlockWriter.CreateBlock(
                            "Tile", null, $$"""{ "Position": {{step}} }""", placement.FenceLength, placement.LineEnding, placement.HasClosingLine),
                        placement.Indentation,
                        placement.LineEnding);
                    var result = MarkdownBlockWriter.InsertLines(source, placement.Line, text, placement.LineEnding, out var edit);
                    var after = MarkdownPageParser.Parse(result);
                    var map = MarkdownBlockAlignment.TryAlign(before, after, edit, 1, owner);
                    var context = $"Round {round}, step {step}, insert at line {edit.Line}:\n{source}\n--- became ---\n{result}";

                    // Assert: an insert at the placement never touches another block.
                    AssertOnlyTheEditedLinesChanged(source, result, edit, context);
                    AssertInsertedLinesEndLikeThePage(result, edit, placement.LineEnding, context);
                    AssertAppendGivesTheLastLineTheLineEndingOfThePage(source, result, edit, placement.LineEnding, context);
                    Assert.True(Describe(before, 0, 0).SequenceEqual(Describe(after, edit.Line, edit.InsertedLineCount)), context);
                    Assert.True(map is not null, context);
                    AssertMapKeepsHeadLines(before, after, map!, context);
                    Assert.Equal(tiles.Count + 1, after.AllBlocks[map![owner.Index]].Children.Count(child => child.Kind == MarkdownBlockKind.Slot));
                    inserts++;
                    source = result;
                }
            }
        }

        // All three outcomes occur, so none of the comparisons above is empty.
        Assert.True(inserts > 0 && cleanRemovals > 0 && refusedRemovals > 0, $"{inserts} inserts, {cleanRemovals} removals, {refusedRemovals} refused removals");
    }

    // Every block of every kind that is not on the given lines: the head line of the block around it, its own
    // head line, and its lines without the given ones. Equal lists before and after a change mean that every
    // other block kept its text and its place.
    private static List<string> Describe(MarkdownPage page, int skippedLine, int skippedLineCount)
    {
        // Without line endings: an insert after a last line without one gives that line a line ending, and
        // AssertOnlyTheEditedLinesChanged compares the line endings of all other lines.
        var lines = page.Source.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var result = new List<string>();
        Add(page.Blocks, "page");
        return result;

        bool IsSkipped(int line) => line >= skippedLine && line < skippedLine + skippedLineCount;

        void Add(IReadOnlyList<MarkdownBlock> blocks, string parent)
        {
            foreach (var block in blocks.OrderBy(candidate => candidate.Line))
            {
                if (IsSkipped(block.Line))
                {
                    continue;
                }

                var head = lines[block.Line - 1];
                var ownLines = Enumerable.Range(block.Line, block.EndLine - block.Line + 1)
                    .Where(line => !IsSkipped(line))
                    .Select(line => lines[line - 1]);

                // Empty lines at the end are not content: a block without a closing line ends at its last line that is not empty.
                result.Add($"{parent} > {head} [{block.Kind}]\n{string.Join('\n', ownLines).TrimEnd('\n', '\r')}");
                Add(block.Children, head);
            }
        }
    }

    private static void AssertMapKeepsHeadLines(MarkdownPage before, MarkdownPage after, int[] map, string context)
    {
        for (var index = 0; index < map.Length; index++)
        {
            if (map[index] < 0)
            {
                continue;
            }

            var oldBlock = before.AllBlocks[index];
            var newBlock = after.AllBlocks[map[index]];
            Assert.True(
                before.Source.Substring(oldBlock.HeadSpan.Start, oldBlock.HeadSpan.Length) ==
                after.Source.Substring(newBlock.HeadSpan.Start, newBlock.HeadSpan.Length),
                context);
        }
    }

    private static void AssertOnlyTheEditedLinesChanged(string before, string after, MarkdownLineEdit edit, string context)
    {
        var beforeLines = SplitKeepingLineEndings(before);
        var afterLines = SplitKeepingLineEndings(after);
        beforeLines.RemoveRange(edit.Line - 1, edit.RemovedLineCount);
        afterLines.RemoveRange(edit.Line - 1, edit.InsertedLineCount);

        Assert.True(beforeLines.Count == afterLines.Count, context);
        for (var index = 0; index < beforeLines.Count; index++)
        {
            // A change at the very end of a text without a final line ending moves that line ending to the
            // line before the change, so only that line is compared without it.
            var isLineBeforeChangeAtTheEnd = index == beforeLines.Count - 1 && index == edit.Line - 2;
            var isSame = isLineBeforeChangeAtTheEnd
                ? beforeLines[index].TrimEnd('\r', '\n') == afterLines[index].TrimEnd('\r', '\n')
                : beforeLines[index] == afterLines[index];
            Assert.True(isSame, $"Line {index + 1} changed.\n{context}");
        }
    }

    private static void AssertInsertedLinesEndLikeThePage(string after, MarkdownLineEdit edit, string lineEnding, string context)
    {
        var lines = SplitKeepingLineEndings(after);
        for (var index = edit.Line - 1; index < edit.Line - 1 + edit.InsertedLineCount; index++)
        {
            var isLastLineOfTheText = index == lines.Count - 1;
            Assert.True(
                isLastLineOfTheText ? !lines[index].EndsWith('\n') || lines[index].EndsWith(lineEnding) : HasLineEnding(lines[index], lineEnding),
                $"Line {index + 1} has another line ending.\n{context}");
        }
    }

    // The empty line that a removal leaves is not compared by Describe, so it is checked here.
    private static void AssertRemovalLeavesNothingButOneEmptyLine(string after, MarkdownLineEdit edit, string lineEnding, string context)
    {
        Assert.True(edit.InsertedLineCount is 0 or 1, context);
        Assert.True(
            edit.InsertedLineCount == 0 || SplitKeepingLineEndings(after)[edit.Line - 1] == lineEnding,
            $"Line {edit.Line} is not an empty line.\n{context}");
    }

    private static void AssertAppendGivesTheLastLineTheLineEndingOfThePage(
        string before, string after, MarkdownLineEdit edit, string lineEnding, string context)
    {
        var isAppendAfterLineWithoutLineEnding = !before.EndsWith('\n') && edit.Line == before.Count(character => character == '\n') + 2;
        Assert.True(
            !isAppendAfterLineWithoutLineEnding || after.StartsWith(before + lineEnding, StringComparison.Ordinal),
            $"The line that was the last one has another line ending.\n{context}");
    }

    private static bool HasLineEnding(string line, string lineEnding) =>
        line.EndsWith(lineEnding) && (lineEnding == "\r\n" || !line.EndsWith("\r\n"));

    private static List<string> SplitKeepingLineEndings(string text)
    {
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var lineFeed = text.IndexOf('\n', start);
            var end = lineFeed < 0 ? text.Length : lineFeed + 1;
            lines.Add(text[start..end]);
            start = end;
        }

        return lines;
    }

    // Boards with tiles, in three styles: every block with a closing line, none, or each one by chance. Each tile
    // has one or two fence characters more than its board, so a tile can stand inside the tile before it. A board
    // can be indented, can stand inside an open block, can start with a line of text and can end with a widget
    // block after its tiles. Style blocks and text stand between the tiles. An indented board after a board that
    // ends with a list item stands inside that item, where it is an invalid block and no owner.
    private static string CreateRandomPage(Random random, int closingLineChance, string lineEnding)
    {
        var lines = new List<string> { "intro", string.Empty };
        var boardCount = random.Next(1, 4);
        for (var board = 0; board < boardCount; board++)
        {
            AddRandomBoard(lines, random, board, closingLineChance, extraDepth: random.Next(6) == 0 ? 1 : 0);
        }

        if (random.Next(2) == 0)
        {
            lines.Add("outro");
        }

        var text = string.Join(lineEnding, lines);
        return random.Next(3) == 0 ? text : text + lineEnding;
    }

    // With an extra depth the board stands inside an open block, and every fence line of the board and of
    // everything in it has that many fence characters more.
    private static void AddRandomBoard(List<string> lines, Random random, int board, int closingLineChance, int extraDepth)
    {
        var indentation = random.Next(5) == 0 ? "  " : string.Empty;

        void Add(string line) => lines.Add(indentation + line);
        string Fence(int length) => new(':', length + extraDepth);
        bool HasClosingLine() => random.Next(100) < closingLineChance;

        if (extraDepth > 0)
        {
            Add(":: Demo.Card");
        }

        Add($"{Fence(2)} Demo.Board#board{board} Columns=2");
        if (random.Next(6) == 0)
        {
            Add(random.Next(3) switch
            {
                0 => "text before the tiles",
                1 => "> a quote",
                _ => "- an item"
            });
        }

        var lastTileFence = Fence(3);
        var tileCount = random.Next(0, 4);
        for (var tile = 0; tile < tileCount; tile++)
        {
            var tileFence = Fence(random.Next(3) == 0 ? 4 : 3);
            lastTileFence = tileFence;

            Add($"{tileFence} Tile Position={tile}");
            if (random.Next(2) == 0)
            {
                Add($"text {tile} with {{{{ /a/b }}}}");
            }

            if (random.Next(3) == 0)
            {
                Add($"{tileFence}: Demo.Leaf Count=1");
                if (HasClosingLine())
                {
                    Add($"{tileFence}:");
                }
            }

            if (HasClosingLine())
            {
                Add(tileFence);
            }

            if (random.Next(4) == 0)
            {
                Add($"{Fence(3)} {{.note}}");
                Add("styled");
                if (HasClosingLine())
                {
                    Add(Fence(3));
                }
            }

            if (random.Next(4) == 0)
            {
                Add("text after a tile");
            }
        }

        if (random.Next(4) == 0)
        {
            Add($"{lastTileFence} Demo.Leaf Count=2");
            if (HasClosingLine())
            {
                Add(lastTileFence);
            }
        }

        if (HasClosingLine())
        {
            Add(Fence(2));
        }

        if (random.Next(2) == 0)
        {
            lines.Add(string.Empty);
        }
    }
}
