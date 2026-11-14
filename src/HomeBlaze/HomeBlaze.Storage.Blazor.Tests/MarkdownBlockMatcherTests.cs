using HomeBlaze.Storage.Blazor.Models;

namespace HomeBlaze.Storage.Blazor.Tests;

public class MarkdownBlockMatcherTests
{
    private const string TwoMotors = "# Page\n\n:: Demo.Motor#first Speed=1\n::\n\nText\n\n:: Demo.Motor#second Speed=2\n::\n\nEnd\n";

    private const string Grid = "# Page\n\n:: Demo.Grid#grid Rows=1\n```json\n{ \"Cells\": [] }\n```\n::\n\nEnd\n";

    [Fact]
    public void WhenTextsAreIdentical_ThenTheBlockIsFound()
    {
        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(TwoMotors, TwoMotors, 1);

        // Assert
        Assert.NotNull(block);
        Assert.Equal("second", block.Head!.Id);
        Assert.Equal(":: Demo.Motor#second Speed=2", TwoMotors.Substring(block.HeadSpan.Start, block.HeadSpan.Length));
    }

    [Fact]
    public void WhenUnsavedTextFollowsTheBlock_ThenTheBlockIsFound()
    {
        // Arrange
        var editorText = TwoMotors.Replace("End\n", "Changed end\n\n:: Demo.Motor#third Speed=3\n::\n");

        // Act
        var first = MarkdownBlockMatcher.FindSavedBlock(editorText, TwoMotors, 0);
        var second = MarkdownBlockMatcher.FindSavedBlock(editorText, TwoMotors, 1);

        // Assert
        Assert.Equal("first", first?.Head?.Id);
        Assert.Equal("second", second?.Head?.Id);
    }

    [Theory]
    [InlineData("# Page\n\nTyped above\n\n")]
    [InlineData("# Page\n\n:: Demo.Motor#typed Speed=9\n::\n\n")]
    public void WhenUnsavedTextIsTypedAboveTheBlock_ThenTheBlockIsRefused(string newStart)
    {
        // Arrange
        var editorText = TwoMotors.Replace("# Page\n\n", newStart);

        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(editorText, TwoMotors, 0);

        // Assert
        Assert.Null(block);
    }

    [Fact]
    public void WhenFirstOfTwoBlocksWithoutIdIsDeletedInTheEditor_ThenTheRemainingBlockIsRefused()
    {
        // Arrange
        const string savedText = ":: Demo.Motor Speed=1\n::\n\n:: Demo.Motor Speed=2\n::\n";
        const string editorText = ":: Demo.Motor Speed=2\n::\n";

        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(editorText, savedText, 0);

        // Assert
        Assert.Null(block);
    }

    [Fact]
    public void WhenTwoBlocksWithoutIdAreSwappedInTheEditor_ThenBothAreRefused()
    {
        // Arrange
        const string savedText = ":: Demo.Motor Speed=1\n::\n\n:: Demo.Motor Speed=2\n::\n";
        const string editorText = ":: Demo.Motor Speed=2\n::\n\n:: Demo.Motor Speed=1\n::\n";

        // Act
        var first = MarkdownBlockMatcher.FindSavedBlock(editorText, savedText, 0);
        var second = MarkdownBlockMatcher.FindSavedBlock(editorText, savedText, 1);

        // Assert
        Assert.Null(first);
        Assert.Null(second);
    }

    [Theory]
    [InlineData(":: Demo.Motor#first Speed=5")]
    [InlineData(":: Demo.Motor#first")]
    [InlineData(":: Demo.Motor#first Speed=1 Name=Typed")]
    public void WhenHeadLineOfTheBlockHasUnsavedChange_ThenOnlyTheMatchAfterWriteBackFindsIt(string editorHead)
    {
        // Arrange
        var editorText = TwoMotors.Replace(":: Demo.Motor#first Speed=1", editorHead);

        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(editorText, TwoMotors, 0);
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(editorText, TwoMotors, 0);

        // Assert
        Assert.Null(block);
        Assert.Equal("first", blockAfterWriteBack?.Head?.Id);
    }

    [Fact]
    public void WhenHeadLineOfAnEarlierBlockHasUnsavedChange_ThenALaterBlockIsRefusedByBothMatches()
    {
        // Arrange
        var editorText = TwoMotors.Replace("Speed=1", "Speed=5");

        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(editorText, TwoMotors, 1);
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(editorText, TwoMotors, 1);

        // Assert
        Assert.Null(block);
        Assert.Null(blockAfterWriteBack);
    }

