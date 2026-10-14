using System.Text.Json.Nodes;
using HomeBlaze.Storage.Pages;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownBlockWriterTests
{
    private const string Source =
        "before\n\n::Demo.Card#main Title=Old\nBody **text** {{ /A/B }}\n\n::::Demo.Motor Speed=1\n```json\n{ \"Cells\": [1] }\n```\n::::\n::\n\nafter\n";

    private static MarkdownBlock Card => MarkdownPageParser.Parse(Source).Blocks[0];

    private static MarkdownBlock Motor => Card.Children[0];

    [Fact]
    public void WhenScalarsChange_ThenOnlyTheHeadLineChanges()
    {
        // Arrange
        var card = Card;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(
            Source, card, """{ "$type": "Demo.Card", "Title": "New Title", "Count": 2, "Enabled": true, "Nothing": null }""");

        // Assert
        Assert.Equal(
            Source.Replace("::Demo.Card#main Title=Old", ":: Demo.Card#main Title=\"New Title\" Count=2 Enabled=true"),
            result);
    }

    [Fact]
    public void WhenComplexValueChanges_ThenTheJsonFenceIsReplaced()
    {
        // Arrange
        var motorBlock = Motor;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, motorBlock, """{ "Speed": 5, "Cells": [3] }""");

        // Assert
        var motor = MarkdownPageParser.Parse(result).Blocks[0].Children[0];
        Assert.Equal(":::: Demo.Motor Speed=5", result.Substring(motor.HeadSpan.Start, motor.HeadSpan.Length));
        Assert.Equal("{\"Cells\":[3]}", motor.ConfigurationJson!.Replace(" ", "").Replace("\n", ""));
    }

    [Fact]
    public void WhenNoComplexValueIsLeft_ThenTheJsonFenceIsRemoved()
    {
        // Arrange
        var motorBlock = Motor;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, motorBlock, """{ "Speed": 5 }""");

        // Assert
        Assert.Contains(":::: Demo.Motor Speed=5\n::::\n::", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void WhenBlockHadNoFence_ThenAComplexValueAddsOneAfterTheHead()
    {
        // Arrange
        var card = Card;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, card, """{ "Title": "Old", "Tags": ["a"] }""");

        // Assert
        Assert.Contains(":: Demo.Card#main Title=Old\n```json\n{\n  \"Tags\": [\n    \"a\"\n  ]\n}\n```\nBody **text**", result);
    }

    private const string TagsOne = "```json\n{ \"Tags\": [1] }\n```";

    private const string TagsTwo = "```json\n{\n  \"Tags\": [\n    2\n  ]\n}\n```";

    private const string NewTitle = """{ "Title": "New" }""";

    private const string NewTitleAndTags = """{ "Title": "New", "Tags": [2] }""";

    // The source, the index of the block to write, the configuration and the page text after the write.
    public static TheoryData<string, int, string, string> BlocksWithoutClosingLine() => new()
    {
        // An empty block that the head line of the next block ends.
        { ":: Demo.Card Title=Old\n:: Demo.Motor Speed=1\n", 0, NewTitle, ":: Demo.Card Title=New\n:: Demo.Motor Speed=1\n" },
        { ":: Demo.Card Title=Old\n:: Demo.Motor Speed=1\n", 0, NewTitleAndTags, $":: Demo.Card Title=New\n{TagsTwo}\n:: Demo.Motor Speed=1\n" },
        { $":: Demo.Card Title=Old\n{TagsOne}\n:: Demo.Motor Speed=1\n", 0, NewTitleAndTags, $":: Demo.Card Title=New\n{TagsTwo}\n:: Demo.Motor Speed=1\n" },
        { $":: Demo.Card Title=Old\n{TagsOne}\n:: Demo.Motor Speed=1\n", 0, NewTitle, ":: Demo.Card Title=New\n:: Demo.Motor Speed=1\n" },
        { ":: Demo.Card Title=Old\n:: Demo.Motor Speed=1\n", 1, """{ "Speed": 2 }""", ":: Demo.Card Title=Old\n:: Demo.Motor Speed=2\n" },

        // A block with content.
        { "before\n\n:: Demo.Card Title=Old\nBody **text**\n\n:: Demo.Motor\n", 0, NewTitle, "before\n\n:: Demo.Card Title=New\nBody **text**\n\n:: Demo.Motor\n" },
        { "before\n\n:: Demo.Card Title=Old\nBody **text**\n\n:: Demo.Motor\n", 0, NewTitleAndTags, $"before\n\n:: Demo.Card Title=New\n{TagsTwo}\nBody **text**\n\n:: Demo.Motor\n" },
        { $"before\n\n:: Demo.Card Title=Old\n{TagsOne}\nBody **text**\n\n:: Demo.Motor\n", 0, NewTitleAndTags, $"before\n\n:: Demo.Card Title=New\n{TagsTwo}\nBody **text**\n\n:: Demo.Motor\n" },
        { $"before\n\n:: Demo.Card Title=Old\n{TagsOne}\nBody **text**\n\n:: Demo.Motor\n", 0, NewTitle, "before\n\n:: Demo.Card Title=New\nBody **text**\n\n:: Demo.Motor\n" },

        // A block that is the last thing on the page, with a final line break.
        { "before\n\n:: Demo.Card Title=Old\n", 0, NewTitle, "before\n\n:: Demo.Card Title=New\n" },
        { "before\n\n:: Demo.Card Title=Old\n", 0, NewTitleAndTags, $"before\n\n:: Demo.Card Title=New\n{TagsTwo}\n" },
        { $"before\n\n:: Demo.Card Title=Old\n{TagsOne}\n", 0, NewTitleAndTags, $"before\n\n:: Demo.Card Title=New\n{TagsTwo}\n" },
        { $"before\n\n:: Demo.Card Title=Old\n{TagsOne}\n", 0, NewTitle, "before\n\n:: Demo.Card Title=New\n" },

        // A block that is the last thing on the page, without a final line break.
        { "before\n\n:: Demo.Card Title=Old", 0, NewTitle, "before\n\n:: Demo.Card Title=New" },
        { "before\n\n:: Demo.Card Title=Old", 0, NewTitleAndTags, $"before\n\n:: Demo.Card Title=New\n{TagsTwo}" },
        { $"before\n\n:: Demo.Card Title=Old\n{TagsOne}", 0, NewTitleAndTags, $"before\n\n:: Demo.Card Title=New\n{TagsTwo}" },
        { $"before\n\n:: Demo.Card Title=Old\n{TagsOne}", 0, NewTitle, "before\n\n:: Demo.Card Title=New" },

        // A block whose first child is another block.
        { ":: Demo.Card Title=Old\n::: Demo.Motor Speed=1\ntext\n", 0, NewTitle, ":: Demo.Card Title=New\n::: Demo.Motor Speed=1\ntext\n" },
        { ":: Demo.Card Title=Old\n::: Demo.Motor Speed=1\ntext\n", 0, NewTitleAndTags, $":: Demo.Card Title=New\n{TagsTwo}\n::: Demo.Motor Speed=1\ntext\n" },
        { $":: Demo.Card Title=Old\n{TagsOne}\n::: Demo.Motor Speed=1\ntext\n", 0, NewTitleAndTags, $":: Demo.Card Title=New\n{TagsTwo}\n::: Demo.Motor Speed=1\ntext\n" },
        { $":: Demo.Card Title=Old\n{TagsOne}\n::: Demo.Motor Speed=1\ntext\n", 0, NewTitle, ":: Demo.Card Title=New\n::: Demo.Motor Speed=1\ntext\n" },
        { ":: Demo.Card Title=Old\n::: Demo.Motor Speed=1\ntext\n", 1, """{ "Speed": 2, "Tags": [2] }""", $":: Demo.Card Title=Old\n::: Demo.Motor Speed=2\n{TagsTwo}\ntext\n" },

        // The short form of a layout: every line but the one of the block stays as it is.
        {
            ":: Demo.Flow\n::: Item\n:::: Demo.Card Title=A\n::::: Demo.Motor Speed=1\n::: Item\n:::: Demo.Card Title=B\n",
            3,
            """{ "Speed": 2 }""",
            ":: Demo.Flow\n::: Item\n:::: Demo.Card Title=A\n::::: Demo.Motor Speed=2\n::: Item\n:::: Demo.Card Title=B\n"
        }
    };

    [Theory]
    [MemberData(nameof(BlocksWithoutClosingLine))]
    public void WhenBlockHasNoClosingLine_ThenOnlyItsHeadLineAndItsJsonFenceChangeAndNoClosingLineIsAdded(
        string source, int blockIndex, string configurationJson, string expected)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(source, page.AllBlocks[blockIndex], configurationJson);

        // Assert
        Assert.Equal(expected, result);

        var written = MarkdownPageParser.Parse(result);
        Assert.Equal(
            page.AllBlocks.Select(block => (block.Kind, block.Head?.Name, block.FenceLength, block.HasClosingLine)),
            written.AllBlocks.Select(block => (block.Kind, block.Head?.Name, block.FenceLength, block.HasClosingLine)));
        Assert.All(written.AllBlocks, block => Assert.False(block.HasClosingLine));
    }

    [Theory]
    [InlineData(":: Demo.Card Title=Old\n```json\n{ \"Tags\": [1] }", NewTitleAndTags, ":: Demo.Card Title=New\n" + TagsTwo)]
    [InlineData(":: Demo.Card Title=Old\n```json\n{ \"Tags\": [1] }\n", NewTitleAndTags, ":: Demo.Card Title=New\n" + TagsTwo + "\n")]
    [InlineData(":: Demo.Card Title=Old\n```json\n{ \"Tags\": [1] }", NewTitle, ":: Demo.Card Title=New")]
    [InlineData(":: Demo.Card Title=Old\n```json\n{ \"Tags\": [1] }\n", NewTitle, ":: Demo.Card Title=New\n")]
    [InlineData(":: Demo.Card Title=Old\n```json", NewTitleAndTags, ":: Demo.Card Title=New\n" + TagsTwo)]
    [InlineData(":: Demo.Card Title=Old\r\n```json\r\n{ \"Tags\": [1] }\r\n", NewTitle, ":: Demo.Card Title=New\r\n")]
    public void WhenJsonFenceOfABlockWithoutClosingLineIsNotClosed_ThenItIsWrittenAsAFenceThatIsClosed(
        string source, string configurationJson, string expected)
    {
        // Arrange
        var card = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(source, card, configurationJson);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenJsonFenceIsNotClosedOnAPageWithWindowsLineEndings_ThenItsSpanEndsBeforeTheLineEndingAndTheWriteKeepsTheLineEndings()
    {
        // Arrange
        const string source = "before\r\n\r\n:: Demo.Card Title=Old\r\n```json\r\n{ \"Tags\": [1] }\r\n";
        var page = MarkdownPageParser.Parse(source);
        var card = Assert.Single(page.AllBlocks);
        var lastCharacter = source.LastIndexOf('}');

        // Act
        var replaced = MarkdownBlockWriter.WriteConfiguration(source, card, NewTitleAndTags);
        var removed = MarkdownBlockWriter.WriteConfiguration(source, card, NewTitle);

        // Assert
        var span = card.ConfigurationSpan!.Value;
        Assert.Equal("```json\r\n{ \"Tags\": [1] }", source.Substring(span.Start, span.Length));
        Assert.True(page.IsInsideCode(lastCharacter));
        Assert.False(page.IsInsideCode(lastCharacter + 1));

        Assert.Equal($"before\r\n\r\n:: Demo.Card Title=New\r\n{TagsTwo.Replace("\n", "\r\n")}\r\n", replaced);
        Assert.Equal("before\r\n\r\n:: Demo.Card Title=New\r\n", removed);
        AssertOnlyWindowsLineEndings(replaced);
        AssertOnlyWindowsLineEndings(removed);
    }

    [Fact]
    public void WhenBlockIsCreated_ThenItIsAClosedBlockWithAFenceOfTheGivenLength()
    {
        // Act
        var block = MarkdownBlockWriter.CreateBlock(
            "Demo.Motor", "m1", """{ "$type": "Demo.Motor", "Name": "My Motor", "Speed": 3 }""", fenceLength: 2);

        // Assert
        Assert.Equal(":: Demo.Motor#m1 Name=\"My Motor\" Speed=3\n::", block);
    }

    [Fact]
    public void WhenBlockIsCreatedForTheInsideOfABlock_ThenBothOfItsLinesHaveTheLongerFence()
    {
        // Act
        var block = MarkdownBlockWriter.CreateBlock("Demo.Motor", null, """{ "Speed": 3 }""", fenceLength: 4);

        // Assert
        Assert.Equal(":::: Demo.Motor Speed=3\n::::", block);
    }

    [Fact]
    public void WhenBlockIsCreatedWithAFenceShorterThanTheMinimum_ThenItThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MarkdownBlockWriter.CreateBlock("Demo.Motor", null, "{}", fenceLength: 1));
    }

    [Fact]
    public void WhenCreatedBlockHasComplexValue_ThenItContainsAJsonFence()
    {
        // Act
        var block = MarkdownBlockWriter.CreateBlock("Demo.Grid", null, """{ "Rows": 1, "Cells": [] }""", fenceLength: 3);

        // Assert
        Assert.Equal("::: Demo.Grid Rows=1\n```json\n{\n  \"Cells\": []\n}\n```\n:::", block);
    }

    [Fact]
    public void WhenStringContainsLineBreak_ThenItGoesIntoTheJsonFence()
    {
        // Arrange
        var motorBlock = Motor;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(
            Source, motorBlock, """{ "Speed": 5, "Description": "first\nsecond" }""");

        // Assert
        var motor = MarkdownPageParser.Parse(result).Blocks[0].Children[0];
        Assert.Null(motor.Error);
        Assert.Equal(":::: Demo.Motor Speed=5", result.Substring(motor.HeadSpan.Start, motor.HeadSpan.Length));
        var configuration = JsonNode.Parse(motor.ConfigurationJson!)!.AsObject();
        Assert.Equal("first\nsecond", (string?)configuration["Description"]);
    }

    [Fact]
    public void WhenKeyIsNotAValidHeadKey_ThenTheScalarGoesIntoTheJsonFence()
    {
        // Arrange
        var motorBlock = Motor;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, motorBlock, """{ "Speed": 5, "my-key": 7 }""");

        // Assert
        var motor = MarkdownPageParser.Parse(result).Blocks[0].Children[0];
        Assert.Null(motor.Error);
        Assert.Equal(":::: Demo.Motor Speed=5", result.Substring(motor.HeadSpan.Start, motor.HeadSpan.Length));
        var configuration = JsonNode.Parse(motor.ConfigurationJson!)!.AsObject();
        Assert.Equal(7, (int?)configuration["my-key"]);
    }

    [Fact]
    public void WhenStringContainsQuoteAndBackslash_ThenItRoundTripsThroughTheHeadLine()
    {
        // Arrange
        var motorBlock = Motor;
        const string value = "say \"hi\" \\ bye";
        var configurationJson = new JsonObject { ["Speed"] = 5, ["Label"] = value }.ToJsonString();

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, motorBlock, configurationJson);

        // Assert
        var motor = MarkdownPageParser.Parse(result).Blocks[0].Children[0];
        Assert.Null(motor.Error);
        Assert.Equal(value, motor.Head!.Arguments.Single(argument => argument.Key == "Label").Value);
    }

    private static void AssertOnlyWindowsLineEndings(string text)
    {
        Assert.Contains("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
        Assert.DoesNotContain("\r", text.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void WhenPageHasWindowsLineEndingsAndAFenceIsAdded_ThenTheResultHasOnlyWindowsLineEndings()
    {
        // Arrange
        var source = Source.Replace("\n", "\r\n");
        var card = MarkdownPageParser.Parse(source).Blocks[0];

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(source, card, """{ "Title": "New", "Cells": [1, 2] }""");

        // Assert
        AssertOnlyWindowsLineEndings(result);
        var written = MarkdownPageParser.Parse(result).Blocks[0];
        Assert.Equal("New", Assert.Single(written.Head!.Arguments).Value);
        Assert.Equal("{\"Cells\":[1,2]}", written.ConfigurationJson!.Replace(" ", "").Replace("\r\n", ""));
    }

    [Fact]
    public void WhenPageHasWindowsLineEndingsAndAFenceIsReplaced_ThenTheResultHasOnlyWindowsLineEndings()
    {
        // Arrange
        var source = Source.Replace("\n", "\r\n");
        var motorBlock = MarkdownPageParser.Parse(source).Blocks[0].Children[0];

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(source, motorBlock, """{ "Speed": 5, "Cells": [3, 4] }""");

        // Assert
        AssertOnlyWindowsLineEndings(result);
        var written = MarkdownPageParser.Parse(result).Blocks[0].Children[0];
        Assert.Equal("{\"Cells\":[3,4]}", written.ConfigurationJson!.Replace(" ", "").Replace("\r\n", ""));
    }

    [Fact]
    public void WhenCreatedBlockIsInsertedIntoPageWithWindowsLineEndings_ThenTheResultHasOnlyWindowsLineEndings()
    {
        // Arrange
        const string source = "before\r\n\r\nafter\r\n";
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source);

        // Act
        var block = MarkdownBlockWriter.CreateBlock("Demo.Grid", "grid", """{ "Rows": 2, "Cells": [1] }""", 2, lineEnding);
        var result = source.Insert("before\r\n\r\n".Length, block + lineEnding + lineEnding);

        // Assert
        AssertOnlyWindowsLineEndings(result);
        var written = Assert.Single(MarkdownPageParser.Parse(result).Blocks);
        Assert.Equal("grid", written.Head!.Id);
        Assert.Equal("{\"Cells\":[1]}", written.ConfigurationJson!.Replace(" ", "").Replace("\r\n", ""));
    }

    [Fact]
    public void WhenPageHasUnixLineEndingsAndAFenceIsAdded_ThenTheResultHasNoCarriageReturn()
    {
        // Arrange
        var card = Card;

        // Act
        var result = MarkdownBlockWriter.WriteConfiguration(Source, card, """{ "Title": "New", "Cells": [1, 2] }""");

        // Assert
        Assert.DoesNotContain("\r", result);
    }

    [Theory]
    [InlineData("one\r\ntwo\r\n", 0, "\r\n")]
    [InlineData("one\ntwo\n", 0, "\n")]
    [InlineData("one line", 0, "\n")]
    [InlineData("", 0, "\n")]
    [InlineData("one\ntwo\r\nthree", 4, "\r\n")]
    [InlineData("one\r\ntwo", 5, "\r\n")]
    public void WhenLineEndingIsAskedFor_ThenItIsTheOneOfTheLineAtTheOffsetOrTheFirstOfTheText(
        string source, int offset, string expected)
    {
        // Act
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, offset);

        // Assert
        Assert.Equal(expected, lineEnding);
    }

    [Theory]
    [InlineData("intro\ra\n", true)]
    [InlineData("intro\r", true)]
    [InlineData("\r\r\n", true)]
    [InlineData("one\r\ntwo\r\n", false)]
    [InlineData("one\ntwo", false)]
    [InlineData("", false)]
    public void WhenTextHasACarriageReturnWithoutALineFeedAfterIt_ThenItHasALoneCarriageReturn(string source, bool expected)
    {
        // Act
        var hasLoneCarriageReturn = MarkdownBlockWriter.HasLoneCarriageReturn(source);

        // Assert
        Assert.Equal(expected, hasLoneCarriageReturn);
    }

    [Fact]
    public void WhenPageHasALoneCarriageReturnBeforeTheOwner_ThenMarkdigCountsALineThatTheTextFunctionsDoNotCount()
    {
        // Arrange
        const string source = "intro\ra\n\n:: Demo.Board#board Columns=2\n::: Tile Position=1\none\n:::\n::\n";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert: the head line of the board is the third line by line feeds, and the fourth for Markdig.
        Assert.True(MarkdownBlockWriter.HasLoneCarriageReturn(source));
        Assert.Equal(3, source[..page.Blocks[0].HeadSpan.Start].Count(character => character == '\n') + 1);
        Assert.Equal(4, page.Blocks[0].Line);
    }

    private const string Board =
        "intro\n\n:: Demo.Board#board Columns=2\n::: Tile Position=1\none\n:::\n::: Tile Position=2\ntwo\n:::\n::\n\noutro\n";

    private const string ShortBoard =
        "intro\n\n:: Demo.Board#board Columns=2\n::: Tile Position=1\none\n::: Tile Position=2\ntwo\n::\n\noutro\n";

    private static MarkdownBlock OwnerOf(string source) => MarkdownPageParser.Parse(source).Blocks[0];

    [Fact]
    public void WhenBlockWithClosingLineIsRemoved_ThenItsLinesAreGoneAndTheRestIsUnchanged()
    {
        // Arrange
        var first = OwnerOf(Board).Children[0];

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(Board, first, out var edit);

        // Assert
        Assert.Equal("intro\n\n:: Demo.Board#board Columns=2\n::: Tile Position=2\ntwo\n:::\n::\n\noutro\n", result);
        Assert.Equal(new MarkdownLineEdit(4, 3, 0), edit);
    }

    [Fact]
    public void WhenBlockWithoutClosingLineIsRemoved_ThenItsHeadAndContentAreGone()
    {
        // Arrange
        var second = OwnerOf(ShortBoard).Children[1];

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(ShortBoard, second, out var edit);

        // Assert
        Assert.Equal("intro\n\n:: Demo.Board#board Columns=2\n::: Tile Position=1\none\n::\n\noutro\n", result);
        Assert.Equal(new MarkdownLineEdit(6, 2, 0), edit);
    }

    [Fact]
    public void WhenRemovedBlockHoldsOtherBlocks_ThenTheyAreRemovedWithIt()
    {
        // Arrange
        const string source = ":: Demo.Board\n::: Tile\n:::: Demo.Leaf Count=1\n::::\ntext\n:::\n::: Tile\n:::\n::\n";
        var first = OwnerOf(source).Children[0];

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(source, first, out _);

        // Assert
        Assert.Equal(":: Demo.Board\n::: Tile\n:::\n::\n", result);
    }

    [Fact]
    public void WhenRemovedBlockEndsTheTextWithoutLineEnding_ThenTheTextEndsBeforeIt()
    {
        // Arrange
        const string source = "intro\n:: Demo.Board\n::: Tile\none";
        var tile = OwnerOf(source).Children[0];

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(source, tile, out _);

        // Assert
        Assert.Equal("intro\n:: Demo.Board\n", result);
    }

    [Fact]
    public void WhenBlockIsRemovedFromTextWithWindowsLineEndings_ThenTheOtherLineEndingsStay()
    {
        // Arrange
        var source = Board.Replace("\n", "\r\n");
        var first = OwnerOf(source).Children[0];

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(source, first, out _);

        // Assert
        Assert.Equal(
            "intro\r\n\r\n:: Demo.Board#board Columns=2\r\n::: Tile Position=2\r\ntwo\r\n:::\r\n::\r\n\r\noutro\r\n", result);
    }

    [Theory]
    [InlineData("> ::: {.note}\n> styled", "text of the panel")]
    [InlineData("- first\n- second", "text of the panel")]
    [InlineData("a paragraph", "text of the panel")]
    // A fence before text that starts no head line is text for the parser.
    [InlineData("a paragraph", ":: 12:30 lunch")]
    [InlineData(":: 12:30 lunch", "text of the panel")]
    public void WhenRemovalWouldJoinTheTextBeforeAndAfterTheBlock_ThenAnEmptyLineStaysInItsPlace(string textBefore, string textAfter)
    {
        // Arrange
        var source = $":: Demo.Panel\n{textBefore}\n::: Tile\ntwo\n:::\n{textAfter}\n::\n";
        var tile = OwnerOf(source).Children.Single(child => child.Kind == MarkdownBlockKind.Slot);

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(source, tile, out var edit);

        // Assert
        var removedLine = textBefore.Count(character => character == '\n') + 3;
        Assert.Equal($":: Demo.Panel\n{textBefore}\n\n{textAfter}\n::\n", result);
        Assert.Equal(new MarkdownLineEdit(removedLine, 3, 1), edit);
    }

    [Theory]
    [InlineData(":: Demo.Panel\n::: Tile\ntwo\n:::\ntext of the panel\n::\n", ":: Demo.Panel\ntext of the panel\n::\n")]
    [InlineData(":: Demo.Panel\ntext before\n::: Tile\ntwo\n:::\n::\n", ":: Demo.Panel\ntext before\n::\n")]
    [InlineData(":: Demo.Panel\ntext before\n\n::: Tile\ntwo\n:::\ntext after\n::\n", ":: Demo.Panel\ntext before\n\ntext after\n::\n")]
    [InlineData(":: Demo.Panel\ntext before\n::: Tile\ntwo\n:::\n\ntext after\n::\n", ":: Demo.Panel\ntext before\n\ntext after\n::\n")]
    [InlineData(":: Demo.Panel\ntext before\n::: Tile\ntwo\n:::\n  \ntext after\n::\n", ":: Demo.Panel\ntext before\n  \ntext after\n::\n")]
    public void WhenRemovalJoinsNoTwoTexts_ThenNoEmptyLineIsLeft(string source, string expected)
    {
        // Arrange
        var tile = OwnerOf(source).Children.Single(child => child.Kind == MarkdownBlockKind.Slot);

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(source, tile, out var edit);

        // Assert
        Assert.Equal(expected, result);
        Assert.Equal(0, edit.InsertedLineCount);
    }

    [Theory]
    [InlineData(":: Demo.Leaf\n::\ntext after\n", "text after\n")]
    [InlineData("text before\n:: Demo.Leaf", "text before\n")]
    public void WhenRemovedBlockStartsOrEndsTheText_ThenNoEmptyLineIsLeft(string source, string expected)
    {
        // Arrange
        var leaf = OwnerOf(source);

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(source, leaf, out var edit);

        // Assert
        Assert.Equal(expected, result);
        Assert.Equal(0, edit.InsertedLineCount);
    }

    [Theory]
    [InlineData("    :: Demo.Leaf", 1)]
    [InlineData("\t:::", 1)]
    [InlineData("   :: Demo.Leaf", 0)]
    public void WhenLineAfterTheBlockIsAFenceAfterFourColumnsOfIndentation_ThenItIsTextAndAnEmptyLineStays(
        string lineAfter, int expectedEmptyLineCount)
    {
        // Arrange: four columns of indentation make the line code, and code continues a paragraph before it.
        var source = $":: Demo.Panel\na paragraph\n::: Tile\n:::\n{lineAfter}\n::\n";
        var tile = OwnerOf(source).Children.First(child => child.Kind == MarkdownBlockKind.Slot);

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(source, tile, out var edit);

        // Assert
        var emptyLine = expectedEmptyLineCount == 1 ? "\n" : string.Empty;
        Assert.Equal($":: Demo.Panel\na paragraph\n{emptyLine}{lineAfter}\n::\n", result);
        Assert.Equal(new MarkdownLineEdit(3, 2, expectedEmptyLineCount), edit);
    }

    [Theory]
    [InlineData(":: 12:30 lunch")]
    [InlineData("::: :-)")]
    [InlineData(":: :")]
    [InlineData(":: 9 lives")]
    [InlineData("::")]
    [InlineData(":::")]
    [InlineData("::::")]
    [InlineData("::: Tile")]
    [InlineData(":::Tile")]
    [InlineData(":: _name")]
    [InlineData("::: {.note}")]
    [InlineData(":: {")]
    [InlineData(":: \u00C4rger")]
    [InlineData(": a definition")]
    [InlineData(" :: Demo.Leaf")]
    [InlineData("   ::: 12:30")]
    [InlineData("\u00A0:: Demo.Leaf")]
    [InlineData("::\u00A0")]
    [InlineData("::\u00A0Tile")]
    [InlineData(":: \u000B")]
    [InlineData("\u000B:: Demo.Leaf")]
    [InlineData("::\u000C")]
    [InlineData("::\u2003Tile")]
    [InlineData(":: \t")]
    [InlineData("\u00A0")]
    public void WhenLineAfterTheBlockIsNotEmpty_ThenAnEmptyLineStaysExactlyWhenTheParserReadsTheLineAsText(string lineAfter)
    {
        // Arrange: the parser opens or closes a block on a fence line, and on no other line.
        var source = $":: Demo.Panel\na paragraph\n::: Tile\n:::\n{lineAfter}\n";
        var page = MarkdownPageParser.Parse(source);
        var tile = page.Blocks[0].Children.First(child => child.Kind == MarkdownBlockKind.Slot);
        var isFenceLine = page.Document.Descendants<MarkdownBlockContainer>().Any(container =>
            container.Line == 4 || (container.HasClosingLine && container.EndLine == 4));

        // Act
        MarkdownBlockWriter.RemoveBlock(source, tile, out var edit);

        // Assert
        Assert.Equal(isFenceLine ? 0 : 1, edit.InsertedLineCount);
    }

    [Fact]
    public void WhenRemovalLeavesAnEmptyLineInTextWithWindowsLineEndings_ThenItHasThatLineEnding()
    {
        // Arrange
        const string source = ":: Demo.Panel\r\na paragraph\r\n::: Tile\r\n:::\r\ntext of the panel\r\n::\r\n";
        var tile = OwnerOf(source).Children.Single(child => child.Kind == MarkdownBlockKind.Slot);

        // Act
        var result = MarkdownBlockWriter.RemoveBlock(source, tile, out _);

        // Assert
        Assert.Equal(":: Demo.Panel\r\na paragraph\r\n\r\ntext of the panel\r\n::\r\n", result);
    }

    [Fact]
    public void WhenSiblingHasClosingLine_ThenPlacementIsAfterItWithClosingLine()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(Board);

        // Act
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(page, page.Blocks[0], "Tile");

        // Assert
        Assert.Equal(new MarkdownBlockPlacement(10, 3, true, string.Empty, "\n"), placement);
    }

    [Fact]
    public void WhenSiblingHasNoClosingLine_ThenPlacementIsAfterItsContentWithoutClosingLine()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(ShortBoard);

        // Act
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(page, page.Blocks[0], "Tile");

        // Assert
        Assert.Equal(new MarkdownBlockPlacement(8, 3, false, string.Empty, "\n"), placement);
    }

    [Theory]
    [InlineData(":: Demo.Board Columns=2\n::\n", 2, true)]
    [InlineData(":: Demo.Board Columns=2\n```json\n{ \"Tags\": [1] }\n```\n::\n", 5, true)]
    [InlineData(":: Demo.Board Columns=2\n", 2, false)]
    [InlineData(":: Demo.Board Columns=2\n```json\n{ \"Tags\": [1] }\n```\n\n:: Demo.Other\n", 5, false)]
    [InlineData(":: Demo.Card\n::: Demo.Leaf\n::\n\n:: Demo.Board Columns=2\n::\n", 6, false)]
    public void WhenOwnerHasNoBlockOfTheSlot_ThenPlacementIsAtTheEndOfItsContentInTheStyleOfThePage(
        string source, int expectedLine, bool expectedClosingLine)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);
        var owner = page.Blocks.Single(block => block.Head!.Name == "Demo.Board");

        // Act
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(page, owner, "Tile");

        // Assert
        Assert.Equal(expectedLine, placement.Line);
        Assert.Equal(expectedClosingLine, placement.HasClosingLine);
    }

    [Fact]
    public void WhenOwnerIsIndentedAndUsesWindowsLineEndings_ThenPlacementCarriesBoth()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("  :: Demo.Board\r\n  ::\r\n");

        // Act
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(page, page.Blocks[0], "Tile");

        // Assert
        Assert.Equal(new MarkdownBlockPlacement(2, 3, true, "  ", "\r\n"), placement);
    }

    [Fact]
    public void WhenSiblingIsIndentedAndUsesWindowsLineEndings_ThenPlacementIsWrittenLikeTheSibling()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Board\r\n  :::: Tile\r\n  ::::\r\n::\r\n");

        // Act
        var placement = MarkdownBlockWriter.GetSlotBlockPlacement(page, page.Blocks[0], "Tile");

        // Assert
        Assert.Equal(new MarkdownBlockPlacement(4, 4, true, "  ", "\r\n"), placement);
    }

    [Fact]
    public void WhenLinesAreInsertedBeforeALine_ThenTheyStandThereWithALineEnding()
    {
        // Act
        var result = MarkdownBlockWriter.InsertLines("a\nb\n", 2, "x\ny", "\n", out var edit);

        // Assert
        Assert.Equal("a\nx\ny\nb\n", result);
        Assert.Equal(new MarkdownLineEdit(2, 0, 2), edit);
    }

    [Theory]
    [InlineData("a\nb\n", 3, "a\nb\nx\n", 3)]
    [InlineData("a\nb", 3, "a\nb\nx", 3)]
    [InlineData("a\nb", 9, "a\nb\nx", 3)]
    [InlineData("", 1, "x\n", 1)]
    public void WhenLinesAreInsertedAfterTheLastLine_ThenTheyAreAppended(
        string source, int line, string expected, int expectedLine)
    {
        // Act
        var result = MarkdownBlockWriter.InsertLines(source, line, "x", "\n", out var edit);

        // Assert
        Assert.Equal(expected, result);
        Assert.Equal(new MarkdownLineEdit(expectedLine, 0, 1), edit);
    }

    [Fact]
    public void WhenBlockIsCreatedWithoutClosingLineAndWithContent_ThenItEndsWithItsContent()
    {
        // Act
        var block = MarkdownBlockWriter.CreateBlock(
            "Tile", null, """{ "Position": 3 }""", fenceLength: 3, hasClosingLine: false, content: ":::: Demo.Leaf Count=1");

        // Assert
        Assert.Equal("::: Tile Position=3\n:::: Demo.Leaf Count=1", block);
    }

    [Fact]
    public void WhenBlockIsCreatedWithJsonAndContent_ThenTheJsonStandsBeforeTheContent()
    {
        // Act
        var block = MarkdownBlockWriter.CreateBlock(
            "Tile", null, """{ "Position": 3, "Tags": [1] }""", fenceLength: 3, content: "text");

        // Assert
        Assert.Equal("::: Tile Position=3\n```json\n{\n  \"Tags\": [\n    1\n  ]\n}\n```\ntext\n:::", block);
    }

    [Fact]
    public void WhenTextIsIndented_ThenEveryLineStartsWithTheIndentation()
    {
        // Act
        var result = MarkdownBlockWriter.Indent("a\r\nb", "  ", "\r\n");

        // Assert
        Assert.Equal("  a\r\n  b", result);
    }
}
