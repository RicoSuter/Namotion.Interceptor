using System.Text.RegularExpressions;
using HomeBlaze.Storage.Pages;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

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
        // Act
        var (_, _, _, map) = RemoveTile(source);

        // Assert: board, first tile, second tile, leaf, other
        Assert.Equal(new[] { 0, -1, 1, 2, 3 }, map);
    }

    [Theory]
    [InlineData(Board)]
    [InlineData(ShortBoard)]
    public void WhenLastSlotBlockIsRemoved_ThenItAndTheBlockInItAreMappedToNothing(string source)
    {
        // Act
        var (_, _, _, map) = RemoveTile(source, position: 1);

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
            "Tile", null, """{ "Position": 3 }""", placement.FenceLength, "\n", placement.HasClosingLine,
            content: MarkdownBlockWriter.CreateBlock("Demo.Leaf", null, "{}", placement.FenceLength + 1, "\n", placement.HasClosingLine));
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

        // Act
        var (_, after, _, map) = InsertTile(source);

        // Assert
        Assert.Equal("intro\n\n:: Demo.Board Columns=2\n::: Tile\n\n:: Demo.Other\n", after.Source);
        Assert.Equal(new[] { 0, 2 }, map);
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

    [Theory]
    // More blocks than expected.
    [InlineData(Board, 11, "::: Tile\n:::\n::: Tile\n:::")]
    // A head line with two fence characters ends the board, so the other block is no longer beside it.
    [InlineData(ShortBoard, 6, ":: Demo.Stray")]
    // The leaf has four fence characters, so a new block with three does not end before it and takes it over.
    [InlineData(":: Demo.Board\n:::: Tile\none\n:::: Demo.Leaf\n::\n", 4, "::: Tile")]
    // The new block makes two lists of one.
    [InlineData(":: Demo.Board\n- first\n- second\n::\n", 3, "::: Tile\n:::")]
    // Directly after an attribute line, the new tile takes the attributes of the line from the heading after it.
    [InlineData(":: Demo.Board\n{#custom}\n# Heading of the board\n::\n", 3, "::: Tile\n:::")]
    [InlineData(":: Demo.Board\n{.highlight}\n# Heading of the board\n::\n", 3, "::: Tile\n:::")]
    // The json block is the configuration of the board only while it is the first thing in it.
    [InlineData(":: Demo.Board\n```json\n{ \"Tags\": [1] }\n```\n::\n", 2, "::: Tile\n:::")]
    public void WhenInsertedLinesChangeMoreThanTheirOwnBlock_ThenAlignmentFails(string source, int line, string text)
    {
        // Act
        var (_, _, _, map) = InsertLines(source, line, text);

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenSiblingHasMoreFenceCharactersThanNeeded_ThenTheNewBlockGetsAsMany()
    {
        // Arrange
        const string source = ":: Demo.Board\n:::: Tile\none\n:::: Demo.Leaf\n::\n";

        // Act
        var (_, after, _, map) = InsertTile(source);

        // Assert: the leaf is still directly inside the board.
        Assert.Equal(":: Demo.Board\n:::: Tile\none\n:::: Tile\n:::: Demo.Leaf\n::\n", after.Source);
        Assert.Equal(new[] { 0, 1, 3 }, map);
        Assert.Equal(3, after.Blocks[0].Children.Count);
    }

    [Fact]
    public void WhenOwnerIsEndedByTheEndOfThePage_ThenItsFirstSlotBlockIsAligned()
    {
        // Arrange
        const string source = "intro\n\n:: Demo.Board Columns=2";

        // Act
        var (_, after, _, map) = InsertTile(source);

        // Assert
        Assert.Equal("intro\n\n:: Demo.Board Columns=2\n::: Tile", after.Source);
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

        // Act
        var (_, after, _, map) = RemoveTile(source);

        // Assert: the paragraph of the text starts on the line of the text, so it continues nothing before it.
        Assert.NotNull(map);
        var textLine = after.Source.Split('\n').ToList().IndexOf("text of the panel");
        Assert.Contains(
            after.Document.Descendants<ParagraphBlock>(),
            paragraph => paragraph.Line == textLine && paragraph.Parent is MarkdownBlockContainer);
    }

    [Fact]
    public void WhenBlockBetweenATextAndAnIndentedFenceIsRemoved_ThenTheIndentedLineStaysCode()
    {
        // Arrange: four columns of indentation make the line code, and code continues a paragraph before it.
        const string source = ":: Demo.Panel\na paragraph\n::: Tile\n:::\n    :: Demo.Leaf\n::\n";

        // Act
        var (_, after, _, map) = RemoveTile(source);

        // Assert
        Assert.NotNull(map);
        var codeLine = after.Source.Split('\n').ToList().IndexOf("    :: Demo.Leaf");
        Assert.Contains(
            after.Document.Descendants<CodeBlock>(),
            code => code.Line == codeLine && code.Parent is MarkdownBlockContainer);
    }

    [Theory]
    // The text becomes a part of the list item, with two or four columns of indentation.
    [InlineData(":: Demo.Panel\n- first\n::: Tile\ntwo\n:::\n  text of the panel\n::\n")]
    [InlineData(":: Demo.Panel\n- first\n::: Tile\ntwo\n:::\n    text of the panel\n::\n")]
    // The text becomes the definition of the term, also after an empty line.
    [InlineData(":: Demo.Panel\na term\n::: Tile\ntwo\n:::\n:   text of the panel\n::\n")]
    [InlineData(":: Demo.Panel\na term\n\n::: Tile\ntwo\n:::\n:   text of the panel\n::\n")]
    // Every text keeps its kind and its containers by name, but the two lists become one, also across the empty
    // line that the removal leaves, and a list with an empty line between its items is rendered with paragraphs.
    [InlineData(":: Demo.Panel\n- first\n::: Tile\ntwo\n:::\n- second\n::\n")]
    [InlineData(":: Demo.Panel\n1. first\n::: Tile\ntwo\n:::\n2. second\n::\n")]
    [InlineData(":: Demo.Panel\na term\n:   a definition\n::: Tile\ntwo\n:::\nanother term\n:   another definition\n::\n")]
    // Without the tile, the empty line at the end of the page follows the definition and makes it loose.
    [InlineData(":: Demo.Board\nterm\n:   definition\n::: Tile\n:::\n\n")]
    public void WhenRemovalJoinsTheTextAfterTheBlockToTheTextBeforeIt_ThenAlignmentFails(string source)
    {
        // Act
        var (_, _, _, map) = RemoveTile(source);

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenInsertedBlockMakesADefinitionAtTheEndOfThePageTight_ThenAlignmentFails()
    {
        // Arrange: Markdig keeps the empty line at the end of the page in the definition, which makes it loose
        // and renders its text as a paragraph. After the new tile the empty line is no part of it any more.
        const string source = ":: Demo.Board\nterm\n:   definition\n\n";

        // Act
        var (_, _, _, map) = InsertTile(source, """{ "Position": 1 }""");

        // Assert
        Assert.Null(map);
    }

    [Theory]
    [InlineData("a paragraph", "another paragraph")]
    [InlineData("- first\n- second", "text of the panel")]
    [InlineData("- first", "* a list of another kind")]
    [InlineData("> a quote", "> another quote")]
    [InlineData("> a quote", "text of the panel")]
    [InlineData("# A heading", "text of the panel")]
    [InlineData("```\ncode\n```", "```\nmore code\n```")]
    [InlineData("    indented code", "text of the panel")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |", "| 3 | 4 |")]
    public void WhenRemovalLeavesTheTextAroundTheBlockAsItIs_ThenAlignmentSucceeds(string textBefore, string textAfter)
    {
        // Arrange
        var source = $":: Demo.Panel\n{textBefore}\n::: Tile\ntwo\n:::\n{textAfter}\n::\n";

        // Act
        var (_, _, _, map) = RemoveTile(source);

        // Assert
        Assert.Equal(new[] { 0, -1 }, map);
    }

    [Theory]
    // The heading in the tile is the first of the page, so Markdig lists the link definition after the panel
    // before the removal and at the end of the page after it.
    [InlineData("intro\n\n:: Demo.Panel\n::: Tile\n# A heading\n:::\n::\n\ntext with [a link]\n\n:: Demo.Other\n::\n\n[a link]: http://example.com\n", new[] { 0, -1, 1 })]
    // The span of the indented code takes the empty line after it once the tile is gone.
    [InlineData(":: Demo.Panel\n    indented code\n::: Tile\n\n::\n", new[] { 0, -1 })]
    public void WhenRemovalChangesOnlyWhereMarkdigListsOrEndsOtherText_ThenAlignmentSucceeds(string source, int[] expected)
    {
        // Act
        var (_, _, _, map) = RemoveTile(source);

        // Assert
        Assert.Equal(expected, map);
    }

    [Fact]
    public void WhenRemovalBindsAnAttributeLineToTheTextAfterTheBlock_ThenAlignmentFails()
    {
        // Arrange: Markdig gives the attributes of a line that holds nothing else to the block after it, which is
        // the tile before the removal and the text of the panel after it, also across an empty line.
        const string source = ":: Demo.Panel\n{.highlight}\n::: Tile\ntwo\n:::\ntext of the panel\n::\n";

        // Act
        var (before, after, _, map) = RemoveTile(source);

        // Assert
        Assert.Null(map);
        Assert.Null(before.Document.Descendants<ParagraphBlock>().Last().TryGetAttributes());
        Assert.Equal(["highlight"], after.Document.Descendants<ParagraphBlock>().Single().TryGetAttributes()?.Classes);
    }

    [Theory]
    [InlineData("{#custom}", "", "text of the panel")]
    [InlineData("{#custom}", "", "# Heading of the panel")]
    [InlineData("{.highlight}", "", "# Heading of the panel")]
    [InlineData("{#custom}", "\n", "text of the panel")]
    [InlineData("{#custom}", "\n", "# Heading of the panel")]
    [InlineData("{#custom}", "\n \t\n", "# Heading of the panel")]
    public void WhenRemovedBlockStandsAfterAnAttributeLine_ThenAlignmentFails(string attributeLine, string emptyLines, string textAfter)
    {
        // Arrange: the attributes of the line belong to the tile before the removal and to the text after it,
        // because Markdig gives them to the next block also across empty lines.
        var source = $":: Demo.Panel\n{attributeLine}\n{emptyLines}::: Tile\ntwo\n:::\n{textAfter}\n::\n";

        // Act
        var (_, _, _, map) = RemoveTile(source);

        // Assert
        Assert.Null(map);
    }

    [Fact]
    public void WhenRemovedBlockHoldsAHeadingWhoseTextStandsLaterInThePage_ThenAlignmentSucceeds()
    {
        // Arrange: Markdig numbers the ids of headings with the same text, so the heading after the panel gets
        // another id once the tile is gone. Nothing of its text or its place changes.
        const string source = ":: Demo.Panel\n::: Tile\n# A heading\n:::\n::\n\n# A heading\n";

        // Act
        var (before, after, _, map) = RemoveTile(source);

        // Assert
        Assert.Equal(new[] { 0, -1 }, map);
        Assert.NotEqual(
            before.Document.Descendants<HeadingBlock>().Last().TryGetAttributes()?.Id,
            after.Document.Descendants<HeadingBlock>().Single().TryGetAttributes()?.Id);
    }

    [Fact]
    public void WhenRemovedBlockHoldsTheOnlyReferenceToAFootnote_ThenAlignmentFails()
    {
        // Arrange: Markdig drops a footnote that nothing refers to, so the text of the footnote outside the tile
        // is gone from the document. That follows from content that the user removes, and it is refused all the same.
        const string source = ":: Demo.Panel\n::: Tile\ntext with a note[^1]\n:::\n::\n\n[^1]: The note.\n";

        // Act
        var (before, after, _, map) = RemoveTile(source);

        // Assert
        Assert.Null(map);
        Assert.Single(before.Document.Descendants<Footnote>());
        Assert.Empty(after.Document.Descendants<Footnote>());
    }

    [Fact]
    public void WhenRemovedBlockHoldsALinkDefinitionThatTextOutsideUses_ThenAlignmentSucceedsAndTheTextLosesItsLink()
    {
        // Arrange: link reference definitions are not compared, so the removal is accepted although the text
        // after the panel is no link any more. That follows from content that the user removes.
        const string source = ":: Demo.Panel\n::: Tile\n[a link]: http://example.com\n:::\n::\n\ntext with [a link]\n";

        // Act
        var (before, after, _, map) = RemoveTile(source);

        // Assert
        Assert.Equal(new[] { 0, -1 }, map);
        Assert.Single(before.Document.Descendants<LinkInline>());
        Assert.Empty(after.Document.Descendants<LinkInline>());
    }

    [Fact]
    public void WhenBlockWithMarkdownOfManyKindsIsRemovedAndInsertedAgain_ThenBothAreAligned()
    {
        // Arrange: nothing in the tile may count as text outside of it, wherever Markdig lists it.
        const string content =
            "# A heading\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\na term\n:   a definition\n\n[a link]: http://example.com\n\n" +
            "- [ ] a task\n- an item\n\n1. one\n2. two\n\n> a quote\n\n```\ncode\n```\n\n    indented code\n\n<div>\nhtml\n</div>\n\n" +
            "A heading\n=========\n\n***\n\ntext with [a link]";
        var source = $"intro with [a link]\n\n:: Demo.Panel\ntext before\n::: Tile\n{content}\n:::\n# Another heading\ntext after\n::\n\noutro\n";

        // Act
        var (_, without, removal, removalMap) = RemoveTile(source);
        var (_, _, _, insertMap) = InsertLines(without.Source, removal.Line, $"::: Tile\n{content}\n:::");

        // Assert
        Assert.Equal(new[] { 0, -1 }, removalMap);
        Assert.Equal(new[] { 0 }, insertMap);
    }

    [Theory]
    // The list item starts before the indentation of the tile, so a line with that indentation is a part of it.
    [InlineData("  :: Demo.Board\n  ::: Tile\n- an item\n  text of the item\n")]
    // After a definition that holds a quote, Markdig counts the indentation of a list item as a part of its
    // marker, so a line with that indentation after it is a part of the item.
    [InlineData("  :: Demo.Board\n  ::: Tile\n  a term\n  :   a definition\n  > a quote\n  - an item\n")]
    public void WhenPlacementIsInsideAListItem_ThenTheNewBlockIsNoSlotBlockAndAlignmentFails(string source)
    {
        // Act
        var (_, after, edit, map) = InsertTile(source);

        // Assert
        Assert.Null(map);
        Assert.Equal(MarkdownBlockKind.Invalid, after.AllBlocks.Single(block => block.Line == edit.Line).Kind);
    }

    [Fact]
    public void WhenBlockThatEndedBeforeTheRemovalGoesOnAfterIt_ThenTheWalkOverTheBlocksRefuses()
    {
        // Arrange: the first tile has no closing line and is ended by the head line of the second one, so it is
        // neither the owner nor around it and takes the text after the removed tile.
        const string source = ":: Demo.Panel\n::: Tile\none\n::: Tile\ntwo\n:::\ntext of the panel\n::\n";
        var (before, after, edit, _) = RemoveTile(source, position: 1);

        // Act
        var map = MarkdownBlockAlignment.TryMapBlocks(before, after, edit, 0, owner: null);

        // Assert
        Assert.Null(map);
        Assert.True(after.Blocks[0].Children[0].EndLine > before.Blocks[0].Children[0].EndLine);
    }

    [Fact]
    public void WhenInsertedBlockGoesOnAfterTheInsertedLines_ThenTheWalkOverTheBlocksRefuses()
    {
        // Arrange: the new tile has no closing line, so it takes the text after it up to the closing line of the board.
        const string source = ":: Demo.Board\n::: Tile\n:::\ntext of the board\n::\n";
        var (before, after, edit, _) = InsertLines(source, 4, "::: Tile");

        // Act
        var map = MarkdownBlockAlignment.TryMapBlocks(before, after, edit, 1, before.Blocks[0]);

        // Assert
        Assert.Null(map);
        Assert.Equal(5, after.Blocks[0].Children[1].EndLine);
    }

    [Fact]
    public void WhenRemovalLetsAStyleBlockTakeTextAfterIt_ThenTheWalkOverTheBlocksRefuses()
    {
        // Arrange: the style block has no closing line and is ended by the head line of the tile. It is in no
        // index of the page, and the walk looks at it all the same.
        const string source = ":: Demo.Panel\n::: {.note}\nstyled\n::: Tile\ntwo\n:::\ntext of the panel\n::\n";
        var (before, after, edit, _) = RemoveTile(source);

        // Act
        var map = MarkdownBlockAlignment.TryMapBlocks(before, after, edit, 0, owner: null);

        // Assert
        Assert.Null(map);
        Assert.Equal(MarkdownBlockKind.Style, after.Blocks[0].Children.Single().Kind);
    }

    [Fact]
    public void WhenOnlyTheTextOfALeafBlockOutsideTheChangeDiffers_ThenTheComparisonOfTheDocumentRefuses()
    {
        // Arrange: the blocks of the page and every head line are the same, only a paragraph after the removed
        // tile has another text.
        const string source = ":: Demo.Panel\n::: Tile\n:::\ntext of the panel\n::\n";
        var before = MarkdownPageParser.Parse(source);
        var edit = new MarkdownLineEdit(2, 2, 0);
        var after = MarkdownPageParser.Parse(":: Demo.Panel\nother text\n::\n");

        // Act
        var isSame = MarkdownBlockAlignment.HasSameDocument(before, after, edit);

        // Assert
        Assert.False(isSame);
        Assert.Equal(new[] { 0, -1 }, MarkdownBlockAlignment.TryMapBlocks(before, after, edit, 0, owner: null));
    }

    [Fact]
    public void WhenOnlyTheAttributesOfALeafBlockOutsideTheChangeDiffer_ThenTheComparisonOfTheDocumentRefuses()
    {
        // Arrange: the inserted attribute line and the empty line after it stand on the lines of the change, and
        // no attribute line stands before the change, so only the classes of the paragraph after it tell the two
        // pages apart.
        const string source = ":: Demo.Panel\n::: Tile\n:::\ntext of the panel\n::\n";
        var before = MarkdownPageParser.Parse(source);
        var edit = new MarkdownLineEdit(2, 2, 2);
        var after = MarkdownPageParser.Parse(":: Demo.Panel\n{.highlight}\n\ntext of the panel\n::\n");

        // Act
        var isSame = MarkdownBlockAlignment.HasSameDocument(before, after, edit);

        // Assert
        Assert.False(isSame);
        Assert.Null(before.Document.Descendants<ParagraphBlock>().Single().TryGetAttributes());
        Assert.Equal(["highlight"], after.Document.Descendants<ParagraphBlock>().Single().TryGetAttributes()?.Classes);
    }

    [Fact]
    public void WhenRandomBlocksAreInsertedAndRemoved_ThenNothingElseChangesAndTheCheckAgreesWithTheBlocks()
    {
        // Arrange
        var random = new Random(20261014);
        var cleanRemovals = 0;
        var refusedRemovals = 0;
        var removalsRefusedForTheDocumentOnly = 0;
        var refusedRemovalsAfterAttributeLines = 0;
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

                    // Assert: the check says yes exactly when every other block has its text and its place, and
                    // every text and every container outside the block is what it was.
                    AssertOnlyTheEditedLinesChanged(source, result, edit, context);
                    AssertRemovalLeavesNothingButOneEmptyLine(result, edit, lineEnding, context);
                    var hasSameBlocks = Describe(before, edit.Line, edit.RemovedLineCount).SequenceEqual(Describe(after, edit.Line, edit.InsertedLineCount));
                    var hasSameDocument = DescribeDocument(before, edit.Line, edit.RemovedLineCount).SequenceEqual(DescribeDocument(after, edit.Line, edit.InsertedLineCount));
                    var followsAttributeLine = FollowsAttributeLine(source, edit.Line);
                    Assert.True(
                        (hasSameBlocks && hasSameDocument && !followsAttributeLine) == (map is not null),
                        $"Same blocks: {hasSameBlocks}, same document: {hasSameDocument}, after an attribute line: {followsAttributeLine}, map: {map is not null}.\n{context}");
                    if (map is null)
                    {
                        refusedRemovals++;
                        if (hasSameBlocks)
                        {
                            removalsRefusedForTheDocumentOnly++;
                        }

                        if (followsAttributeLine)
                        {
                            refusedRemovalsAfterAttributeLines++;
                        }

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

                    // Assert: an insert at the placement never touches another line or another block.
                    AssertOnlyTheEditedLinesChanged(source, result, edit, context);
                    AssertInsertedLinesEndLikeThePage(result, edit, placement.LineEnding, context);
                    AssertAppendGivesTheLastLineTheLineEndingOfThePage(source, result, edit, placement.LineEnding, context);
                    Assert.True(Describe(before, 0, 0).SequenceEqual(Describe(after, edit.Line, edit.InsertedLineCount)), context);

                    // The check says yes exactly when the document outside the new tile is what it was. The document
                    // changes after a definition at the end of the page, which loses the empty line that made it loose.
                    var hasSameDocumentAfterInsert = DescribeDocument(before, 0, 0).SequenceEqual(DescribeDocument(after, edit.Line, edit.InsertedLineCount));
                    Assert.True(
                        hasSameDocumentAfterInsert == (map is not null),
                        $"Same document: {hasSameDocumentAfterInsert}, map: {map is not null}.\n{context}");
                    if (map is null)
                    {
                        continue;
                    }

                    AssertMapKeepsHeadLines(before, after, map, context);
                    Assert.Equal(tiles.Count + 1, after.AllBlocks[map[owner.Index]].Children.Count(child => child.Kind == MarkdownBlockKind.Slot));
                    inserts++;
                    source = result;
                }
            }
        }

        // All three outcomes occur, so none of the comparisons above is empty, and so do removals that only the
        // document refuses, so the description of the blocks alone does not decide every one of them.
        var counters =
            $"{inserts} inserts, {cleanRemovals} removals, {refusedRemovals} refused removals, " +
            $"{removalsRefusedForTheDocumentOnly} of them for the document only, " +
            $"{refusedRemovalsAfterAttributeLines} of them after an attribute line";
        Assert.True(inserts > 0 && cleanRemovals > 0 && refusedRemovals > 0, counters);
        Assert.True(removalsRefusedForTheDocumentOnly > 0, counters);
        Assert.True(refusedRemovalsAfterAttributeLines > 0, counters);
    }

    // Removes the slot block at the position among the slot blocks of the first block, and aligns the pages.
    private static (MarkdownPage Before, MarkdownPage After, MarkdownLineEdit Edit, int[]? Map) RemoveTile(string source, int position = 0)
    {
        var before = MarkdownPageParser.Parse(source);
        var tile = before.Blocks[0].Children.Where(child => child.Kind == MarkdownBlockKind.Slot).ElementAt(position);
        var after = MarkdownPageParser.Parse(MarkdownBlockWriter.RemoveBlock(source, tile, out var edit));
        return (before, after, edit, MarkdownBlockAlignment.TryAlign(before, after, edit, 0, owner: null));
    }

    // Inserts the lines of one block into the first block, and aligns the pages.
    private static (MarkdownPage Before, MarkdownPage After, MarkdownLineEdit Edit, int[]? Map) InsertLines(string source, int line, string text)
    {
        var before = MarkdownPageParser.Parse(source);
        var after = MarkdownPageParser.Parse(MarkdownBlockWriter.InsertLines(source, line, text, "\n", out var edit));
        return (before, after, edit, MarkdownBlockAlignment.TryAlign(before, after, edit, 1, before.Blocks[0]));
    }

    // Inserts a tile at its placement in the first block, written the way the write-back writes it, and aligns the pages.
    private static (MarkdownPage Before, MarkdownPage After, MarkdownLineEdit Edit, int[]? Map) InsertTile(string source, string configurationJson = "{}")
    {
        var before = MarkdownPageParser.Parse(source);
        var owner = before.Blocks[0];
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(before, owner, "Tile");
        var text = MarkdownBlockWriter.Indent(
            MarkdownBlockWriter.CreateBlock("Tile", null, configurationJson, placement.FenceLength, placement.LineEnding, placement.HasClosingLine),
            placement.Indentation,
            placement.LineEnding);
        var after = MarkdownPageParser.Parse(MarkdownBlockWriter.InsertLines(source, placement.Line, text, placement.LineEnding, out var edit));
        return (before, after, edit, MarkdownBlockAlignment.TryAlign(before, after, edit, 1, owner));
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

    // Whether the last line before the given 1-based line that is not blank is an attribute line as CreateRandomPage
    // writes it, an id or a class in braces, which Markdig gives to the next block also across empty lines.
    private static bool FollowsAttributeLine(string source, int line) =>
        source.Split('\n').Take(line - 1).LastOrDefault(text => !string.IsNullOrWhiteSpace(text)) is { } lastText &&
        Regex.IsMatch(lastText, @"^[ \t]*\{(#id-\d+|\.highlight)\}\r?$");

    // Every block of the Markdig document that does not start on the given lines, in the order of the document:
    // its kind, whether a list is loose or a definition holds an empty line, the containers around it from the
    // block outwards, the classes and the properties of its attributes, and for a leaf block its text. A block of
    // the page is named by its head line. The id of the attributes is left out, because Markdig numbers the ids of
    // headings with the same text. Equal lists before and after a change mean that every text outside the change
    // is what it was, with the attributes it had and rendered as a paragraph or not as before, and that no list or
    // quote was joined or split. Written apart from the check on purpose, so that the two must agree.
    private static List<string> DescribeDocument(MarkdownPage page, int skippedLine, int skippedLineCount)
    {
        var lines = page.Source.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var result = new List<string>();
        foreach (var block in page.Document.Descendants<Block>())
        {
            // Link reference definitions are no text, and Markdig lists them where the first one of the page
            // stands. The group of the footnotes and an empty line in a list item have no line of their own.
            var line = block.Line + 1;
            if (block is LinkReferenceDefinition or LinkReferenceDefinitionGroup or FootnoteGroup or BlankLineBlock ||
                (line >= skippedLine && line < skippedLine + skippedLineCount))
            {
                continue;
            }

            var containers = new List<string>();
            for (var parent = block.Parent; parent is not null; parent = parent.Parent)
            {
                containers.Add(parent is MarkdownBlockContainer container ? lines[container.Line] : parent.GetType().Name);
            }

            // White space at the end is no text: the span of an indented code block takes the empty lines after it.
            var text = block is LeafBlock && block.Span.End >= block.Span.Start
                ? page.Source[block.Span.Start..Math.Min(block.Span.End + 1, page.Source.Length)]
                : string.Empty;
            var attributes = block.TryGetAttributes();
            var classes = string.Join(' ', attributes?.Classes ?? []);
            var properties = string.Join(' ', (attributes?.Properties ?? []).Select(property => $"{property.Key}={property.Value}"));
            var looseness = block switch
            {
                ListBlock list => list.IsLoose ? " loose" : " tight",
                DefinitionItem item => item.OfType<BlankLineBlock>().Any() ? " loose" : " tight",
                _ => string.Empty
            };
            result.Add(
                $"{block.GetType().Name}{looseness} in {string.Join(" < ", containers)} with [{classes}] [{properties}]\n" +
                text.Replace("\r", string.Empty).TrimEnd());
        }

        return result;
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
    // block after its tiles. Style blocks, text and lines that join what stands before them when a tile between
    // them is removed stand between the tiles, and a tile can have an attribute line before it. An indented board
    // after a board that ends with a list item stands inside that item, where it is an invalid block and no owner.
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

            // A tile between two lines of which the second joins the first when the tile is removed, as text or
            // as an item of the same list. The lines above meet like this too rarely for the check to say no often.
            var joiningLines = random.Next(6) == 0 ? random.Next(4) : -1;
            if (joiningLines >= 0)
            {
                Add(joiningLines == 2 ? "a term before a tile" : "- an item before a tile");
            }

            // An attribute line, which Markdig gives to the tile, also across an empty line.
            if (random.Next(10) == 0)
            {
                Add(random.Next(2) == 0 ? $"{{#id-{tile}}}" : "{.highlight}");
                if (random.Next(2) == 0)
                {
                    lines.Add(string.Empty);
                }
            }

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

            if (joiningLines >= 0)
            {
                Add(joiningLines switch
                {
                    0 => "  text after two spaces",
                    1 => "    text after four spaces",
                    2 => ":   a definition",
                    _ => "- an item of the same list"
                });
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

            // Lines that an empty line does not keep apart from what stands before a removed tile.
            if (random.Next(5) == 0)
            {
                Add(random.Next(5) switch
                {
                    0 => "  text after two spaces",
                    1 => "    text after four spaces",
                    2 => ":   a definition",
                    3 => "> a quote after a tile",
                    _ => "- an item after a tile"
                });
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