    [Theory]
    [InlineData("# Page\n\n:: Demo.Grid#grid Rows=1\n```json\n{ \"Cells\": [1] }\n```\n::\n\nEnd\n")]
    [InlineData("# Page\n\n:: Demo.Grid#grid Rows=1\n::\n\nEnd\n")]
    [InlineData("# Page\n\n:: Demo.Grid#grid Rows=1\n```json\n{ \"Cells\": [] }\n```x\n```\n::\n\nEnd\n")]
    public void WhenJsonFenceOfTheBlockHasUnsavedChange_ThenOnlyTheMatchAfterWriteBackFindsIt(string editorText)
    {
        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(editorText, Grid, 0);
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(editorText, Grid, 0);

        // Assert
        Assert.Null(block);
        Assert.Equal("grid", blockAfterWriteBack?.Head?.Id);
    }

    [Fact]
    public void WhenJsonFenceIsAddedInTheEditor_ThenTheBlockIsRefused()
    {
        // Arrange
        const string savedText = "# Page\n\n:: Demo.Grid#grid Rows=1\n::\n\nEnd\n";

        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(Grid, savedText, 0);

        // Assert
        Assert.Null(block);
    }

    [Fact]
    public void WhenBlockHasJsonFenceAndTextsAreIdentical_ThenTheBlockIsFound()
    {
        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(Grid, Grid, 0);

        // Assert
        Assert.NotNull(block?.ConfigurationSpan);
    }

    [Theory]
    [InlineData(TwoMotors, ":: Demo.Motor#first Speed=1\n::\n", 1)]
    [InlineData("# Page\n\n:: Demo.Motor#first Speed=1\n::\n", TwoMotors, 1)]
    [InlineData(TwoMotors, TwoMotors, 2)]
    [InlineData(TwoMotors, TwoMotors, -1)]
    [InlineData(TwoMotors, "", 0)]
    [InlineData(TwoMotors, null, 0)]
    public void WhenIndexIsOutOfRangeOnEitherSide_ThenTheBlockIsRefusedByBothMatches(
        string editorText, string? savedText, int blockIndex)
    {
        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(editorText, savedText, blockIndex);
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(editorText, savedText, blockIndex);

        // Assert
        Assert.Null(block);
        Assert.Null(blockAfterWriteBack);
    }

    [Theory]
    [InlineData(":: Demo.Motor Speed=1\ntext\n")]
    [InlineData(":: Demo.Motor Speed=1")]
    public void WhenBlockHasNoClosingLineAndTextsAreIdentical_ThenItIsFoundByBothMatches(string text)
    {
        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(text, text, 0);
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(text, text, 0);

        // Assert
        Assert.Equal("Demo.Motor", block?.Head?.Name);
        Assert.Equal("Demo.Motor", blockAfterWriteBack?.Head?.Name);
    }

    private const string ShortForm =
        "# Page\n\n:: Demo.Flow\n::: Item\n:::: Demo.Card#card Title=A\n::::: Demo.Motor#inner Speed=1\n::: Item\n:::: Demo.Motor#last Speed=2\n";

    [Theory]
    [InlineData(2, "card")]
    [InlineData(3, "inner")]
    [InlineData(5, "last")]
    public void WhenBlocksAreNestedWithoutClosingLinesAndTextsAreIdentical_ThenTheBlockIsFound(int blockIndex, string expectedId)
    {
        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(ShortForm, ShortForm, blockIndex);

        // Assert
        Assert.Equal(expectedId, block?.Head?.Id);
        Assert.Equal(blockIndex, block?.Index);
    }

    [Fact]
    public void WhenWriteBackChangedTheHeadLineOfABlockWithoutClosingLine_ThenTheMatchAfterWriteBackFindsIt()
    {
        // Arrange
        var savedText = ShortForm.Replace("Demo.Motor#inner Speed=1", "Demo.Motor#inner Speed=5");

        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(ShortForm, savedText, 3);
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(ShortForm, savedText, 3);

        // Assert
        Assert.Null(block);
        Assert.Equal("inner", blockAfterWriteBack?.Head?.Id);
        Assert.Equal("::::: Demo.Motor#inner Speed=1", ShortForm.Substring(blockAfterWriteBack!.HeadSpan.Start, blockAfterWriteBack.HeadSpan.Length));
    }

    [Theory]
    // Text that is inside the block, because nothing ended the block before it.
    [InlineData("::::: Demo.Motor#inner Speed=1\n", "::::: Demo.Motor#inner Speed=1\nTyped text\n")]
    // A closing line for the block.
    [InlineData("::::: Demo.Motor#inner Speed=1\n", "::::: Demo.Motor#inner Speed=1\n:::::\n")]
    public void WhenUnsavedTextFollowsTheHeadLineOfABlockWithoutClosingLine_ThenTheBlockIsFound(string savedPart, string editorPart)
    {
        // Arrange
        var editorText = ShortForm.Replace(savedPart, editorPart);

        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(editorText, ShortForm, 3);

        // Assert
        Assert.Equal("inner", block?.Head?.Id);
    }

    [Fact]
    public void WhenAHeadLineIsTypedAboveABlockWithoutClosingLine_ThenTheBlockIsRefusedByBothMatches()
    {
        // Arrange
        var editorText = ShortForm.Replace("::::: Demo.Motor#inner", ":::: Demo.Card#typed\n::::: Demo.Motor#inner");

        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(editorText, ShortForm, 4);
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(editorText, ShortForm, 4);

        // Assert
        Assert.Null(block);
        Assert.Null(blockAfterWriteBack);
    }

    [Theory]
    [InlineData(":: Demo.Motor Speed=\"1\ntext\n::\n")]
    [InlineData("::\ntext\n")]
    public void WhenBlockIsInvalid_ThenItIsRefusedByBothMatches(string text)
    {
        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(text, text, 0);
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(text, text, 0);

        // Assert
        Assert.Null(block);
        Assert.Null(blockAfterWriteBack);
    }

    [Theory]
    [InlineData(":: Demo.Motor#other Speed=1")]
    [InlineData(":: Demo.Pump#first Speed=1")]
    [InlineData(":: Demo.Motor Speed=1")]
    [InlineData(":: first Speed=1")]
    public void WhenNameOrIdOfTheBlockDiffers_ThenTheMatchAfterWriteBackRefusesIt(string editorHead)
    {
        // Arrange
        var editorText = TwoMotors.Replace(":: Demo.Motor#first Speed=1", editorHead);

        // Act
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(editorText, TwoMotors, 0);

        // Assert
        Assert.Null(blockAfterWriteBack);
    }

    [Fact]
    public void WhenWriteBackAddedTheSpaceAfterTheFence_ThenTheMatchAfterWriteBackFindsTheBlock()
    {
        // Arrange
        const string editorText = "# Page\n\n::Demo.Card#card\n::::Demo.Motor#inner Speed=1\n::::\n::\n";
        const string savedText = "# Page\n\n::Demo.Card#card\n:::: Demo.Motor#inner Speed=5\n::::\n::\n";

        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(editorText, savedText, 1);
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(editorText, savedText, 1);

        // Assert
        Assert.Null(block);
        Assert.Equal("inner", blockAfterWriteBack?.Head?.Id);
        Assert.Equal(4, blockAfterWriteBack?.FenceLength);
    }

    [Fact]
    public void WhenBlockIsInsideAnotherBlockAndTextsAreIdentical_ThenTheInnerBlockIsFound()
    {
        // Arrange
        const string text = "# Page\n\n:: Demo.Card#card\n::: Demo.Motor#inner Speed=1\n:::\n::\n";

        // Act
        var block = MarkdownBlockMatcher.FindSavedBlock(text, text, 1);

        // Assert
        Assert.NotNull(block);
        Assert.Equal("::: Demo.Motor#inner Speed=1", text.Substring(block.HeadSpan.Start, block.HeadSpan.Length));
    }

    [Fact]
    public void WhenTextBeforeTheBlockDiffers_ThenTheMatchAfterWriteBackRefusesIt()
    {
        // Arrange
        var editorText = TwoMotors.Replace("# Page", "# Pane");

        // Act
        var blockAfterWriteBack = MarkdownBlockMatcher.FindSavedBlockAfterWriteBack(editorText, TwoMotors, 0);

        // Assert
        Assert.Null(blockAfterWriteBack);
    }
}
